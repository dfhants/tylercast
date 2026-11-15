import CoreVideo
import IOKit.pwr_mgt
import Foundation

public struct MirrorOptions {
    public enum Audio: String, CaseIterable {
        /// A silent AC-3 track: the TV gets the format it lists, with no sound.
        case silentAC3 = "silent-ac3"
        /// The Mac's sound, AAC. The test TV doesn't list AAC with H.264 TS, but plays it
        /// (tried 2026-11-15 with AVC_TS_MP_HD_AAC_MULT5_ISO).
        case aac
        case none

        public var codec: AudioCodec { self == .aac ? .aac : .ac3 }
        public var capturesSound: Bool { self == .aac }
    }

    public var port: UInt16 = 8090
    public var marginPercent: Double = 0
    public var video = VideoSettings()
    public var muxRate = 20_000_000  // the test TV takes it; keyframes go out in half the time
    /// Capture-to-DTS slack. Frames whose last packet leaves later than this are "late".
    public var delay = 0.15  // 0.1 measured 31ms worst-case slack at 20M; 0.15 keeps heavier screens on time
    public var audio: Audio = .aac
    /// What is actually sent: the test pattern captures no sound, so it keeps
    /// the silent AC-3 track rather than an empty AAC one.
    public var effectiveAudio: Audio { testPattern && audio == .aac ? .silentAC3 : audio }
    public var testPattern = false
    /// Mute the Mac while mirroring, so sound comes from the TV only (Core Audio tap).
    public var soundOnTVOnly = true
    public var mime = DLNA.defaultMime
    public var sendProfile = true
    public var tlag = false
    public var sendMetadata = true
    public init() {}

    public var contentFeatures: String {
        DLNA.contentFeatures(profile: sendProfile ? DLNA.profile(for: effectiveAudio.codec) : nil)
    }
}

/// Source → encoder → muxer → pacer → broadcaster (→ HTTP server). Runs without
/// any TV, and before Play, so every connection the TV makes joins it.
public final class Pipeline {
    public let options: MirrorOptions
    public let broadcaster = Broadcaster()
    public let pacer: Pacer
    private var server: StreamServer?
    private var encoder: VideoEncoder?
    private var clock: FrameClock?
    private var capture: ScreenCapture?
    private let audio = SilentAudioSource()
    private var aac: AACEncoder?
    /// SystemAudioTap (macOS 14.2+), kept untyped for the availability check.
    private var tap: AnyObject?
    private var silenceTimer: DispatchSourceTimer?
    private var silenceTimerRunning = false
    private let frameLock = NSLock()
    private var sawFrame = false
    public var onFailure: ((String) -> Void)?

    public init(options: MirrorOptions) {
        self.options = options
        var mc = TSMuxer.Config()
        mc.muxRate = options.muxRate
        mc.includeAudio = options.effectiveAudio != .none
        mc.audioCodec = options.effectiveAudio.codec
        pacer = Pacer(muxer: TSMuxer(config: mc), broadcaster: broadcaster, delay: options.delay)
    }

    /// The stream's path while serving: random per pipeline, so it can't be guessed.
    public let streamPath = StreamServer.randomPath()

