using System.Net;
using System.Text;
using TylerCast.Core;

namespace TylerCast.Core.Tests;

public class ClipTests
{
    [Fact]
    public void EmbeddedClipIsFourSecondsWithTwoKeyframes()
    {
        var c = new ClipVideoSource();
        Assert.Equal(100, c.AccessUnits.Count);
        Assert.Equal([0, 50], c.AccessUnits.Select((a, i) => (a, i)).Where(x => x.a.Key).Select(x => x.i));
        // Every unit starts with an access unit delimiter, as the muxer expects.
        Assert.All(c.AccessUnits, a => Assert.Equal(new byte[] { 0, 0, 0, 1, 0x09 }, a.Au[..5]));
    }

    [Fact]
    public void KeyframeRequestJumpsToTheNextIdr()
    {
        var c = new ClipVideoSource();
        var got = new List<bool>();
        var done = new ManualResetEventSlim();
        c.Start((_, _, key) =>
        {
            lock (got)
            {
                got.Add(key);
                if (got.Count == 3) c.RequestKeyframe();
                if (got.Count == 5) done.Set();
            }
        });
        Assert.True(done.Wait(TimeSpan.FromSeconds(5)));
        c.Stop();
        lock (got)
        {
            Assert.True(got[0]);  // starts on the IDR
            Assert.False(got[1]);
            Assert.True(got[3]);  // the frame after the request is a keyframe
        }
    }
}

/// <summary>A fake TV: answers AVTransport SOAP on 127.0.0.1, records the actions, and can go silent.</summary>
internal sealed class FakeTv : IDisposable
{
    private readonly HttpListener _http = new();
    public List<string> Actions { get; } = [];
    public string State { get; set; } = "PLAYING";
    /// <summary>SetAVTransportURI answers UPnP 716 this many times first (the TV can't fetch the stream).</summary>
    public int Refuse716 { get; set; }
    /// <summary>Stops answering altogether (connection refused): as a TV gone to standby or off the network.</summary>
    public void GoSilent() => _http.Stop();
    public Uri ControlUrl { get; }

    public FakeTv()
    {
        var port = new Random().Next(20_000, 40_000);
        _http.Prefixes.Add($"http://127.0.0.1:{port}/");
        _http.Start();
        ControlUrl = new Uri($"http://127.0.0.1:{port}/dmr/control_2");
        _ = Serve();
    }

    private async Task Serve()
    {
        while (_http.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _http.GetContextAsync(); }
            catch (Exception) { return; }
            var action = (ctx.Request.Headers["SOAPACTION"] ?? "").Trim('"').Split('#').Last();
            lock (Actions) Actions.Add(action);
            var body = action == "GetTransportInfo"
                ? $"<s:Envelope><s:Body><u:GetTransportInfoResponse><CurrentTransportState>{State}</CurrentTransportState></u:GetTransportInfoResponse></s:Body></s:Envelope>"
                : "<s:Envelope><s:Body/></s:Envelope>";
            if (action == "SetAVTransportURI" && Refuse716 > 0)
            {
                Refuse716--;
                ctx.Response.StatusCode = 500;
                body = "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><s:Fault><faultcode>s:Client</faultcode>"
                    + "<faultstring>UPnPError</faultstring><detail><UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\">"
                    + "<errorCode>716</errorCode><errorDescription>Resource not found</errorDescription></UPnPError></detail></s:Fault></s:Body></s:Envelope>";
            }
            var b = Encoding.UTF8.GetBytes(body);
            ctx.Response.ContentType = "text/xml";
            await ctx.Response.OutputStream.WriteAsync(b);
            ctx.Response.Close();
        }
    }

    public void Dispose() => _http.Close();
}

[Collection("network")]
public class SessionTests
{
    private static MirrorOptions Options => new() { TestPattern = true, Port = 0 };

    private static async Task<bool> WaitFor(Func<bool> cond, double seconds = 10)
    {
        for (var i = 0; i < seconds * 20 && !cond(); i++) await Task.Delay(50);
        return cond();
    }

