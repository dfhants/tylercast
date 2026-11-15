using System.Net.Sockets;
using System.Text;
using TylerCast.Core;

namespace TylerCast.Core.Tests;

/// <summary>Ported from the Swift DLNATests (DLNA, SOAP and HTTP suites).</summary>
public class DlnaTests
{
    internal const string Features =
        "DLNA.ORG_PN=AVC_TS_HD_50_AC3_ISO;DLNA.ORG_OP=00;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000";

    [Fact]
    public void ContentFeaturesAsSpecified()
    {
        Assert.Equal(Features, Dlna.ContentFeatures());
        Assert.Equal("DLNA.ORG_OP=00;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000", Dlna.ContentFeatures(null));
        Assert.Equal("http-get:*:video/mpeg:" + Features, Dlna.ProtocolInfo(Features));
    }

    [Fact]
    public void SearchMessage() =>
        Assert.Equal("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\n"
            + "MX: 2\r\nST: urn:schemas-upnp-org:device:MediaRenderer:1\r\n\r\n", Ssdp.SearchMessage());

    [Fact]
    public void ParsesSsdpResponse()
    {
        var text = "HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=1800\r\nLocation: http://192.0.2.175:55000/dmr/ddd.xml\r\n"
            + "SERVER: Linux/4.0 UPnP/1.0 Example-UPnP-MW/1.0\r\nST: urn:schemas-upnp-org:device:MediaRenderer:1\r\n\r\n";
        Assert.Equal(new SsdpResponse("192.0.2.175", "http://192.0.2.175:55000/dmr/ddd.xml",
            "Linux/4.0 UPnP/1.0 Example-UPnP-MW/1.0", Dlna.RendererST), Ssdp.ParseResponse(text, "192.0.2.175"));
        Assert.Null(Ssdp.ParseResponse("HTTP/1.1 200 OK\r\nST: x\r\n\r\n", "1.2.3.4"));
    }

    [Fact]
    public void ParsesDeviceDescriptionAndResolvesControlUrls()
    {
        var xml = """
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
            """;
        var r = DeviceDescription.Parse(Encoding.UTF8.GetBytes(xml), "http://192.0.2.175:55000/dmr/ddd.xml", "192.0.2.175");
        Assert.Equal("Living Room TV", r.Name);
        Assert.Equal("Example Example Vision", r.Model);
        Assert.Equal("http://192.0.2.175:55000/dmr/control_2", r.AVTransport?.AbsoluteUri);
        Assert.Equal("http://192.0.2.175:55000/dmr/control_1", r.ConnectionManager?.AbsoluteUri);
    }

    [Fact]
    public void UrlBaseWins()
    {
        var xml = "<root xmlns=\"urn:schemas-upnp-org:device-1-0\"><URLBase>http://10.0.0.5:8080/</URLBase><device>"
            + $"<friendlyName>X</friendlyName><serviceList><service><serviceType>{Dlna.AVTransport}</serviceType>"
            + "<controlURL>avt/ctl</controlURL></service></serviceList></device></root>";
        var r = DeviceDescription.Parse(Encoding.UTF8.GetBytes(xml), "http://10.0.0.5:1/desc.xml", "10.0.0.5");
        Assert.Equal("http://10.0.0.5:8080/avt/ctl", r.AVTransport?.AbsoluteUri);
    }

    /// <summary>A UTF-8 byte order mark (some Windows-built devices send one) doesn't stop parsing.</summary>
    [Fact]
    public void ToleratesByteOrderMark()
    {
        var xml = $"<root xmlns=\"urn:schemas-upnp-org:device-1-0\"><device><friendlyName>TV</friendlyName></device></root>";
        byte[] data = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(xml)];
        Assert.Equal("TV", DeviceDescription.Parse(data, "http://10.0.0.5:1/d.xml", "10.0.0.5").Name);
    }
}

public class SoapTests
{
    [Fact]
    public void EnvelopeMatchesMirrorPy()
    {
        var e = Soap.Envelope(Dlna.AVTransport, "Play", [("InstanceID", "0"), ("Speed", "1")]);
        Assert.Equal("<?xml version=\"1.0\" encoding=\"utf-8\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" "
            + "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body><u:Play xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\">"
            + "<InstanceID>0</InstanceID><Speed>1</Speed></u:Play></s:Body></s:Envelope>", e);
        Assert.Equal("\"urn:schemas-upnp-org:service:AVTransport:1#Play\"", Soap.Headers(Dlna.AVTransport, "Play")["SOAPACTION"]);
    }

