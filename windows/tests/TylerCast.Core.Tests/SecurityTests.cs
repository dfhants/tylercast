using System.Net;
using System.Net.Sockets;
using System.Text;
using TylerCast.Core;

namespace TylerCast.Core.Tests;

/// <summary>
/// Ported from the Swift StreamAccessTests and DiscoveryTrustTests (commit f73a5f2,
/// "Only the TV can watch the stream; trust discovery less"), plus the .NET-side
/// additions: no redirects, no DTDs, and choosing a renderer unambiguously.
/// </summary>
[Collection("network")]
public class StreamAccessTests
{
    /// <summary>Sends a GET and returns whatever comes back before the server closes (or 2s pass).
    /// A reset counts as the end: a refused connection may be reset rather than closed.</summary>
    internal static async Task<byte[]> Get(int port, string path)
    {
        var buf = new MemoryStream();
        try
        {
            using var c = new TcpClient();
            await c.ConnectAsync("127.0.0.1", port);
            var s = c.GetStream();
            await s.WriteAsync(Encoding.ASCII.GetBytes($"GET {path} HTTP/1.0\r\n\r\n"));
            var b = new byte[65536];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (buf.Length <= 4096)
            {
                var n = await s.ReadAsync(b, cts.Token);
                if (n == 0) break;
                buf.Write(b, 0, n);
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException) { }
        return buf.ToArray();
    }

    internal static (CancellationTokenSource, Task) Feed(Broadcaster b)
    {
        var cts = new CancellationTokenSource();
        var t = Task.Run(async () =>
        {
            var m = new TsMuxer();
            while (!cts.IsCancellationRequested)
            {
                b.Send(m.Produce(64));
                await Task.Delay(5);
            }
        });
        return (cts, t);
    }

    [Fact]
    public void RandomPathsDiffer()
    {
        string a = StreamServer.RandomPath(), b = StreamServer.RandomPath();
        Assert.True(a != b && a.StartsWith('/') && a.EndsWith(".ts", StringComparison.Ordinal) && a.Length > 30);
    }

    [Theory]
    [InlineData("::ffff:192.0.2.5")]
    [InlineData("192.0.2.5%en0")]
    [InlineData("192.0.2.5")]
    public void NormalizesPeerAddresses(string raw) => Assert.Equal("192.0.2.5", StreamServer.Normalize(raw));

    /// <summary>Someone other than the TV gets nothing, not even headers, even with the right path.</summary>
    [Fact]
    public async Task RefusesOtherPeers()
    {
        var b = new Broadcaster();
        var path = StreamServer.RandomPath();
        var server = new StreamServer(b, new StreamHeaders(), port: 0, path: path, allowedPeer: "192.0.2.10");
        server.Start();
        var (cts, feeder) = Feed(b);
        try
        {
            Assert.Empty(await Get(server.Port, path));
            Assert.Equal(0, b.Count); // never subscribed
        }
        finally
        {
            cts.Cancel();
            await feeder;
            server.Stop();
        }
    }

    /// <summary>The TV, on the bound address, with the right path, gets the stream; a wrong path is 404.</summary>
    [Fact]
    public async Task AllowsTheTvOnTheBoundAddress()
    {
        var b = new Broadcaster();
        var path = StreamServer.RandomPath();
        var server = new StreamServer(b, new StreamHeaders(), port: 0, path: path, allowedPeer: "127.0.0.1", bindHost: "127.0.0.1");
        server.Start();
        var (cts, feeder) = Feed(b);
        try
        {
            var ok = await Get(server.Port, path);
            Assert.Equal("HTTP/1.0 200 OK", Encoding.ASCII.GetString(ok, 0, 15));
            Assert.True(ok.Length > 1000);
            var nf = await Get(server.Port, "/stream.ts");
            Assert.Equal("HTTP/1.0 404", Encoding.ASCII.GetString(nf, 0, 12));
        }
        finally
        {
            cts.Cancel();
            await feeder;
            server.Stop();
        }
    }

    /// <summary>Bound to loopback, the server can't be reached on the machine's other addresses.</summary>
    [Fact]
    public void BindHostIsTheOnlyListeningAddress()
    {
        var server = new StreamServer(new Broadcaster(), new StreamHeaders(), port: 0, bindHost: "127.0.0.1");
        server.Start();
        try
        {
            var other = Dns.GetHostAddresses(Dns.GetHostName())
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
            if (other is null) return; // no second address on this machine to try
            using var c = new TcpClient();
            Assert.ThrowsAny<SocketException>(() => c.Connect(other, server.Port));
        }
        finally { server.Stop(); }
    }
}

public class DiscoveryTrustTests
{
    [Theory]
    [InlineData("http://192.0.2.175:55000/dmr/ddd.xml", true)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("https://192.0.2.175/x", false)]
    [InlineData("http://192.0.2.17/x", false)]
    [InlineData("http://192.0.2.1750/x", false)]
    [InlineData("http://evil.example/x", false)]
    [InlineData("http://192.0.2.175@evil.example/x", false)]
    public void LocationMustBeHttpOnTheAnsweringHost(string url, bool ok) =>
        Assert.Equal(ok, DeviceDescription.IsOnDevice(new Uri(url), "192.0.2.175"));

