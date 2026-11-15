import Darwin
import Foundation
import Network

public struct SSDPResponse: Equatable {
    public var ip: String
    public var location: String
    public var server: String
    public var st: String
}

/// SSDP M-SEARCH over a plain UDP socket (multicast out, unicast replies back).
public enum SSDP {
    public static let address = "239.255.255.250"
    public static let port: UInt16 = 1900

    public static func searchMessage(st: String = DLNA.rendererST, mx: Int = 2) -> String {
        "M-SEARCH * HTTP/1.1\r\n"
            + "HOST: \(address):\(port)\r\n"
            + "MAN: \"ssdp:discover\"\r\n"
            + "MX: \(mx)\r\n"
            + "ST: \(st)\r\n\r\n"
    }

    /// Headers of an SSDP reply, keys lowercased. Nil if it has no LOCATION.
    public static func parseResponse(_ text: String, from ip: String) -> SSDPResponse? {
        var headers: [String: String] = [:]
        for line in text.components(separatedBy: "\r\n").dropFirst() {
            guard let colon = line.firstIndex(of: ":") else { continue }
            let key = line[..<colon].trimmingCharacters(in: .whitespaces).lowercased()
            headers[key] = line[line.index(after: colon)...].trimmingCharacters(in: .whitespaces)
        }
        guard let loc = headers["location"], !loc.isEmpty else { return nil }
        return SSDPResponse(ip: ip, location: loc, server: headers["server"] ?? "", st: headers["st"] ?? "")
    }

    /// Blocking search; logs every reply. Asks twice because UDP is lossy.
    /// errno of the last failed M-SEARCH send (EHOSTUNREACH/EPERM: Local Network
    /// access not granted yet), or 0.
    public private(set) static var lastSendErrno: Int32 = 0
    public static var localNetworkBlocked: Bool { lastSendErrno == EHOSTUNREACH || lastSendErrno == EPERM }

    /// `onResponse` is called (on this thread) for each new answer as it arrives.
    public static func search(st: String = DLNA.rendererST, timeout: TimeInterval = 3,
                              onResponse: ((SSDPResponse) -> Void)? = nil) -> [SSDPResponse] {
        lastSendErrno = 0
        let fd = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP)
        guard fd >= 0 else { Log.error("SSDP: socket() failed: \(String(cString: strerror(errno)))"); return [] }
        defer { close(fd) }
        var ttl: UInt8 = 2
        setsockopt(fd, IPPROTO_IP, IP_MULTICAST_TTL, &ttl, socklen_t(MemoryLayout<UInt8>.size))
        var tv = timeval(tv_sec: 0, tv_usec: 300_000)
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))

        var dest = sockaddr_in()
        dest.sin_family = sa_family_t(AF_INET)
        dest.sin_port = port.bigEndian
        inet_pton(AF_INET, address, &dest.sin_addr)
        let msg = Array(searchMessage(st: st).utf8)
        Log.debug("SSDP M-SEARCH:\n\(searchMessage(st: st).trimmingCharacters(in: .whitespacesAndNewlines))")
        for _ in 0..<2 {
            let sent = withUnsafePointer(to: &dest) {
                $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                    sendto(fd, msg, msg.count, 0, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
                }
            }
            if sent < 0 { lastSendErrno = errno; Log.error("SSDP: sendto failed: \(String(cString: strerror(errno))) (Local Network permission?)") }
        }

        var found: [SSDPResponse] = []
        var seen = Set<String>()
        let end = Date().addingTimeInterval(timeout)
        var buf = [UInt8](repeating: 0, count: 65507)
        while Date() < end {
            var from = sockaddr_in()
            var len = socklen_t(MemoryLayout<sockaddr_in>.size)
            let n = withUnsafeMutablePointer(to: &from) {
                $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { recvfrom(fd, &buf, buf.count, 0, $0, &len) }
            }
            if n <= 0 { continue }
            var ipBuf = [CChar](repeating: 0, count: Int(INET_ADDRSTRLEN))
            inet_ntop(AF_INET, &from.sin_addr, &ipBuf, socklen_t(INET_ADDRSTRLEN))
            let ip = String(cString: ipBuf)
            let text = String(decoding: buf[0..<n], as: UTF8.self)
            Log.debug("SSDP response from \(ip):\n\(text.trimmingCharacters(in: .whitespacesAndNewlines))")
            if let r = parseResponse(text, from: ip), !seen.contains(r.location) {
                seen.insert(r.location)
                Log.info("SSDP: \(ip) \(r.location) (\(r.server))")
                found.append(r)
                onResponse?(r)
            }
        }
        return found
    }
}

/// The local address the kernel would use to reach `ip` (no packet is sent).
public func localAddress(towards ip: String) -> String? {
    let fd = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP)
    guard fd >= 0 else { return nil }
    defer { close(fd) }
    var dest = sockaddr_in()
    dest.sin_family = sa_family_t(AF_INET)
    dest.sin_port = UInt16(9).bigEndian
    guard inet_pton(AF_INET, ip, &dest.sin_addr) == 1 else { return nil }
    let ok = withUnsafePointer(to: &dest) {
        $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { connect(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) }
    }
    guard ok == 0 else { return nil }
    var local = sockaddr_in()
    var len = socklen_t(MemoryLayout<sockaddr_in>.size)
    _ = withUnsafeMutablePointer(to: &local) {
        $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { getsockname(fd, $0, &len) }
    }
    var ipBuf = [CChar](repeating: 0, count: Int(INET_ADDRSTRLEN))
    inet_ntop(AF_INET, &local.sin_addr, &ipBuf, socklen_t(INET_ADDRSTRLEN))
    return String(cString: ipBuf)
}


/// Sends one M-SEARCH through Network.framework. macOS shows its Local Network
/// prompt for Network.framework traffic; a plain BSD socket can just fail
/// with "No route to host" and never ask.
public enum LocalNetworkPrompt {
    private static var connection: NWConnection?
    public static func trigger() {
        let c = NWConnection(host: NWEndpoint.Host(SSDP.address), port: NWEndpoint.Port(rawValue: SSDP.port)!, using: .udp)
        c.stateUpdateHandler = { state in
            switch state {
            case .ready:
                c.send(content: Data(SSDP.searchMessage().utf8), completion: .contentProcessed { _ in
                    DispatchQueue.main.asyncAfter(deadline: .now() + 2) { c.cancel() }
                })
            case let .waiting(e): Log.info("Local Network: waiting (\(e)); macOS should be asking for permission")
            case let .failed(e): Log.warn("Local Network check failed: \(e)"); c.cancel()
            default: break
            }
        }
        connection = c
        c.start(queue: .main)
    }
}
