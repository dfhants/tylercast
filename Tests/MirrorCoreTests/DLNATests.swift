import Foundation
import Network
import Testing
@testable import MirrorCore

let features = "DLNA.ORG_PN=AVC_TS_HD_50_AC3_ISO;DLNA.ORG_OP=00;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000"

@Suite struct DLNATests {
    @Test func contentFeaturesAsSpecified() {
        #expect(DLNA.contentFeatures() == features)
        #expect(DLNA.contentFeatures(profile: nil) == "DLNA.ORG_OP=00;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000")
        #expect(DLNA.protocolInfo(features: features) == "http-get:*:video/mpeg:" + features)
    }

    @Test func searchMessage() {
        #expect(SSDP.searchMessage() == "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\n"
            + "MX: 2\r\nST: urn:schemas-upnp-org:device:MediaRenderer:1\r\n\r\n")
    }

    @Test func parsesSSDPResponse() {
        let text = "HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=1800\r\nLocation: http://192.0.2.175:55000/dmr/ddd.xml\r\n"
            + "SERVER: Linux/4.0 UPnP/1.0 Example-UPnP-MW/1.0\r\nST: urn:schemas-upnp-org:device:MediaRenderer:1\r\n\r\n"
        let r = SSDP.parseResponse(text, from: "192.0.2.175")
        #expect(r == SSDPResponse(ip: "192.0.2.175", location: "http://192.0.2.175:55000/dmr/ddd.xml",
                                  server: "Linux/4.0 UPnP/1.0 Example-UPnP-MW/1.0", st: DLNA.rendererST))
        #expect(SSDP.parseResponse("HTTP/1.1 200 OK\r\nST: x\r\n\r\n", from: "1.2.3.4") == nil)
    }

    @Test func parsesDeviceDescriptionAndResolvesControlURLs() throws {
        let xml = """
        <?xml version="1.0"?>
        <root xmlns="urn:schemas-upnp-org:device-1-0" xmlns:ex="urn:schemas-example-com:ex">
          <specVersion><major>1</major><minor>0</minor></specVersion>
          <device>
            <deviceType>urn:schemas-upnp-org:device:MediaRenderer:1</deviceType>
            <friendlyName>Living Room TV</friendlyName>
            <manufacturer>Example</manufacturer>
            <modelName>Example Vision</modelName>
            <serviceList>
              <service><serviceType>urn:schemas-upnp-org:service:RenderingControl:1</serviceType>
                <controlURL>/dmr/control_0</controlURL></service>
              <service><serviceType>urn:schemas-upnp-org:service:ConnectionManager:1</serviceType>
                <controlURL>/dmr/control_1</controlURL></service>
              <service><serviceType>urn:schemas-upnp-org:service:AVTransport:1</serviceType>
                <controlURL>/dmr/control_2</controlURL></service>
            </serviceList>
          </device>
        </root>
        """
        let r = try DeviceDescription.parse(Data(xml.utf8), location: "http://192.0.2.175:55000/dmr/ddd.xml", ip: "192.0.2.175")
        #expect(r.name == "Living Room TV")
        #expect(r.model == "Example Example Vision")
        #expect(r.avTransport?.absoluteString == "http://192.0.2.175:55000/dmr/control_2")
        #expect(r.connectionManager?.absoluteString == "http://192.0.2.175:55000/dmr/control_1")
    }

    @Test func urlBaseWins() throws {
        let xml = "<root xmlns=\"urn:schemas-upnp-org:device-1-0\"><URLBase>http://10.0.0.5:8080/</URLBase><device>"
            + "<friendlyName>X</friendlyName><serviceList><service><serviceType>\(DLNA.avTransport)</serviceType>"
            + "<controlURL>avt/ctl</controlURL></service></serviceList></device></root>"
        let r = try DeviceDescription.parse(Data(xml.utf8), location: "http://10.0.0.5:1/desc.xml", ip: "10.0.0.5")
        #expect(r.avTransport?.absoluteString == "http://10.0.0.5:8080/avt/ctl")
    }
}

