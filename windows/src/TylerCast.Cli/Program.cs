using System.Globalization;
using System.Net;
using TylerCast.Core;

// TylerCast command line. Phase 1 has `discover` only: it needs no capture or
// encoder, so it runs the same on the Mac and on Windows. `serve`, `play`,
// `record`, `diag` and `selftest` follow once the Windows pipeline exists.

var argv = args.ToList();
if (argv.Remove("-v") | argv.Remove("--verbose")) Log.Verbose = true;
var cmd = argv.Count > 0 ? argv[0] : "help";
argv = argv.Skip(1).ToList();

string? Option(string name)
{
    var i = argv.IndexOf(name);
    if (i < 0 || i + 1 >= argv.Count) return null;
    var v = argv[i + 1];
    argv.RemoveRange(i, 2);
    return v;
}

switch (cmd)
{
    case "discover":
        return await Discover();
    case "help" or "-h" or "--help":
        Console.WriteLine("""
            tylercast discover [--timeout SECONDS] [--st TARGET] [--via LOCAL_IP] [-v]
                SSDP search; logs each renderer, its services and the formats it says it plays.
                --st ssdp:all lists every UPnP device; --via sends the search from one network adapter.
            """);
        return 0;
    default:
        Console.Error.WriteLine($"Unknown command '{cmd}'. Try: tylercast help");
        return 2;
}

async Task<int> Discover()
{
    var timeout = double.Parse(Option("--timeout") ?? "4", CultureInfo.InvariantCulture);
    var st = Option("--st") ?? Dlna.RendererST;
    var via = Option("--via") is { } v ? IPAddress.Parse(v) : null;
    Log.Info($"Log file: {Log.FilePath}");
    var hits = await Task.Run(() => Ssdp.Search(st, timeout, localAddress: via));
    Log.Info($"{hits.Count} response(s)");
    foreach (var h in hits)
    {
        Renderer d;
        try { d = await DeviceDescription.FetchAsync(h); }
        catch (Exception e)
        {
            Log.Warn($"  description failed: {e.Message}");
            continue;
        }
        Log.Info($"  {d.Name} [{d.Model}] at {d.Ip}");
        foreach (var (type, url) in d.Services) Log.Info($"    {type} -> {url}");
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
