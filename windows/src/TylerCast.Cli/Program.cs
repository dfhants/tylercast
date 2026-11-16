using System.Globalization;
using System.Net;
using TylerCast.Core;

// TylerCast command line: the Swift app's CLI modes, for debugging and for the
// Windows test rounds. Screen capture comes with the Windows project; until then
// the video is the pre-encoded test pattern (--test-pattern), which runs anywhere.

const string Usage = """
    tylercast: mirror this computer's screen to a DLNA TV.

      discover [--timeout S] [--st ssdp:all] [--via LOCAL_IP]
      play     [--tv IP|name] [--avt URL] [stream options]   mirror to a TV until Ctrl-C (sends Stop on exit)
      serve    [stream options]                     no TV; open the URL it prints (random path)
      record   FILE [--seconds N] [stream options]  no TV, no server; check with ffprobe

    stream options:
      --test-pattern     colour bars (pre-encoded clip) instead of the screen
      --margin PCT       black border on each edge, beats TV overscan (try 3-5)
      --audio KIND       aac (default: the computer's sound), silent-ac3, none (= --no-audio)
      --bitrate 6M       video;  --muxrate 20M  constant TS rate (null padding)
      --delay 0.15       seconds of capture-to-DTS slack; raise if the log shows late frames
      --mime video/mpeg  --no-pn  --tlag  --no-metadata  --port 8090
      -v                 full SSDP/SOAP/HTTP bodies on stderr (always in the log file)
    """;

string[] valued = ["--tv", "--avt", "--margin", "--bitrate", "--muxrate", "--delay", "--mime", "--port", "--seconds",
                   "--timeout", "--st", "--audio", "--via"];
var positional = new List<string>();
var values = new Dictionary<string, string>();
var flags = new HashSet<string>();
for (var i = 0; i < args.Length; i++)
{
    var x = args[i];
    if (valued.Contains(x))
    {
        if (i + 1 >= args.Length) return Fail($"{x} needs a value");
        values[x] = args[++i];
    }
    else if (x.StartsWith('-')) flags.Add(x);
    else positional.Add(x);
}
Log.Verbose = flags.Contains("-v") || flags.Contains("--verbose");
var cmd = positional.Count > 0 ? positional[0] : "help";
var rest = positional.Skip(1).ToList();

try
{
    return cmd switch
    {
        "discover" => await Discover(),
        "play" => await Play(),
        "serve" => await Serve(),
        "record" => await Record(),
        "dump-pattern" => DumpPattern(),
        _ => Help(),
    };
}
catch (Exception e) when (e is PipelineException or RendererChoiceException or FormatException or ArgumentException)
{
    return Fail(e.Message);
}

static int Fail(string msg)
{
    Log.Error(msg);
    return 1;
}

int Help()
{
    Console.WriteLine(Usage);
    Console.WriteLine($"Log file: {Log.FilePath}");
    return cmd is "help" or "-h" or "--help" ? 0 : 2;
}

