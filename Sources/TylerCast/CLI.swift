import Foundation
import MirrorCore

/// Command-line modes, for debugging without the menu:
///   TylerCast discover                   renderers, their services, the TV's sink formats
///   TylerCast play [--tv IP|name]        mirror to a TV until Ctrl-C (sends Stop on exit)
///   TylerCast serve                      stream on :8090 with no TV (try ffplay/VLC on it)
///   TylerCast record out.ts [--seconds]  write the stream to a file (check with ffprobe)
enum CLI {
    static let commands: Set<String> = ["discover", "play", "serve", "record", "make-icon", "help", "-h", "--help"]

    static let usage = """
    TylerCast: mirror this Mac's screen to a DLNA TV.
    With no arguments it runs as a menu bar app.

      discover [--timeout S] [--st ssdp:all]
      play     [--tv IP|name] [--avt URL] [stream options]
      serve    [stream options]                     no TV; open the URL it prints (random path)
      record   FILE [--seconds N] [stream options]  no TV, no server

    stream options:
      --margin PCT       black border on each edge, beats TV overscan (try 3-5)
      --test-pattern     colour bars instead of the screen
      --audio KIND       aac (default: the Mac's sound), silent-ac3 (silent track in the
                         AC-3 profile the TV lists), none (= --no-audio)
      --mac-sound        keep the Mac's speakers playing too (default: muted while mirroring)
      --bitrate 6M       video;  --muxrate 20M  constant TS rate (null padding)
      --delay 0.15       seconds of capture-to-DTS slack; raise if the log shows late frames
      --mime video/mpeg  --no-pn  --tlag  --no-metadata  --port 8090
      -v                 full SSDP/SOAP/HTTP bodies on stderr (always in ~/Library/Logs/TylerCast.log)
    """

    struct Args {
        var positional: [String] = []
        var values: [String: String] = [:]
        var flags: Set<String> = []
        static let valued: Set<String> = ["--tv", "--avt", "--margin", "--bitrate", "--muxrate", "--delay", "--mime",
                                          "--port", "--seconds", "--timeout", "--st", "--audio"]
        init(_ a: [String]) throws {
            var i = 0
            while i < a.count {
                let x = a[i]
                if Self.valued.contains(x) {
                    guard i + 1 < a.count else { throw CLIError("\(x) needs a value") }
                    values[x] = a[i + 1]; i += 2
                } else if x.hasPrefix("-") {
                    flags.insert(x); i += 1
                } else {
                    positional.append(x); i += 1
                }
            }
        }
    }

    struct CLIError: Error, CustomStringConvertible {
        var description: String
        init(_ s: String) { description = s }
    }

    static func rate(_ s: String) throws -> Int {
        let u = s.uppercased()
        let mult: Double = u.hasSuffix("M") ? 1e6 : u.hasSuffix("K") ? 1e3 : 1
        guard let v = Double(u.trimmingCharacters(in: CharacterSet(charactersIn: "MK"))) else { throw CLIError("bad rate \(s)") }
        return Int(v * mult)
    }

    static func options(_ a: Args) throws -> MirrorOptions {
        var o = MirrorOptions()
        if let m = a.values["--margin"] { o.marginPercent = Double(m) ?? 0 }
        if let b = a.values["--bitrate"] { o.video.bitrate = try rate(b) }
        if let m = a.values["--muxrate"] { o.muxRate = try rate(m) }
        if let d = a.values["--delay"], let v = Double(d) { o.delay = v }
        if let m = a.values["--mime"] { o.mime = m }
        if let p = a.values["--port"], let v = UInt16(p) { o.port = v }
        o.testPattern = a.flags.contains("--test-pattern")
        if let k = a.values["--audio"] {
            guard let v = MirrorOptions.Audio(rawValue: k) else {
                throw CLIError("--audio must be one of \(MirrorOptions.Audio.allCases.map(\.rawValue).joined(separator: ", "))")
            }
            o.audio = v
        }
        if a.flags.contains("--no-audio") { o.audio = .none }
        if a.flags.contains("--mac-sound") { o.soundOnTVOnly = false }
        if a.flags.contains("--no-pn") { o.sendProfile = false }
        if a.flags.contains("--tlag") { o.tlag = true }
        if a.flags.contains("--no-metadata") { o.sendMetadata = false }
        if o.video.bitrate > o.muxRate * 8 / 10 { Log.warn("--bitrate is close to --muxrate: keyframes will be late") }
        return o
    }

    @MainActor
    static func run(_ argv: [String]) async -> Int32 {
        do {
            let a = try Args(Array(argv.dropFirst()))
            Log.verbose = a.flags.contains("-v") || a.flags.contains("--verbose")
            switch argv[0] {
            case "discover": await discover(a)
            case "play": return try await play(a)
            case "serve": return try await serve(a)
            case "record": return try await record(a)
            case "make-icon":  // make-icon DIR: AppIcon.iconset PNGs for iconutil (used by make-app.sh)
                guard let dir = a.positional.first else { throw CLIError("make-icon needs a directory") }
                try FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
                for pt in [16, 32, 128, 256, 512] {
                    for scale in [1, 2] {
                        let name = scale == 1 ? "icon_\(pt)x\(pt).png" : "icon_\(pt)x\(pt)@2x.png"
                        try TylerIcon.appIconPNG(px: pt * scale)!.write(to: URL(fileURLWithPath: dir).appendingPathComponent(name))
                    }
                }
            default: print(usage)
            }
            return 0
        } catch {
            Log.error("\(error)")
            return 1
        }
    }