    [Fact]
    public void SetUriKeepsArgumentOrderAndEscapesMetadata()
    {
        const string url = "http://192.0.2.10:8090/stream.ts";
        var meta = Soap.Didl(url, Dlna.ProtocolInfo(DlnaTests.Features));
        var e = Soap.Envelope(Dlna.AVTransport, "SetAVTransportURI",
            [("InstanceID", "0"), ("CurrentURI", url), ("CurrentURIMetaData", meta)]);
        int i = e.IndexOf("<InstanceID>", StringComparison.Ordinal), u = e.IndexOf("<CurrentURI>", StringComparison.Ordinal),
            m = e.IndexOf("<CurrentURIMetaData>", StringComparison.Ordinal);
        Assert.True(i < u && u < m);
        Assert.Contains("<CurrentURIMetaData>&lt;DIDL-Lite xmlns=&quot;urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/&quot;", e);
        Assert.DoesNotContain("<DIDL-Lite", e);
        // Unescaped once, it is the DIDL again.
        Assert.Equal(meta, Soap.Element("CurrentURIMetaData", e));
    }

    [Fact]
    public void DidlHasProtocolInfo()
    {
        var d = Soap.Didl("http://h:8090/stream.ts", Dlna.ProtocolInfo(DlnaTests.Features));
        Assert.Equal("<DIDL-Lite xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" "
            + "xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\"><item id=\"0\" parentID=\"-1\" restricted=\"1\">"
            + "<dc:title>TylerCast</dc:title><upnp:class>object.item.videoItem</upnp:class>"
            + $"<res protocolInfo=\"http-get:*:video/mpeg:{DlnaTests.Features}\">http://h:8090/stream.ts</res></item></DIDL-Lite>", d);
    }

    [Fact]
    public void ParsesUpnpFault()
    {
        var body = """
            <?xml version="1.0"?><s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><s:Fault>
            <faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring><detail>
            <UPnPError xmlns="urn:schemas-upnp-org:control-1-0"><errorCode>714</errorCode>
            <errorDescription>Illegal MIME-type</errorDescription></UPnPError></detail></s:Fault></s:Body></s:Envelope>
            """;
        var f = Soap.Fault("SetAVTransportURI", 500, body);
        Assert.True(f.UpnpCode == "714" && f.UpnpDescription == "Illegal MIME-type");
        Assert.Equal("SetAVTransportURI refused: HTTP 500, UPnP error 714 Illegal MIME-type", f.Message);
    }

    [Fact]
    public void ReadsTransportStateAndSink()
    {
        var r = "<s:Envelope><s:Body><u:GetTransportInfoResponse xmlns:u=\"x\"><CurrentTransportState>PLAYING</CurrentTransportState>"
            + "</u:GetTransportInfoResponse></s:Body></s:Envelope>";
        Assert.Equal("PLAYING", Soap.Element("CurrentTransportState", r));
        Assert.Equal("http-get:*:video/mpeg:DLNA.ORG_PN=A&B", Soap.Element("Sink", "<Sink>http-get:*:video/mpeg:DLNA.ORG_PN=A&amp;B</Sink>"));
    }
}

[Collection("network")]
public class HttpTests
{
    [Fact]
    public void ParsesRequestHead()
    {
        var raw = "GET /stream.ts HTTP/1.1\r\nHost: 192.0.2.10:8090\r\ngetcontentFeatures.dlna.org: 1\r\nRange: bytes=0-\r\n\r\n";
        var r = HttpRequestHead.Parse(Encoding.UTF8.GetBytes(raw))!;
        Assert.True(r.Method == "GET" && r.Path == "/stream.ts" && r.Version == "HTTP/1.1");
        Assert.True(r.Header("range") == "bytes=0-" && r.Header("GETCONTENTFEATURES.DLNA.ORG") == "1");
        Assert.Null(HttpRequestHead.Parse(Encoding.UTF8.GetBytes("GET / HTTP/1.1\r\nHost: x\r\n"))); // incomplete
    }

    [Fact]
    public void StreamHeadersAreAsSent()
    {
        var h = new StreamHeaders().Ok();
        Assert.StartsWith("HTTP/1.0 200 OK\r\n", h);
        Assert.EndsWith("\r\n\r\n", h);
        foreach (var line in new[] { "Content-Type: video/mpeg", "transferMode.dlna.org: Streaming",
                     $"contentFeatures.dlna.org: {DlnaTests.Features}", "Connection: close" })
            Assert.Contains($"\r\n{line}\r\n", h);
        Assert.DoesNotContain("content-length", h.ToLowerInvariant());
        Assert.DoesNotContain("realTimeInfo", h);
    }