    /// `allowedPeer`: only that address may fetch the stream (the TV).
    /// `bindHost`: listen on that local address only.
    public func start(serve: Bool, allowedPeer: String? = nil, bindHost: String? = nil) async throws {
        if serve {
            var h = StreamHeaders()
            h.contentType = options.mime
            h.contentFeatures = options.contentFeatures
            h.tlag = options.tlag
            let s = StreamServer(port: options.port, path: streamPath, headers: h, broadcaster: broadcaster,
                                 allowedPeer: allowedPeer, bindHost: bindHost)
            s.onJoin = { [weak self] in self?.clock?.requestKeyframe() }
            do { try s.start() } catch {
                throw CaptureError(description: "cannot listen on port \(options.port): \(error) (another copy running?)")
            }
            server = s
        }

        let enc = try VideoEncoder(settings: options.video)
        enc.onAccessUnit = { [weak self] au, host, key in
            guard let self else { return }
            self.pacer.withMuxer { $0.enqueueVideo(annexB: au, pts90: self.pacer.pts90(forHost: host), keyframe: key) }
            self.frameLock.lock(); self.sawFrame = true; self.frameLock.unlock()
        }
        encoder = enc
        let fc = FrameClock(fps: options.video.fps, encoder: enc)
        clock = fc

        if options.effectiveAudio == .silentAC3 {
            pacer.onTick = { [weak self] now in
                guard let self else { return }
                self.audio.tick(host: now, pacer: self.pacer)
            }
            Log.info("Audio: silent AC-3 track" + (options.testPattern && options.audio == .aac ? " (test pattern: no sound to capture)" : ""))
        } else if options.effectiveAudio == .aac {
            // Preferred: a Core Audio tap that also mutes the Mac, so the sound
            // comes from the TV only. Fallback: ScreenCaptureKit's copy (Mac stays audible).
            var rate = Double(PCM.sampleRate)
            if options.soundOnTVOnly, #available(macOS 14.2, *) {
                let t = SystemAudioTap()
                do {
                    try t.start()
                    tap = t
                    rate = t.sampleRate
                } catch {
                    Log.warn("Audio: \(error); using ScreenCaptureKit's sound instead (the Mac stays audible)")
                }
            }
            let a = try AACEncoder(inputSampleRate: rate)
            a.onFrame = { [weak self] frame, host in
                guard let self else { return }
                self.pacer.withMuxer { $0.enqueueAudio(frame: frame, pts90: self.pacer.pts90(forHost: host)) }
            }
            aac = a
            if #available(macOS 14.2, *), let t = tap as? SystemAudioTap {
                t.onAudio = { samples, host in a.push(samples, host: host) }
            }
            let timer = DispatchSource.makeTimerSource(queue: DispatchQueue(label: "audio-silence", qos: .userInitiated))
            timer.schedule(deadline: .now(), repeating: .milliseconds(20))
            timer.setEventHandler { a.padSilence(upTo: Pacer.hostSeconds()) }
            silenceTimer = timer  // resumed once the pacer's clock runs
            Log.info("Audio: the Mac's sound, AAC-LC 48 kHz stereo \(a.bitrate / 1000)k (profile \(DLNA.profile(for: .aac)))")
        } else {
            Log.info("Audio: none (video-only TS)")
        }

        if options.testPattern {
            let tp = TestPattern(width: options.video.width, height: options.video.height)
            fc.render = { tp.frame($0) }
            Log.info("Source: test pattern")
        } else {
            let cap = ScreenCapture()
            cap.onFrame = { [weak fc] pb in fc?.push(pb) }
            if let a = aac, tap == nil {
                cap.capturesAudio = true
                cap.onAudio = { samples, host in a.push(samples, host: host) }
            }
            cap.onError = { [weak self] e in self?.onFailure?("capture stopped: \(e.localizedDescription)") }
            do {
                try await cap.start(settings: options.video, marginPercent: options.marginPercent)
            } catch {
                stop()
                throw error
            }
            capture = cap
        }
        pacer.start()
        if let t = silenceTimer, !silenceTimerRunning { t.resume(); silenceTimerRunning = true }
        fc.start()
        var got = false
        for _ in 0..<40 where !got {
            try? await Task.sleep(nanoseconds: 100_000_000)
            got = frameLock.withLock { sawFrame }
        }
        if !got {
            stop()
            throw CaptureError(description: options.testPattern
                ? "the encoder produced nothing in 4s"
                : "no picture from the screen in 4s: check Screen Recording permission (see README)")
        }
        Log.info("Encoder running; stream is live")
    }

    public func setMargin(_ m: Double) async { await capture?.setMargin(m) }

    public func stop() {
        if #available(macOS 14.2, *) { (tap as? SystemAudioTap)?.stop() }  // the Mac's speakers come back
        tap = nil
        if let t = silenceTimer {
            if !silenceTimerRunning { t.resume() }  // a suspended source must be resumed before cancel
            t.cancel()
        }
        silenceTimer = nil
        silenceTimerRunning = false
        clock?.stop()
        if let cap = capture { Task { await cap.stop() } }
        capture = nil
        encoder?.invalidate()
        encoder = nil
        pacer.stop()
        server?.stop()
        server = nil
    }
}

/// One mirroring session to one TV: pipeline up first, then SOAP Stop,
/// SetAVTransportURI, Play. Stop on the way out.
@MainActor
public final class MirrorSession {
    public enum State: Equatable {
        case stopped, connecting(String), playing(String), error(String)
        public var text: String {
            switch self {
            case .stopped: return "Stopped"
            case let .connecting(s): return "Connecting: \(s)"
            case let .playing(tv): return "Playing on \(tv)"
            case let .error(e): return "Error: \(e)"
            }
        }
    }

    public private(set) var state: State = .stopped {
        didSet {
            if isActive { keepDisplayAwake() } else { allowDisplaySleep() }
            onState?(state)
        }
    }
    private var awakeAssertion: IOPMAssertionID?

    /// While mirroring, the display mustn't idle-sleep: capture ends when it does.
    private func keepDisplayAwake() {
        guard awakeAssertion == nil else { return }
        var id = IOPMAssertionID(0)
        let r = IOPMAssertionCreateWithName(kIOPMAssertionTypePreventUserIdleDisplaySleep as CFString,
                                            IOPMAssertionLevel(kIOPMAssertionLevelOn),
                                            "TylerCast is mirroring the screen to a TV" as CFString, &id)
        if r == kIOReturnSuccess { awakeAssertion = id; Log.info("Keeping the display awake while mirroring") }
        else { Log.warn("Could not keep the display awake (\(r))") }
    }