    [Fact]
    public async Task PlaysThenStopsTheTv()
    {
        using var tv = new FakeTv();
        var s = new MirrorSession(Options, o => new ClipVideoSource());
        var awake = new List<bool>();
        s.KeepAwake = awake.Add;
        await s.StartAsync(Renderer.Manual("127.0.0.1", tv.ControlUrl));
        Assert.IsType<MirrorSession.State.Playing>(s.Current);
        Assert.Equal(["Stop", "SetAVTransportURI", "Play"], tv.Actions.Take(3));
        // Bound to the address facing the TV, for the TV only, on a random path.
        Assert.Equal("127.0.0.1", s.Pipeline!.Server!.AllowedPeer);
        Assert.Equal("127.0.0.1", s.Pipeline.Server.BindHost);
        Assert.NotEqual("/stream.ts", s.Pipeline.StreamPath);
        await s.StopAsync();
        Assert.Equal("Stop", tv.Actions.Last());
        Assert.IsType<MirrorSession.State.Stopped>(s.Current);
        Assert.Equal([true, false], awake);
    }

    /// <summary>Ported behaviour from f73a5f2: a TV that stops answering ends the session after 3 missed checks.</summary>
    [Fact]
    public async Task StopsAfterThreeMissedStateChecks()
    {
        var interval = MirrorSession.PollInterval;
        MirrorSession.PollInterval = TimeSpan.FromMilliseconds(100);
        try
        {
            using var tv = new FakeTv();
            var s = new MirrorSession(Options, o => new ClipVideoSource());
            await s.StartAsync(Renderer.Manual("127.0.0.1", tv.ControlUrl));
            Assert.True(await WaitFor(() => { lock (tv.Actions) return tv.Actions.Contains("GetTransportInfo"); }));
            Assert.IsType<MirrorSession.State.Playing>(s.Current);
            tv.GoSilent();
            Assert.True(await WaitFor(() => s.Current is MirrorSession.State.Failed, 60));
            Assert.Equal("The TV stopped answering", ((MirrorSession.State.Failed)s.Current).Why);
            Assert.Null(s.Pipeline); // nothing left serving the screen
        }
        finally { MirrorSession.PollInterval = interval; }
    }

    [Fact]
    public async Task EndsWhenTheTvStopsTheStream()
    {
        var interval = MirrorSession.PollInterval;
        MirrorSession.PollInterval = TimeSpan.FromMilliseconds(100);
        try
        {
            using var tv = new FakeTv();
            var s = new MirrorSession(Options, o => new ClipVideoSource());
            await s.StartAsync(Renderer.Manual("127.0.0.1", tv.ControlUrl));
            Assert.True(await WaitFor(() => { lock (tv.Actions) return tv.Actions.Contains("GetTransportInfo"); }));
            tv.State = "STOPPED";
            Assert.True(await WaitFor(() => s.Current is MirrorSession.State.Failed));
            Assert.Equal("The TV stopped the stream", ((MirrorSession.State.Failed)s.Current).Why);
        }
        finally { MirrorSession.PollInterval = interval; }
    }

    [Fact]
    public async Task WaitsForTheTvToReachThisComputer()
    {
        using var tv = new FakeTv { Refuse716 = 2 };
        var s = new MirrorSession(Options with { ReachWait = TimeSpan.FromSeconds(20) }, o => new ClipVideoSource());
        var seen = new List<MirrorSession.State>();
        s.StateChanged += seen.Add;
        await s.StartAsync(Renderer.Manual("127.0.0.1", tv.ControlUrl));
        Assert.IsType<MirrorSession.State.Playing>(s.Current);
        Assert.Contains(seen, x => x is MirrorSession.State.Connecting { What: MirrorSession.WaitingForReach });
        await s.StopAsync();
    }

    [Fact]
    public async Task GivesUpAtOnceWithoutReachWait()
    {
        using var tv = new FakeTv { Refuse716 = 1 };
        var s = new MirrorSession(Options, o => new ClipVideoSource());
        await s.StartAsync(Renderer.Manual("127.0.0.1", tv.ControlUrl));
        Assert.Equal("The TV can't reach this computer", Assert.IsType<MirrorSession.State.Failed>(s.Current).Why);
    }

    [Fact]
    public void ExplainsErrorsPlainly()
    {
        Assert.Equal("The TV can't reach this computer", MirrorSession.Explain(new SoapException("Play", 500, "716", "x", ""), "TV"));
        Assert.Equal("The TV didn't answer", MirrorSession.Explain(new HttpRequestException("refused"), "TV"));
        Assert.Equal("Can't reach the TV", MirrorSession.Explain(new PipelineException("can't reach the TV"), "TV"));
    }

    [Fact]
    public async Task SoundCaptureIsRefusedUntilBuilt()
    {
        var p = new Pipeline(new MirrorOptions { Audio = MirrorOptions.AudioKind.Aac }, new ClipVideoSource());
        await Assert.ThrowsAsync<PipelineException>(() => p.StartAsync(serve: false));
    }
}