    /// <summary>Sends a raw request and reads until <paramref name="want"/> bytes or the server closes.</summary>
    internal static async Task<byte[]> Raw(int port, string request, int want)
    {
        using var c = new TcpClient();
        await c.ConnectAsync("127.0.0.1", port);
        var s = c.GetStream();
        await s.WriteAsync(Encoding.ASCII.GetBytes(request));
        var buf = new MemoryStream();
        var b = new byte[65536];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (buf.Length < want)
        {
            var n = await s.ReadAsync(b, cts.Token);
            if (n == 0) break;
            buf.Write(b, 0, n);
        }
        return buf.ToArray();
    }

    internal static int HeadEnd(byte[] d) => d.AsSpan().IndexOf("\r\n\r\n"u8) + 4;

    /// <summary>Real server on a free port: HEAD gets headers only, two GETs both join one stream, other paths 404.</summary>
    [Fact]
    public async Task ServerHeadGetAndJoin()
    {
        var b = new Broadcaster();
        var server = new StreamServer(b, new StreamHeaders(), port: 0);
        server.Start();
        using var cts = new CancellationTokenSource();
        var feeder = Task.Run(async () =>
        {
            var m = new TsMuxer();
            while (!cts.IsCancellationRequested)
            {
                b.Send(m.Produce(64));
                await Task.Delay(5);
            }
        });
        try
        {
            var head = await Raw(server.Port, "HEAD /stream.ts HTTP/1.1\r\nHost: x\r\n\r\n", 1 << 20);
            Assert.Equal(new StreamHeaders().Ok(), Encoding.UTF8.GetString(head)); // headers, no body, then closed

            var gets = await Task.WhenAll(
                Raw(server.Port, "GET /stream.ts HTTP/1.1\r\nHost: x\r\n\r\n", 100_000),
                Raw(server.Port, "GET /stream.ts HTTP/1.0\r\n\r\n", 100_000));
            foreach (var g in gets)
            {
                var end = HeadEnd(g);
                Assert.Equal(new StreamHeaders().Ok(), Encoding.UTF8.GetString(g, 0, end));
                var body = g[end..];
                Assert.True(body.Length > 188 * 10);
                for (var k = 0; k + 188 < body.Length; k += 188) Assert.Equal(0x47, body[k]); // whole packets
            }

            var nf = Encoding.UTF8.GetString(await Raw(server.Port, "GET /other HTTP/1.1\r\n\r\n", 1 << 20));
            Assert.StartsWith("HTTP/1.0 404", nf);
        }
        finally
        {
            cts.Cancel();
            await feeder;
            server.Stop();
        }
    }

    /// <summary>With OnJoin set, a GET triggers it and its body starts exactly at a keyframe's PAT.</summary>
    [Fact]
    public async Task JoinStartsAtKeyframe()
    {
        var b = new Broadcaster();
        var server = new StreamServer(b, new StreamHeaders(), port: 0);
        var joins = 0;
        var m = new TsMuxer();
        var gate = new Lock();
        server.OnJoin = () =>
        {
            Interlocked.Increment(ref joins);
            lock (gate) m.EnqueueVideo(Enumerable.Repeat((byte)1, 3000).ToArray(), m.Now90 + 9000, keyframe: true);
        };
        server.Start();
        using var cts = new CancellationTokenSource();
        var feeder = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                (byte[] d, int? k) chunk;
                lock (gate) chunk = m.ProduceChunk(64);
                b.Send(chunk.d, chunk.k);
                await Task.Delay(5);
            }
        });
        try
        {
            var g = await Raw(server.Port, "GET /stream.ts HTTP/1.1\r\n\r\n", 4096);
            var p = g[HeadEnd(g)..];
            Assert.True(p.Length >= 188 * 2);
            Assert.Equal(1, joins);
            Assert.True(p[0] == 0x47 && (p[1] & 0x1F) == 0 && p[2] == 0);       // PAT first
            Assert.True((p[188 + 1] & 0x1F) == 0x10 && p[188 + 2] == 0x00);     // then PMT (0x1000)
        }
        finally
        {
            cts.Cancel();
            await feeder;
            server.Stop();
        }
    }

    /// <summary>When the stream ends, every connection is closed (the TV sees the end of the body).</summary>
    [Fact]
    public async Task FinishClosesConnections()
    {
        var b = new Broadcaster();
        var server = new StreamServer(b, new StreamHeaders(), port: 0);
        server.Start();
        try
        {
            var get = Raw(server.Port, "GET /stream.ts HTTP/1.0\r\n\r\n", int.MaxValue);
            for (var i = 0; i < 100 && b.Count == 0; i++) await Task.Delay(20);
            b.Send(new TsMuxer().Produce(10));
            b.Finish();
            var g = await get.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(188 * 10, g.Length - HeadEnd(g));
        }
        finally { server.Stop(); }
    }
}