    private func allowDisplaySleep() {
        guard let id = awakeAssertion else { return }
        IOPMAssertionRelease(id)
        awakeAssertion = nil
        Log.info("Display may sleep again")
    }
    public var onState: ((State) -> Void)?
    public var options: MirrorOptions
    private var pipeline: Pipeline?
    private var transport: AVTransport?
    private var pollTask: Task<Void, Never>?

    public init(options: MirrorOptions) { self.options = options }

    public var isActive: Bool {
        switch state { case .connecting, .playing: return true; default: return false }
    }

    public func start(on tv: Renderer) async {
        guard !isActive else { return }
        guard let avt = tv.avTransport else { state = .error("\(tv.name) can't play streams (no AVTransport)"); return }
        do {
            state = .connecting("finding a route to \(tv.name)")
            // Right after wake the Wi-Fi may still be rejoining: give it a few seconds.
            var host = localAddress(towards: tv.ip)
            for _ in 0..<10 where host == nil {
                try? await Task.sleep(nanoseconds: 500_000_000)
                host = localAddress(towards: tv.ip)
            }
            guard let host else {
                Log.error("No route to \(tv.ip)")
                throw CaptureError(description: "Can't reach the TV")
            }
            state = .connecting("starting capture and encoder")
            let p = Pipeline(options: options)
            p.onFailure = { [weak self] why in Task { @MainActor in await self?.fail(why) } }
            try await p.start(serve: true, allowedPeer: tv.ip, bindHost: host)
            pipeline = p

            let url = "http://\(host):\(options.port)\(p.streamPath)"
            let protocolInfo = DLNA.protocolInfo(mime: options.mime, features: options.contentFeatures)
            Log.info("Serving \(url)  (Content-Type \(options.mime))")
            Log.info("protocolInfo \(protocolInfo)")

            state = .connecting("asking \(tv.name) to play")
            let t = AVTransport(url: avt)
            transport = t
            do { try await t.stop() } catch { Log.info("Stop before play failed (nothing playing is fine): \(error)") }
            let meta = options.sendMetadata ? SOAP.didl(url: url, protocolInfo: protocolInfo) : ""
            try await t.setURI(url, metadata: meta)
            try await t.play()
            state = .playing(tv.name)
            Log.info("Playing on \(tv.name)")
            poll(t, name: tv.name)
        } catch {
            Log.error("Start failed: \(error)")
            await teardown(sendStop: transport != nil)
            state = .error(Self.explain(error, tv: tv.name))
        }
    }

    /// One plain sentence for the panel; the log keeps the raw error.
    public static func explain(_ error: Error, tv: String) -> String {
        if let u = error as? URLError {
            switch u.code {
            case .timedOut, .cannotConnectToHost, .networkConnectionLost, .cannotFindHost, .notConnectedToInternet:
                return "The TV didn't answer"
            default: return "Couldn't talk to \(tv): \(u.localizedDescription)"
            }
        }
        if let f = error as? SOAPError, f.upnpCode == "716" {
            // The TV couldn't open our stream URL: usually a guest network or
            // client isolation between this Mac and the TV.
            return "The TV can't reach this Mac"
        }
        let text = "\(error)"
        return text.prefix(1).uppercased() + text.dropFirst()
    }

    public func setMargin(_ m: Double) async {
        options.marginPercent = m
        await pipeline?.setMargin(m)
    }

    public func stop() async {
        guard pipeline != nil || transport != nil else { state = .stopped; return }
        Log.info("Stopping")
        await teardown(sendStop: true)
        state = .stopped
    }

    private func fail(_ why: String) async {
        guard isActive else { return }
        Log.error("Stopping: \(why)")
        await teardown(sendStop: true)
        state = .error(why)
    }

    private func teardown(sendStop: Bool) async {
        pollTask?.cancel()
        pollTask = nil
        if sendStop, let t = transport { try? await t.stop() }
        transport = nil
        pipeline?.stop()
        pipeline = nil
    }

    /// Logs the TV's transport state every 5s; if it stops after playing
    /// (remote control, input change) the session ends and says so.
    /// State checks in a row the TV may miss before the session ends.
    static let maxMissedPolls = 3

    private func poll(_ t: AVTransport, name: String) {
        pollTask = Task { [weak self] in
            var last: String?, played = false, misses = 0
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: 5_000_000_000)
                if Task.isCancelled { return }
                // A TV in standby or off the network never answers again: stop
                // rather than keep capturing and serving the screen to no one.
                let answer: String?
                do { answer = try await t.transportState(); misses = 0 } catch {
                    misses += 1
                    Log.info("TV didn't answer a state check (\(misses) of \(Self.maxMissedPolls)): \(error)")
                    if misses >= Self.maxMissedPolls { await self?.fail("The TV stopped answering"); return }
                    continue
                }
                guard let s = answer else { continue }
                if s != last { Log.info("TV transport state: \(s)"); last = s }
                if s == "PLAYING" { played = true }
                if played, s == "STOPPED" || s == "NO_MEDIA_PRESENT" {
                    Log.info("TV stopped playing (state \(s))"); await self?.fail("The TV stopped the stream")
                    return
                }
            }
        }
    }
}