@Suite struct SOAPTests {
    @Test func envelopeMatchesMirrorPy() {
        let e = SOAP.envelope(service: DLNA.avTransport, action: "Play", args: [("InstanceID", "0"), ("Speed", "1")])
        #expect(e == "<?xml version=\"1.0\" encoding=\"utf-8\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" "
            + "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body><u:Play xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\">"
            + "<InstanceID>0</InstanceID><Speed>1</Speed></u:Play></s:Body></s:Envelope>")
        #expect(SOAP.headers(service: DLNA.avTransport, action: "Play")["SOAPACTION"] == "\"urn:schemas-upnp-org:service:AVTransport:1#Play\"")
    }

    @Test func setURIKeepsArgumentOrderAndEscapesMetadata() {
        let url = "http://192.0.2.10:8090/stream.ts"
        let pi = DLNA.protocolInfo(features: features)
        let meta = SOAP.didl(url: url, protocolInfo: pi)
        let e = SOAP.envelope(service: DLNA.avTransport, action: "SetAVTransportURI",
                              args: [("InstanceID", "0"), ("CurrentURI", url), ("CurrentURIMetaData", meta)])
        let i = e.range(of: "<InstanceID>")!.lowerBound, u = e.range(of: "<CurrentURI>")!.lowerBound,
            m = e.range(of: "<CurrentURIMetaData>")!.lowerBound
        #expect(i < u && u < m)
        #expect(e.contains("<CurrentURIMetaData>&lt;DIDL-Lite xmlns=&quot;urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/&quot;"))
        #expect(!e.contains("<DIDL-Lite"))
        // Unescaped once, it is the DIDL again.
        #expect(SOAP.element("CurrentURIMetaData", in: e) == meta)
    }

    @Test func didlHasProtocolInfo() {
        let pi = DLNA.protocolInfo(features: features)
        let d = SOAP.didl(url: "http://h:8090/stream.ts", protocolInfo: pi)
        #expect(d == "<DIDL-Lite xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" "
            + "xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\"><item id=\"0\" parentID=\"-1\" restricted=\"1\">"
            + "<dc:title>TylerCast</dc:title><upnp:class>object.item.videoItem</upnp:class>"
            + "<res protocolInfo=\"http-get:*:video/mpeg:\(features)\">http://h:8090/stream.ts</res></item></DIDL-Lite>")
    }

    @Test func parsesUPnPFault() {
        let body = """
        <?xml version="1.0"?><s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><s:Fault>
        <faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring><detail>
        <UPnPError xmlns="urn:schemas-upnp-org:control-1-0"><errorCode>714</errorCode>
        <errorDescription>Illegal MIME-type</errorDescription></UPnPError></detail></s:Fault></s:Body></s:Envelope>
        """
        let f = SOAP.fault(action: "SetAVTransportURI", status: 500, body: body)
        #expect(f.upnpCode == "714" && f.upnpDescription == "Illegal MIME-type")
        #expect(f.description == "SetAVTransportURI refused: HTTP 500, UPnP error 714 Illegal MIME-type")
    }

    @Test func readsTransportStateAndSink() {
        let r = "<s:Envelope><s:Body><u:GetTransportInfoResponse xmlns:u=\"x\"><CurrentTransportState>PLAYING</CurrentTransportState>"
            + "</u:GetTransportInfoResponse></s:Body></s:Envelope>"
        #expect(SOAP.element("CurrentTransportState", in: r) == "PLAYING")
        #expect(SOAP.element("Sink", in: "<Sink>http-get:*:video/mpeg:DLNA.ORG_PN=A&amp;B</Sink>") == "http-get:*:video/mpeg:DLNA.ORG_PN=A&B")
    }
}

@Suite(.serialized) struct HTTPTests {
    @Test func parsesRequestHead() {
        let raw = "GET /stream.ts HTTP/1.1\r\nHost: 192.0.2.10:8090\r\ngetcontentFeatures.dlna.org: 1\r\nRange: bytes=0-\r\n\r\n"
        let r = HTTPRequest.parse(Data(raw.utf8))!
        #expect(r.method == "GET" && r.path == "/stream.ts" && r.version == "HTTP/1.1")
        #expect(r.header("range") == "bytes=0-" && r.header("GETCONTENTFEATURES.DLNA.ORG") == "1")
        #expect(HTTPRequest.parse(Data("GET / HTTP/1.1\r\nHost: x\r\n".utf8)) == nil)  // incomplete
    }

    @Test func streamHeaders() {
        let h = StreamHeaders().ok()
        #expect(h.hasPrefix("HTTP/1.0 200 OK\r\n"))
        #expect(h.hasSuffix("\r\n\r\n"))
        for line in ["Content-Type: video/mpeg", "transferMode.dlna.org: Streaming", "contentFeatures.dlna.org: \(features)",
                     "Connection: close"] {
            #expect(h.contains("\r\n\(line)\r\n"))
        }
        #expect(!h.lowercased().contains("content-length"))
        #expect(!h.contains("realTimeInfo"))
    }