    static func discover(_ a: Args) async {
        let st = a.values["--st"] ?? DLNA.rendererST
        let timeout = a.values["--timeout"].flatMap(Double.init) ?? 4
        let hits = await Task.detached { SSDP.search(st: st, timeout: timeout) }.value
        Log.info("\(hits.count) response(s)")
        for h in hits {
            Log.info("\(h.ip)  \(h.location)  (\(h.server))")
            guard let d = try? await DeviceDescription.fetch(h) else { Log.warn("  description failed"); continue }
            Log.info("  \(d.name) [\(d.model)]")
            for (t, u) in d.services.sorted(by: { $0.key < $1.key }) { Log.info("    \(t) -> \(u.absoluteString)") }
            if let cm = d.connectionManager,
               let r = try? await SOAP.call(cm, service: DLNA.connectionManager, action: "GetProtocolInfo", args: []),
               let sink = SOAP.element("Sink", in: r) {
                for p in sink.split(separator: ",") where p.lowercased().contains("mpeg") || p.contains("mp2t") || p.contains("AVC_TS") {
                    Log.info("    sink: \(p)")
                }
            }
        }
    }

    @MainActor
    static func findTV(_ a: Args) async throws -> Renderer {
        if let avt = a.values["--avt"], let u = URL(string: avt), let host = u.host {
            return Renderer.manual(ip: host, avTransport: u)  // skip SSDP: e.g. http://192.0.2.175:55000/dmr/control_2
        }
        let want = a.values["--tv"]
        let all = await Discovery.renderers()
        guard !all.isEmpty else {
            throw CLIError("No MediaRenderer answered SSDP. Is the TV on (not standby), on this network, with DLNA remote on? "
                + "Or pass --avt http://IP:55000/dmr/control_2")
        }
        // An exact IP, or a name that only one renderer has. Any device can answer
        // SSDP with any name, so an ambiguous choice is refused, not guessed.
        let matches = all.filter { want == nil || $0.ip == want! || $0.name.lowercased().contains(want!.lowercased()) }
        if matches.count == 1 { return matches[0] }
        if matches.isEmpty { throw CLIError("No renderer with AVTransport matched \(want ?? "")") }
        let list = matches.map { "\($0.name) (\($0.ip))" }.joined(separator: ", ")
        throw CLIError("Several renderers match: \(list). Pick one with --tv IP")
    }

    @MainActor
    static func play(_ a: Args) async throws -> Int32 {
        let tv = try await findTV(a)
        let session = MirrorSession(options: try options(a))
        var finished: CheckedContinuation<Int32, Never>?
        session.onState = { s in
            Log.info("State: \(s.text)")
            if case .error = s { finished?.resume(returning: 1); finished = nil }
        }
        let stopper = SignalStopper { await session.stop(); finished?.resume(returning: 0); finished = nil }
        await session.start(on: tv)
        guard session.isActive else { return 1 }
        Log.info("Playing. Ctrl-C to stop.")
        let code = await withCheckedContinuation { finished = $0 }
        _ = stopper
        return code
    }

    @MainActor
    static func serve(_ a: Args) async throws -> Int32 {
        let p = Pipeline(options: try options(a))
        try await p.start(serve: true)
        Log.info("Serving http://\(localAddress(towards: "8.8.8.8") ?? "<this mac>"):\(p.options.port)\(p.streamPath) "
                 + "to anyone on the network. Ctrl-C to stop.")
        var stopper: SignalStopper?
        let code = await withCheckedContinuation { (c: CheckedContinuation<Int32, Never>) in
            stopper = SignalStopper { p.stop(); c.resume(returning: 0) }
        }
        _ = stopper
        return code
    }

    @MainActor
    static func record(_ a: Args) async throws -> Int32 {
        guard let path = a.positional.first else { throw CLIError("record needs a file name") }
        let seconds = a.values["--seconds"].flatMap(Double.init) ?? 10
        FileManager.default.createFile(atPath: path, contents: nil)
        guard let fh = FileHandle(forWritingAtPath: path) else { throw CLIError("cannot write \(path)") }
        let p = Pipeline(options: try options(a))
        var bytes = 0
        let id = p.broadcaster.subscribe { d, _ in if let d { fh.write(d); bytes += d.count } }
        try await p.start(serve: false)
        try await Task.sleep(nanoseconds: UInt64(seconds * 1e9))
        p.broadcaster.unsubscribe(id)
        p.stop()
        try fh.close()
        Log.info("Wrote \(bytes / 1000) kB to \(path). Check: ffprobe -show_streams \(path)")
        return 0
    }
}

/// Ctrl-C / SIGTERM run `action` once, on the main actor.
final class SignalStopper {
    private var sources: [DispatchSourceSignal] = []
    init(_ action: @escaping @MainActor () async -> Void) {
        var fired = false
        for sig in [SIGINT, SIGTERM] {
            signal(sig, SIG_IGN)
            let s = DispatchSource.makeSignalSource(signal: sig, queue: .main)
            s.setEventHandler {
                guard !fired else { exit(1) }  // second Ctrl-C: give up waiting
                fired = true
                Task { @MainActor in await action() }
            }
            s.resume()
            sources.append(s)
        }
    }
}
