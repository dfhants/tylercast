import Foundation
import Network

public struct HTTPRequest {
    public var method: String
    public var path: String
    public var version: String
    public var headers: [(String, String)]
    public var raw: String

    public func header(_ name: String) -> String? {
        headers.first { $0.0.caseInsensitiveCompare(name) == .orderedSame }?.1
    }

    /// Parses a request head; nil until the blank line has arrived.
    public static func parse(_ data: Data) -> HTTPRequest? {
        guard let end = data.range(of: Data("\r\n\r\n".utf8)) else { return nil }
        let raw = String(decoding: data[..<end.lowerBound], as: UTF8.self)
        var lines = raw.components(separatedBy: "\r\n")
        let parts = lines.removeFirst().split(separator: " ", omittingEmptySubsequences: true).map(String.init)
        guard parts.count >= 2 else { return nil }
        let headers = lines.compactMap { line -> (String, String)? in
            guard let c = line.firstIndex(of: ":") else { return nil }
            return (String(line[..<c]).trimmingCharacters(in: .whitespaces),
                    String(line[line.index(after: c)...]).trimmingCharacters(in: .whitespaces))
        }
        return HTTPRequest(method: parts[0], path: parts[1], version: parts.count > 2 ? parts[2] : "HTTP/1.0",
                           headers: headers, raw: raw)
    }
}

public struct StreamHeaders {
    public var contentType = DLNA.defaultMime
    public var contentFeatures = DLNA.contentFeatures()
    /// realTimeInfo.dlna.org: DLNA.ORG_TLAG=* ("this is live").
    public var tlag = false
    public init() {}

    /// HTTP/1.0, no Content-Length: the body ends when the connection closes.
    public func ok() -> String {
        var h = "HTTP/1.0 200 OK\r\n"
            + "Server: \(DLNA.serverHeader)\r\n"
            + "Content-Type: \(contentType)\r\n"
            + "transferMode.dlna.org: Streaming\r\n"
            + "contentFeatures.dlna.org: \(contentFeatures)\r\n"
        if tlag { h += "realTimeInfo.dlna.org: DLNA.ORG_TLAG=*\r\n" }
        h += "Cache-Control: no-cache\r\nConnection: close\r\n\r\n"
        return h
    }

    public static func notFound() -> String {
        "HTTP/1.0 404 Not Found\r\nServer: \(DLNA.serverHeader)\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
    }

    public static func badRequest() -> String {
        "HTTP/1.0 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
    }
}

/// HTTP/1.0 server for `/stream.ts`. Every GET joins the one running stream
/// (the TV sends HEAD, then a short GET it drops, then the real GET).
public final class StreamServer {
    public let port: UInt16
    public let path: String
    public var headers: StreamHeaders
    private let broadcaster: Broadcaster
    /// A client that falls this far behind (bytes queued in the kernel/NW) is dropped.
    private let maxBacklog: Int
    private var listener: NWListener?
    private let queue = DispatchQueue(label: "http-server")
    private var connections: [ObjectIdentifier: NWConnection] = [:]
    public var onRequest: ((HTTPRequest, String) -> Void)?
    /// When set, a new GET calls this (the pipeline forces a keyframe) and its
    /// data starts at the next keyframe, not mid-GOP. Many TVs hold their clock
    /// from connect until the first keyframe, so a mid-GOP join adds up to a
    /// whole keyframe interval (2s) of permanent delay.
    public var onJoin: (() -> Void)?

    public init(port: UInt16 = 8090, path: String = "/stream.ts", headers: StreamHeaders, broadcaster: Broadcaster,
                maxBacklogBytes: Int = 4_000_000) {
        self.port = port; self.path = path; self.headers = headers; self.broadcaster = broadcaster
        self.maxBacklog = maxBacklogBytes
    }

    /// Starts listening; returns once the listener is ready or has failed.
    /// The TV's last connection lingers in TIME_WAIT for ~30s after a session,
    /// and NWListener will not bind over it even with endpoint reuse on, so
    /// "address in use" is retried for up to `waitForPort` seconds.
    public func start(waitForPort: Double = 35) throws {
        let deadline = Date().addingTimeInterval(waitForPort)
        var warned = false
        while true {
            do {
                try listenOnce()
                return
            } catch NWError.posix(.EADDRINUSE) where Date() < deadline {
                if !warned {
                    Log.warn("Port \(port) busy (the TV's last connection is closing, or another copy is running); waiting up to \(Int(waitForPort))s")
                    warned = true
                }
                Thread.sleep(forTimeInterval: 1)
            }
        }
    }

    private func listenOnce() throws {
        let params = NWParameters.tcp
        params.allowLocalEndpointReuse = true
        let l = try NWListener(using: params, on: NWEndpoint.Port(rawValue: port)!)
        let ready = DispatchSemaphore(value: 0)
        var failure: Error?
        l.stateUpdateHandler = { state in
            switch state {
            case .ready: ready.signal()
            case let .failed(e): failure = e; ready.signal()
            default: break
            }
        }
        l.newConnectionHandler = { [weak self] c in self?.accept(c) }
        l.start(queue: queue)
        listener = l
        if ready.wait(timeout: .now() + 3) == .timedOut { l.cancel(); listener = nil; throw NWError.posix(.ETIMEDOUT) }
        if let e = failure { l.cancel(); listener = nil; throw e }
        Log.info("HTTP server listening on port \(port)")
    }