    [Fact]
    public async Task FetchRefusesForeignLocation()
    {
        var hit = new SsdpResponse("192.0.2.175", "file:///etc/hosts", "", "");
        await Assert.ThrowsAsync<DeviceDescriptionException>(() => DeviceDescription.FetchAsync(hit));
    }

    /// <summary>Control URLs pointing at another host (directly, or via URLBase) are dropped.</summary>
    [Fact]
    public void DropsControlUrlsOnOtherHosts()
    {
        var xml = "<root xmlns=\"urn:schemas-upnp-org:device-1-0\"><URLBase>http://10.0.0.99:8080/</URLBase><device>"
            + "<friendlyName>X</friendlyName><serviceList>"
            + $"<service><serviceType>{Dlna.AVTransport}</serviceType><controlURL>avt/ctl</controlURL></service>"
            + $"<service><serviceType>{Dlna.ConnectionManager}</serviceType><controlURL>file:///tmp/x</controlURL></service>"
            + "</serviceList></device></root>";
        var r = DeviceDescription.Parse(Encoding.UTF8.GetBytes(xml), "http://10.0.0.5:1/desc.xml", "10.0.0.5");
        Assert.Null(r.AVTransport);
        Assert.Null(r.ConnectionManager);
    }

    /// <summary>.NET addition: a description with a DTD (entity-expansion bombs need one) is refused.</summary>
    [Fact]
    public void RefusesDtds()
    {
        var xml = "<?xml version=\"1.0\"?><!DOCTYPE root [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;\">]>"
            + "<root xmlns=\"urn:schemas-upnp-org:device-1-0\"><device><friendlyName>&b;</friendlyName></device></root>";
        Assert.Throws<DeviceDescriptionException>(() =>
            DeviceDescription.Parse(Encoding.UTF8.GetBytes(xml), "http://10.0.0.5:1/d.xml", "10.0.0.5"));
    }

    /// <summary>.NET addition: a description URL that redirects (here to another host) is not followed.</summary>
    [Fact]
    public async Task DoesNotFollowRedirects()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        var serve = Task.Run(async () =>
        {
            using var s = await l.AcceptSocketAsync();
            var buf = new byte[4096];
            await s.ReceiveAsync(buf, SocketFlags.None);
            await s.SendAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 302 Found\r\nLocation: http://192.0.2.10/desc.xml\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), SocketFlags.None);
        });
        try
        {
            var hit = new SsdpResponse("127.0.0.1", $"http://127.0.0.1:{port}/desc.xml", "", "");
            await Assert.ThrowsAsync<HttpRequestException>(() => DeviceDescription.FetchAsync(hit));
        }
        finally
        {
            await serve;
            l.Stop();
        }
    }

    private static Renderer R(string ip, string name) => new($"http://{ip}/d.xml", ip, name, "", new Dictionary<string, Uri>());

    /// <summary>Ported from the CLI's TV choice: an exact IP, or a name only one renderer has.</summary>
    [Fact]
    public void ChoosesOnlyUnambiguousRenderers()
    {
        var tv = R("192.0.2.175", "Living Room TV");
        var fake = R("192.0.2.66", "Living Room TV (2)");
        var other = R("192.0.2.7", "Kitchen speaker");
        Assert.Equal(tv, Discovery.Choose([tv], null));
        Assert.Equal(tv, Discovery.Choose([tv, fake, other], "192.0.2.175"));
        Assert.Equal(other, Discovery.Choose([tv, fake, other], "kitchen"));
        // "192.0.2.17" is no longer a substring match for .175
        Assert.Throws<RendererChoiceException>(() => Discovery.Choose([tv, fake, other], "192.0.2.17"));
        var e = Assert.Throws<RendererChoiceException>(() => Discovery.Choose([tv, fake, other], "living room"));
        Assert.Contains("Several renderers match", e.Message);
        Assert.Throws<RendererChoiceException>(() => Discovery.Choose([tv, fake], null));
        Assert.Throws<RendererChoiceException>(() => Discovery.Choose([], null));
    }
}