    /// Real server on a spare port: HEAD gets headers only, two GETs both join one stream, other paths 404.
    @Test func serverHeadGetAndJoin() async throws {
        let b = Broadcaster()
        let port: UInt16 = 18_090
        let server = StreamServer(port: port, headers: StreamHeaders(), broadcaster: b)
        try server.start()
        defer { server.stop() }
        let feeder = Task.detached {
            let m = TSMuxer()
            while !Task.isCancelled { b.send(m.produce(count: 64)); try? await Task.sleep(nanoseconds: 5_000_000) }
        }
        defer { feeder.cancel() }

        @Sendable func raw(_ request: String, want: Int) async throws -> Data {
            try await withCheckedThrowingContinuation { cont in
                let c = NWConnection(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: port)!, using: .tcp)
                var buf = Data(), done = false
                func finish(_ r: Result<Data, Error>) { if !done { done = true; c.cancel(); cont.resume(with: r) } }
                func read() {
                    c.receive(minimumIncompleteLength: 1, maximumLength: 65536) { d, _, complete, err in
                        if let d { buf.append(d) }
                        if buf.count >= want || complete || err != nil { finish(.success(buf)) } else { read() }
                    }
                }
                c.start(queue: .global())
                c.send(content: Data(request.utf8), completion: .contentProcessed { e in if let e { finish(.failure(e)) } })
                read()
            }
        }

        let head = try await raw("HEAD /stream.ts HTTP/1.1\r\nHost: x\r\n\r\n", want: 1 << 20)
        let headText = String(decoding: head, as: UTF8.self)
        #expect(headText == StreamHeaders().ok())  // headers, no body, then closed

        async let g1 = raw("GET /stream.ts HTTP/1.1\r\nHost: x\r\n\r\n", want: 100_000)
        async let g2 = raw("GET /stream.ts HTTP/1.0\r\n\r\n", want: 100_000)
        for g in try await [g1, g2] {
            let end = g.range(of: Data("\r\n\r\n".utf8))!.upperBound
            #expect(String(decoding: g[..<end], as: UTF8.self) == StreamHeaders().ok())
            let body = [UInt8](g[end...])
            #expect(body.count > 188 * 10)
            for k in stride(from: 0, to: body.count - 188, by: 188) { #expect(body[k] == 0x47) }  // whole packets
        }

        let nf = String(decoding: try await raw("GET /other HTTP/1.1\r\n\r\n", want: 1 << 20), as: UTF8.self)
        #expect(nf.hasPrefix("HTTP/1.0 404"))
    }

    /// With onJoin set, a GET triggers it and its body starts exactly at a keyframe's PAT.
    @Test func joinStartsAtKeyframe() async throws {
        let b = Broadcaster()
        let port: UInt16 = 18_092
        let server = StreamServer(port: port, headers: StreamHeaders(), broadcaster: b)
        let joins = Counter()
        let m = TSMuxer(), lock = NSLock()
        server.onJoin = { joins.add(); lock.withLock { m.enqueueVideo(annexB: [UInt8](repeating: 1, count: 3000), pts90: m.now90 + 9000, keyframe: true) } }
        try server.start()
        defer { server.stop() }
        let feeder = Task.detached {
            while !Task.isCancelled {
                let (d, k) = lock.withLock { m.produceChunk(count: 64) }
                b.send(d, keyOffset: k)
                try? await Task.sleep(nanoseconds: 5_000_000)
            }
        }
        defer { feeder.cancel() }
        let body: Data = try await withCheckedThrowingContinuation { cont in
            let c = NWConnection(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: port)!, using: .tcp)
            var buf = Data(), done = false
            func read() {
                c.receive(minimumIncompleteLength: 1, maximumLength: 65536) { d, _, complete, err in
                    if let d { buf.append(d) }
                    if let end = buf.range(of: Data("\r\n\r\n".utf8)), buf.count - end.upperBound >= 188 * 8 {
                        if !done { done = true; c.cancel(); cont.resume(returning: buf[end.upperBound...]) }
                    } else if complete || err != nil {
                        if !done { done = true; c.cancel(); cont.resume(throwing: NWError.posix(.ECONNRESET)) }
                    } else { read() }
                }
            }
            c.start(queue: .global())
            c.send(content: Data("GET /stream.ts HTTP/1.1\r\n\r\n".utf8), completion: .contentProcessed { _ in })
            read()
        }
        let p = [UInt8](body)
        #expect(joins.value == 1)
        #expect(p[0] == 0x47 && p[1] & 0x1F == 0 && p[2] == 0)  // PAT first
        #expect(p[188 + 1] & 0x1F == 0x10 && p[188 + 2] == 0x00)  // then PMT (0x1000)
    }
}

final class Counter: @unchecked Sendable {
    private let l = NSLock(); private var n = 0
    func add() { l.withLock { n += 1 } }
    var value: Int { l.withLock { n } }
}
