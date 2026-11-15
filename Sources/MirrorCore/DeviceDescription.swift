import Foundation

/// A renderer from its UPnP device description.
public struct Renderer: Hashable, Identifiable {
    public var id: String { location }
    public var location: String
    public var ip: String
    public var name: String
    public var model: String
    /// serviceType -> absolute control URL
    public var services: [String: URL]

    public var avTransport: URL? { services[DLNA.avTransport] }
    public var connectionManager: URL? { services[DLNA.connectionManager] }

    public init(location: String, ip: String, name: String, model: String, services: [String: URL]) {
        self.location = location; self.ip = ip; self.name = name; self.model = model; self.services = services
    }

    /// For a TV whose address is known (skips SSDP): AVTransport at the given control URL.
    public static func manual(ip: String, avTransport: URL) -> Renderer {
        Renderer(location: avTransport.absoluteString, ip: ip, name: ip, model: "", services: [DLNA.avTransport: avTransport])
    }
}

public enum DeviceDescription {
    public struct ParseError: Error, CustomStringConvertible {
        public var description: String
    }

    /// Parses the description XML; relative control URLs are resolved against
    /// URLBase if present, else the description's own URL (as urljoin does).
    public static func parse(_ data: Data, location: String, ip: String) throws -> Renderer {
        let d = Collector()
        let p = XMLParser(data: data)
        p.delegate = d
        guard p.parse() else { throw ParseError(description: "bad XML: \(p.parserError?.localizedDescription ?? "?")") }
        let baseString = (d.urlBase?.isEmpty == false ? d.urlBase! : location)
        guard let base = URL(string: baseString) ?? URL(string: location) else {
            throw ParseError(description: "bad base URL \(baseString)")
        }
        var services: [String: URL] = [:]
        for s in d.services {
            guard let type = s["serviceType"], let ctl = s["controlURL"],
                  let url = URL(string: ctl, relativeTo: base)?.absoluteURL else { continue }
            // SOAP calls (with the stream URL) go only to the device that answered SSDP.
            guard isOnDevice(url, ip: ip) else {
                Log.warn("Ignoring \(type) at \(url.absoluteString): not plain http on \(ip)")
                continue
            }
            services[type] = url
        }
        let model = [d.manufacturer, d.modelName].compactMap { $0 }.joined(separator: " ")
            .trimmingCharacters(in: .whitespaces)
        return Renderer(location: location, ip: ip, name: d.friendlyName ?? ip, model: model, services: services)
    }

    /// True for an http URL whose host is `ip`. Any device on the network can
    /// answer SSDP, so its LOCATION and control URLs must point back at itself:
    /// no file:// reads, no requests to other hosts on its say-so.
    public static func isOnDevice(_ url: URL, ip: String) -> Bool {
        url.scheme?.lowercased() == "http" && url.host == ip
    }

    public static func fetch(_ hit: SSDPResponse) async throws -> Renderer {
        guard let url = URL(string: hit.location) else { throw ParseError(description: "bad LOCATION \(hit.location)") }
        guard isOnDevice(url, ip: hit.ip) else {
            throw ParseError(description: "LOCATION \(hit.location) is not plain http on \(hit.ip), the address that answered")
        }
        var req = URLRequest(url: url, timeoutInterval: 5)
        req.httpMethod = "GET"
        let (data, _) = try await URLSession.shared.data(for: req)
        Log.debug("Device description \(hit.location):\n\(String(decoding: data, as: UTF8.self))")
        return try parse(data, location: hit.location, ip: hit.ip)
    }

    private final class Collector: NSObject, XMLParserDelegate {
        var urlBase: String?
        var friendlyName: String?, manufacturer: String?, modelName: String?
        var services: [[String: String]] = []
        private var stack: [String] = []
        private var text = ""
        private var current: [String: String]?

        private func local(_ name: String) -> String { name.split(separator: ":").last.map(String.init) ?? name }

        func parser(_ parser: XMLParser, didStartElement name: String, namespaceURI: String?,
                    qualifiedName: String?, attributes: [String: String] = [:]) {
            let n = local(name)
            stack.append(n)
            text = ""
            if n == "service" { current = [:] }
        }

        func parser(_ parser: XMLParser, foundCharacters string: String) { text += string }

        func parser(_ parser: XMLParser, didEndElement name: String, namespaceURI: String?, qualifiedName: String?) {
            let n = local(name)
            let value = text.trimmingCharacters(in: .whitespacesAndNewlines)
            let parent = stack.dropLast().last
            switch n {
            case "URLBase": urlBase = value
            // First device only (the root renderer), as mirror.py's find(".//device").
            case "friendlyName" where parent == "device" && friendlyName == nil: friendlyName = value
            case "manufacturer" where parent == "device" && manufacturer == nil: manufacturer = value
            case "modelName" where parent == "device" && modelName == nil: modelName = value
            case "serviceType", "controlURL": if parent == "service" { current?[n] = value }
            case "service": if let c = current { services.append(c) }; current = nil
            default: break
            }
            stack.removeLast()
            text = ""
        }
    }
}

public enum Discovery {
    /// SSDP search; each answer's description is fetched as it arrives, and
    /// `onFound` hears about each renderer straight away (a TV usually answers
    /// in ~0.1s; the search keeps listening for `timeout`). Renderers without
    /// AVTransport are logged and skipped.
    public static func renderers(timeout: TimeInterval = 3, st: String = DLNA.rendererST,
                                 onFound: ((Renderer) async -> Void)? = nil) async -> [Renderer] {
        let answers = AsyncStream<SSDPResponse> { cont in
            Task.detached {
                _ = SSDP.search(st: st, timeout: timeout) { cont.yield($0) }
                cont.finish()
            }
        }
        var out: [Renderer] = []
        for await h in answers {
            do {
                let r = try await DeviceDescription.fetch(h)
                let names = r.services.keys.map { $0.split(separator: ":").dropLast().last.map(String.init) ?? $0 }
                Log.info("Renderer: \(r.name) [\(r.model)] at \(r.ip) services=\(names.sorted())")
                guard r.avTransport != nil else { continue }
                out.append(r)
                await onFound?(r)
            } catch {
                Log.warn("Could not read \(h.location): \(error)")
            }
        }
        return out
    }
}