    public func stop() {
        listener?.cancel()
        listener = nil
        queue.sync { for c in connections.values { c.cancel() }; connections = [:] }
    }

    private func accept(_ c: NWConnection) {
        connections[ObjectIdentifier(c)] = c
        c.stateUpdateHandler = { [weak self, weak c] state in
            guard let self, let c else { return }
            switch state {
            case .failed, .cancelled: self.connections[ObjectIdentifier(c)] = nil
            default: break
            }
        }
        c.start(queue: queue)
        readHead(c, buffer: Data())
    }

    private func peer(_ c: NWConnection) -> String {
        if case let .hostPort(host, _) = c.endpoint { return "\(host)".components(separatedBy: "%").first ?? "\(host)" }
        return "\(c.endpoint)"
    }

    private func readHead(_ c: NWConnection, buffer: Data) {
        c.receive(minimumIncompleteLength: 1, maximumLength: 16384) { [weak self] data, _, done, error in
            guard let self else { return }
            var buf = buffer
            if let data { buf.append(data) }
            if let req = HTTPRequest.parse(buf) {
                self.handle(req, on: c)
            } else if done || error != nil || buf.count > 65536 {
                if buf.count > 0 { self.finish(c, StreamHeaders.badRequest()) } else { c.cancel() }
            } else {
                self.readHead(c, buffer: buf)
            }
        }
    }

    private func finish(_ c: NWConnection, _ text: String) {
        c.send(content: Data(text.utf8), contentContext: .finalMessage, isComplete: true,
               completion: .contentProcessed { _ in c.cancel() })
    }

    private func handle(_ req: HTTPRequest, on c: NWConnection) {
        let from = peer(c)
        Log.info("TV \(req.method) \(req.path) from \(from)")
        Log.debug("TV request headers:\n\(req.raw)")
        onRequest?(req, from)
        let target = req.path.split(separator: "?").first.map(String.init) ?? req.path
        guard target == path, req.method == "GET" || req.method == "HEAD" else {
            finish(c, StreamHeaders.notFound())
            return
        }
        if req.method == "HEAD" {
            finish(c, headers.ok())
            return
        }
        c.send(content: Data(headers.ok().utf8), completion: .contentProcessed { _ in })
        join(c, from: from)
        onJoin?()
    }

    /// Streams the live TS to one connection until either side ends it.
    private func join(_ c: NWConnection, from: String) {
        let lock = NSLock()
        var pending = 0, sent: UInt64 = 0, closed = false, lastReport = Date()
        let alignToKeyframe = onJoin != nil
        var waiting = alignToKeyframe
        let joined = Date()
        var id: UUID?
        let maxBacklog = self.maxBacklog
        let close: (String) -> Void = { [weak self] why in
            lock.lock()
            if closed { lock.unlock(); return }
            closed = true
            let total = sent
            lock.unlock()
            if let id { self?.broadcaster.unsubscribe(id) }
            Log.info("TV connection from \(from) ended after \(String(format: "%.1f", Double(total) / 1e6)) MB: \(why)")
            c.cancel()
        }
        id = broadcaster.subscribe { chunk, keyOffset in
            guard var data = chunk else { close("stream ended"); return }
            lock.lock()
            if closed { lock.unlock(); return }
            if waiting {
                if let k = keyOffset {
                    data = data.subdata(in: k..<data.count)
                    waiting = false
                    Log.info("TV \(from) starts at a keyframe after \(Int(Date().timeIntervalSince(joined) * 1000))ms")
                } else if Date().timeIntervalSince(joined) > 3 {
                    waiting = false
                    Log.warn("TV \(from): no keyframe within 3s; joining mid-stream")
                } else {
                    lock.unlock()
                    return
                }
            }
            if pending > maxBacklog {
                lock.unlock()
                close("fell \(maxBacklog / 1_000_000) MB behind; dropped")
                return
            }
            pending += data.count
            let first = sent == 0
            sent += UInt64(data.count)
            var backlog: Int?
            if Date().timeIntervalSince(lastReport) >= 5 { lastReport = Date(); backlog = pending }
            lock.unlock()
            if first { Log.info("First bytes sent to TV (\(from))") }
            // Bytes handed to the network but not yet taken by the TV: at a constant
            // mux rate this is pure added delay (1.25 MB = 1s at 10 Mbit/s).
            if let b = backlog { Log.info("TV \(from) backlog \(b / 1000) kB") }
            c.send(content: data, completion: .contentProcessed { error in
                lock.lock(); pending -= data.count; lock.unlock()
                if let error { close("\(error)") }
            })
        }
        // The TV never sends a body; a completed read means it hung up.
        c.receive(minimumIncompleteLength: 1, maximumLength: 1024) { _, _, done, error in
            if done || error != nil { close(error.map { "\($0)" } ?? "TV closed the connection") }
        }
    }
}