static int Rate(string s)
{
    var u = s.ToUpperInvariant();
    var mult = u.EndsWith('M') ? 1e6 : u.EndsWith('K') ? 1e3 : 1;
    if (!double.TryParse(u.TrimEnd('M', 'K'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        throw new FormatException($"bad rate {s}");
    return (int)(v * mult);
}

double Number(string name, double fallback) =>
    values.TryGetValue(name, out var s)
        ? double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : throw new FormatException($"bad {name} {s}")
        : fallback;

MirrorOptions Options()
{
    var o = new MirrorOptions();
    var audio = o.Audio;
    if (values.TryGetValue("--audio", out var k))
        audio = MirrorOptions.ParseAudio(k) ?? throw new FormatException("--audio must be one of aac, silent-ac3, none");
    if (flags.Contains("--no-audio")) audio = MirrorOptions.AudioKind.None;
    o = o with
    {
        MarginPercent = Number("--margin", 0),
        Video = values.TryGetValue("--bitrate", out var b) ? o.Video with { Bitrate = Rate(b) } : o.Video,
        MuxRate = values.TryGetValue("--muxrate", out var m) ? Rate(m) : o.MuxRate,
        Delay = Number("--delay", o.Delay),
        Mime = values.GetValueOrDefault("--mime", o.Mime),
        Port = (int)Number("--port", o.Port),
        TestPattern = flags.Contains("--test-pattern"),
        Audio = audio,
        SoundOnTvOnly = !flags.Contains("--pc-sound"),
        SendProfile = !flags.Contains("--no-pn"),
        Tlag = flags.Contains("--tlag"),
        SendMetadata = !flags.Contains("--no-metadata"),
    };
    if (o.Video.Bitrate > o.MuxRate * 8L / 10) Log.Warn("--bitrate is close to --muxrate: keyframes will be late");
    return o;
}

// Screen capture is Windows code that doesn't exist yet; the test pattern runs anywhere.
static IVideoSource VideoFor(MirrorOptions o) =>
    o.TestPattern ? new ClipVideoSource() : throw new PipelineException("screen capture isn't built yet: use --test-pattern");

// Ctrl-C, SIGTERM, or (Windows) the console window closing / log-off / shutdown:
// stop cleanly, so the TV gets Stop. A second Ctrl-C gives up waiting.
// The registrations live as long as the process (kept in a static list).
static Task CtrlC()
{
    var tcs = new TaskCompletionSource();
    var fired = 0;
    foreach (var sig in new[] { System.Runtime.InteropServices.PosixSignal.SIGINT, System.Runtime.InteropServices.PosixSignal.SIGTERM,
                                System.Runtime.InteropServices.PosixSignal.SIGHUP, System.Runtime.InteropServices.PosixSignal.SIGQUIT })
    {
        Signals.Keep.Add(System.Runtime.InteropServices.PosixSignalRegistration.Create(sig, ctx =>
        {
            if (Interlocked.Exchange(ref fired, 1) == 1) return; // second signal: default action (exit)
            ctx.Cancel = true;
            Log.Info($"{ctx.Signal}: stopping");
            tcs.TrySetResult();
        }));
    }
    return tcs.Task;
}

async Task<int> Discover()
{
    var timeout = Number("--timeout", 4);
    var st = values.GetValueOrDefault("--st", Dlna.RendererST);
    var via = values.TryGetValue("--via", out var v) ? IPAddress.Parse(v) : null;
    Log.Info($"Log file: {Log.FilePath}");
    var hits = await Task.Run(() => Ssdp.Search(st, timeout, localAddress: via));
    Log.Info($"{hits.Count} response(s)");
    foreach (var h in hits)
    {
        Renderer d;
        try { d = await DeviceDescription.FetchAsync(h); }
        catch (Exception e)
        {
            Log.Warn($"  {h.Location}: description failed: {e.Message}");
            continue;
        }
        Log.Info($"  {d.Name} [{d.Model}] at {d.Ip}");
        foreach (var (type, url) in d.Services.OrderBy(s => s.Key, StringComparer.Ordinal)) Log.Info($"    {type} -> {url}");
        if (d.ConnectionManager is not { } cm) continue;
        try
        {
            // What the TV says it can play: the list to match protocolInfo against.
            var reply = await Soap.CallAsync(cm, Dlna.ConnectionManager, "GetProtocolInfo", []);
            foreach (var p in (Soap.Element("Sink", reply) ?? "").Split(','))
            {
                if (p.Contains("mpeg", StringComparison.OrdinalIgnoreCase) || p.Contains("mp2t", StringComparison.OrdinalIgnoreCase)
                    || p.Contains("AVC_TS", StringComparison.Ordinal))
                    Log.Info($"    sink: {p}");
            }
        }
        catch (Exception) { /* already logged by Soap.CallAsync */ }
    }
    return hits.Count > 0 ? 0 : 1;
}

async Task<Renderer> FindTv()
{
    if (values.TryGetValue("--avt", out var avt))
    {
        var u = new Uri(avt);
        return Renderer.Manual(u.Host, u);
    }
    var all = await Discovery.RenderersAsync(timeout: 3);
    if (all.Count == 0)
        throw new PipelineException("No MediaRenderer answered SSDP. Is the TV on (not standby), on this network, with DLNA remote on? "
            + "Or pass --avt http://IP:55000/dmr/control_2");
    // An exact IP, or a name that only one renderer has (any device can answer SSDP with any name).
    return Discovery.Choose(all, values.GetValueOrDefault("--tv"));
}

async Task<int> Play()
{
    var options = Options();
    var tv = await FindTv();
    var session = new MirrorSession(options, VideoFor);
    var ended = new TaskCompletionSource<int>();
    session.StateChanged += s =>
    {
        if (s is MirrorSession.State.Failed f) { Log.Error(f.Why); ended.TrySetResult(1); }
    };
    var ctrlC = CtrlC();
    await session.StartAsync(tv);
    if (!session.IsActive) return 1;
    Log.Info("Playing. Ctrl-C to stop.");
    if (await Task.WhenAny(ctrlC, ended.Task) == ctrlC)
    {
        await session.StopAsync();
        return 0;
    }
    return await ended.Task;
}

async Task<int> Serve()
{
    var p = new Pipeline(Options(), VideoFor(Options()));
    var ctrlC = CtrlC();
    await p.StartAsync(serve: true);
    Log.Info($"Serving http://{Ssdp.LocalAddressTowards("8.8.8.8") ?? "<this computer>"}:{p.Options.Port}{p.StreamPath} "
        + "to anyone on the network. Ctrl-C to stop.");
    await ctrlC;
    p.Stop();
    return 0;
}

async Task<int> Record()
{
    var path = rest.FirstOrDefault() ?? throw new ArgumentException("record needs a file name");
    var seconds = Number("--seconds", 10);
    var o = Options();
    var p = new Pipeline(o, VideoFor(o));
    long bytes = 0;
    await using (var f = File.Create(path))
    {
        var gate = new Lock();
        var id = p.Broadcaster.Subscribe((d, _) =>
        {
            if (d is null) return;
            lock (gate) { f.Write(d); bytes += d.Length; }
        });
        await p.StartAsync(serve: false);
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        p.Broadcaster.Unsubscribe(id);
        p.Stop();
        lock (gate) f.Flush();
    }
    Log.Info($"Wrote {bytes / 1000} kB to {path}. Check: ffprobe -show_streams {path}");
    return 0;
}

int DumpPattern() // dump-pattern N: N raw NV12 frames on stdout, for tools/make-test-clip.sh
{
    var tp = new TestPattern(1920, 1080);
    using var stdout = Console.OpenStandardOutput();
    var n = rest.Count > 0 ? int.Parse(rest[0], CultureInfo.InvariantCulture) : 100;
    for (var i = 0; i < n; i++) stdout.Write(tp.Frame(i));
    return 0;
}

static class Signals
{
    public static readonly List<IDisposable> Keep = [];
}
