import Foundation

public struct SOAPError: Error, CustomStringConvertible {
    public var action: String
    public var httpStatus: Int
    public var upnpCode: String?
    public var upnpDescription: String?
    public var body: String
    public var description: String {
        "\(action) refused: HTTP \(httpStatus)" + (upnpCode.map { ", UPnP error \($0)" } ?? "")
            + (upnpDescription.map { " \($0)" } ?? "")
    }
}

public enum SOAP {
    /// Escapes as Python's html.escape(quote=True), which mirror.py used.
    public static func escape(_ s: String) -> String {
        var out = ""
        out.reserveCapacity(s.count)
        for c in s {
            switch c {
            case "&": out += "&amp;"
            case "<": out += "&lt;"
            case ">": out += "&gt;"
            case "\"": out += "&quot;"
            case "'": out += "&#x27;"
            default: out.append(c)
            }
        }
        return out
    }

    public static func unescape(_ s: String) -> String {
        s.replacingOccurrences(of: "&lt;", with: "<").replacingOccurrences(of: "&gt;", with: ">")
            .replacingOccurrences(of: "&quot;", with: "\"").replacingOccurrences(of: "&#x27;", with: "'")
            .replacingOccurrences(of: "&apos;", with: "'").replacingOccurrences(of: "&amp;", with: "&")
    }

    /// Argument order matters to some renderers, so it is a list, not a dictionary.
    public static func envelope(service: String, action: String, args: [(String, String)]) -> String {
        let body = args.map { "<\($0.0)>\(escape($0.1))</\($0.0)>" }.joined()
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
            + "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" "
            + "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>"
            + "<u:\(action) xmlns:u=\"\(service)\">\(body)</u:\(action)>"
            + "</s:Body></s:Envelope>"
    }

    public static func headers(service: String, action: String) -> [String: String] {
        ["Content-Type": "text/xml; charset=\"utf-8\"", "SOAPACTION": "\"\(service)#\(action)\""]
    }

    public static func didl(url: String, protocolInfo: String, title: String = "TylerCast") -> String {
        "<DIDL-Lite xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\" "
            + "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" "
            + "xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\">"
            + "<item id=\"0\" parentID=\"-1\" restricted=\"1\">"
            + "<dc:title>\(escape(title))</dc:title>"
            + "<upnp:class>object.item.videoItem</upnp:class>"
            + "<res protocolInfo=\"\(escape(protocolInfo))\">\(escape(url))</res>"
            + "</item></DIDL-Lite>"
    }

    /// Text of the first <name>…</name> (any namespace prefix), unescaped.
    public static func element(_ name: String, in xml: String) -> String? {
        let pattern = "<(?:[A-Za-z0-9_]+:)?\(name)(?:\\s[^>]*)?>(.*?)</(?:[A-Za-z0-9_]+:)?\(name)>"
        guard let re = try? NSRegularExpression(pattern: pattern, options: [.dotMatchesLineSeparators]),
              let m = re.firstMatch(in: xml, range: NSRange(xml.startIndex..., in: xml)),
              let r = Range(m.range(at: 1), in: xml) else { return nil }
        return unescape(String(xml[r]))
    }

    public static func fault(action: String, status: Int, body: String) -> SOAPError {
        SOAPError(action: action, httpStatus: status, upnpCode: element("errorCode", in: body),
                  upnpDescription: element("errorDescription", in: body), body: body)
    }

    @discardableResult
    public static func call(_ url: URL, service: String, action: String, args: [(String, String)],
                            quiet: Bool = false) async throws -> String {
        let env = envelope(service: service, action: action, args: args)
        var req = URLRequest(url: url, timeoutInterval: 10)
        req.httpMethod = "POST"
        for (k, v) in headers(service: service, action: action) { req.setValue(v, forHTTPHeaderField: k) }
        req.httpBody = Data(env.utf8)
        let note: (String) -> Void = quiet ? { Log.debug($0) } : { Log.info($0) }
        note("SOAP -> \(action) \(url.absoluteString)")
        Log.debug("SOAP request body:\n\(env)")
        let (data, resp) = try await URLSession.shared.data(for: req)
        let status = (resp as? HTTPURLResponse)?.statusCode ?? 0
        let body = String(decoding: data, as: UTF8.self)
        guard (200..<300).contains(status) else {
            let e = fault(action: action, status: status, body: body)
            Log.error("SOAP <- \(action) HTTP \(status)  UPnP error \(e.upnpCode ?? "?") \(e.upnpDescription ?? "")\n\(body)")
            throw e
        }
        note("SOAP <- \(action) HTTP \(status)")
        Log.debug("SOAP reply:\n\(body)")
        return body
    }
}

/// The three AVTransport calls used, plus state polling.
public struct AVTransport {
    public var url: URL
    public init(url: URL) { self.url = url }

    public func stop() async throws {
        try await SOAP.call(url, service: DLNA.avTransport, action: "Stop", args: [("InstanceID", "0")])
    }
    public func setURI(_ uri: String, metadata: String) async throws {
        try await SOAP.call(url, service: DLNA.avTransport, action: "SetAVTransportURI",
                            args: [("InstanceID", "0"), ("CurrentURI", uri), ("CurrentURIMetaData", metadata)])
    }
    public func play() async throws {
        try await SOAP.call(url, service: DLNA.avTransport, action: "Play", args: [("InstanceID", "0"), ("Speed", "1")])
    }
    public func transportState() async throws -> String? {
        let r = try await SOAP.call(url, service: DLNA.avTransport, action: "GetTransportInfo", args: [("InstanceID", "0")],
                                   quiet: true)  // every 5s; full exchange still in the log file
        return SOAP.element("CurrentTransportState", in: r)
    }
}
