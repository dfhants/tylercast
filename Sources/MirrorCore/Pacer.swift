import Foundation

/// Fans the one TS byte stream out to every TV connection. A connection that
/// falls too far behind is dropped rather than allowed to stall the encoder.
public final class Broadcaster {
    /// (chunk, offset of a keyframe start in it if any); nil chunk = stream ended
    public typealias Sink = (Data?, Int?) -> Void
    private let lock = NSLock()
    private var sinks: [UUID: Sink] = [:]
    private var ended = false

    public init() {}

    public func subscribe(_ sink: @escaping Sink) -> UUID {
        let id = UUID()
        lock.lock()
        let isEnded = ended
        if !isEnded { sinks[id] = sink }
        lock.unlock()
        if isEnded { sink(nil, nil) }
        return id
    }

    public func unsubscribe(_ id: UUID) {
        lock.lock(); sinks[id] = nil; lock.unlock()
    }

    public var count: Int { lock.lock(); defer { lock.unlock() }; return sinks.count }

    public func send(_ data: Data, keyOffset: Int? = nil) {
        lock.lock(); let s = Array(sinks.values); lock.unlock()
        for sink in s { sink(data, keyOffset) }
    }

    public func finish() {
        lock.lock(); let s = Array(sinks.values); sinks = [:]; ended = true; lock.unlock()
        for sink in s { sink(nil, nil) }
    }
}

/// Drives a TSMuxer in real time: emits exactly muxRate bits per second
/// (null-padded) to the broadcaster, so the byte position, the PCR and the
/// wall clock stay locked together. Also maps host time (what ScreenCaptureKit
/// and the frame clock use) to stream time.
public final class Pacer {
    public let muxer: TSMuxer
    public let broadcaster: Broadcaster
    /// Capture-to-DTS delay: the slack that keeps frames from arriving late.
    public let delay: Double
    /// Called under the muxer lock before each batch, with the host time in seconds.
    public var onTick: ((Double) -> Void)?

    private let lock = NSLock()
    private var thread: Thread?
    private var running = false
    private var startHost: Double = 0
    private var lastStats = 0.0
    public private(set) var bytesSent: UInt64 = 0

    public init(muxer: TSMuxer, broadcaster: Broadcaster, delay: Double) {
        self.muxer = muxer
        self.broadcaster = broadcaster
        self.delay = delay
    }

    public static func hostSeconds() -> Double {
        Double(clock_gettime_nsec_np(CLOCK_UPTIME_RAW)) / 1e9
    }

    /// Stream time (90 kHz) for a host time, plus the delay: the PTS to stamp
    /// on something captured at `host`.
    public func pts90(forHost host: Double) -> UInt64 {
        let t = max(0, host - startHost + delay)
        return TSMuxer.base90 + UInt64(t * 90_000)
    }

    public func withMuxer<T>(_ body: (TSMuxer) -> T) -> T {
        lock.lock(); defer { lock.unlock() }
        return body(muxer)
    }

    public func start() {
        startHost = Self.hostSeconds()
        lastStats = startHost
        running = true
        let t = Thread { [weak self] in self?.loop() }
        t.name = "ts-pacer"
        t.qualityOfService = .userInteractive
        thread = t
        t.start()
    }

    public func stop() {
        lock.lock(); running = false; lock.unlock()
        broadcaster.finish()
    }

    private func loop() {
        let rate = Double(muxer.config.muxRate)
        while true {
            lock.lock()
            guard running else { lock.unlock(); return }
            let now = Self.hostSeconds()
            let target = UInt64(max(0, now - startHost) * rate / 1504)
            var data: Data?, keyOffset: Int?
            if target > muxer.packetsEmitted {
                onTick?(now)
                (data, keyOffset) = muxer.produceChunk(count: Int(target - muxer.packetsEmitted))
            }
            var stats: TSMuxer.Stats?
            if now - lastStats >= 10 { stats = muxer.takeStats(); lastStats = now }
            lock.unlock()
            if let d = data {
                bytesSent += UInt64(d.count)
                broadcaster.send(d, keyOffset: keyOffset)
            }
            if let s = stats { Self.log(s) }
            usleep(4000)
        }
    }

    static func log(_ s: TSMuxer.Stats) {
        func ms(_ v: Double) -> String { v.isFinite ? String(format: "%.0fms", v) : "-" }
        let msg = "TS 10s: \(s.videoFrames) video frames (\(s.keyframes) key), \(s.audioFrames) audio, "
            + "late \(s.lateFrames), min slack \(ms(s.minSlackMs)) (keyframes \(ms(s.minKeyframeSlackMs))), "
            + "max queue \(s.maxQueuedPackets) pkts, padding \(s.packets > 0 ? 100 * s.nullPackets / s.packets : 0)%"
        if s.lateFrames > 0 { Log.warn(msg + "  <- late frames: raise --delay or --muxrate, or lower --bitrate") } else { Log.info(msg) }
    }
}
