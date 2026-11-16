using System.Net;
using System.Threading.Channels;
using System.Xml;
using System.Xml.Linq;

namespace TylerCast.Core;

/// <summary>A renderer from its UPnP device description.</summary>
public sealed record Renderer(string Location, string Ip, string Name, string Model, IReadOnlyDictionary<string, Uri> Services)
{
    public string Id => Location;
    public Uri? AVTransport => Services.GetValueOrDefault(Dlna.AVTransport);
    public Uri? ConnectionManager => Services.GetValueOrDefault(Dlna.ConnectionManager);

    /// <summary>For a TV whose address is known (skips SSDP): AVTransport at the given control URL.</summary>
    public static Renderer Manual(string ip, Uri avTransport) =>
        new(avTransport.AbsoluteUri, ip, ip, "", new Dictionary<string, Uri> { [Dlna.AVTransport] = avTransport });
}

public sealed class DeviceDescriptionException(string message) : Exception(message);

public sealed class RendererChoiceException(string message) : Exception(message);

public static class DeviceDescription
{
    /// <summary>The largest description accepted; real ones are a few kB.</summary>
    public const int MaxDescriptionBytes = 1_000_000;

    // No redirects: a device could otherwise answer from its own address and send
    // the fetch elsewhere, getting round IsOnDevice.
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(5),
        MaxResponseContentBufferSize = MaxDescriptionBytes,
    };

    /// <summary>
    /// True for an http URL whose host is <paramref name="ip"/>. Any device on the network can
    /// answer SSDP, so its LOCATION and control URLs must point back at itself:
    /// no file:// reads, no requests to other hosts on its say-so.
    /// </summary>
    public static bool IsOnDevice(Uri url, string ip) =>
        url.IsAbsoluteUri && url.Scheme == Uri.UriSchemeHttp && url.Host == ip;

    /// <summary>
    /// Parses the description XML; relative control URLs are resolved against
    /// URLBase if present, else the description's own URL (as urljoin does).
    /// </summary>
    public static Renderer Parse(byte[] data, string location, string ip)
    {
        XDocument doc;
        try
        {
            // No DTDs: a description never needs one, and refusing them rules out
            // entity-expansion tricks from whoever answered SSDP.
            using var ms = new MemoryStream(data);
            using var reader = XmlReader.Create(ms, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            doc = XDocument.Load(reader);
        }
        catch (XmlException e) { throw new DeviceDescriptionException($"bad XML: {e.Message}"); }

        var all = doc.Descendants().ToList();
        string? Text(XElement? e) => e?.Value.Trim();
        var urlBase = Text(all.FirstOrDefault(e => e.Name.LocalName == "URLBase"));
        var baseString = string.IsNullOrEmpty(urlBase) ? location : urlBase;
        if (!Uri.TryCreate(baseString, UriKind.Absolute, out var baseUri) && !Uri.TryCreate(location, UriKind.Absolute, out baseUri))
            throw new DeviceDescriptionException($"bad base URL {baseString}");

        // First device only (the root renderer), as mirror.py's find(".//device").
        string? First(string name) => Text(all.FirstOrDefault(e => e.Name.LocalName == name && e.Parent?.Name.LocalName == "device"));

        var services = new Dictionary<string, Uri>();
        foreach (var s in all.Where(e => e.Name.LocalName == "service"))
        {
            var type = Text(s.Elements().FirstOrDefault(e => e.Name.LocalName == "serviceType"));
            var ctl = Text(s.Elements().FirstOrDefault(e => e.Name.LocalName == "controlURL"));
            if (type is null || ctl is null || !Uri.TryCreate(baseUri, ctl, out var url)) continue;
            // SOAP calls (with the stream URL) go only to the device that answered SSDP.
            if (!IsOnDevice(url, ip))
            {
                Log.Warn($"Ignoring {type} at {url}: not plain http on {ip}");
                continue;
            }
            services[type] = url;
        }
        var model = string.Join(" ", new[] { First("manufacturer"), First("modelName") }.Where(x => x is not null)).Trim();
        return new Renderer(location, ip, First("friendlyName") ?? ip, model, services);
    }

    public static async Task<Renderer> FetchAsync(SsdpResponse hit, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(hit.Location, UriKind.Absolute, out var url))
            throw new DeviceDescriptionException($"bad LOCATION {hit.Location}");
        if (!IsOnDevice(url, hit.Ip))
            throw new DeviceDescriptionException($"LOCATION {hit.Location} is not plain http on {hit.Ip}, the address that answered");
        var data = await Http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
        Log.Debug($"Device description {hit.Location}:\n{Log.Body(System.Text.Encoding.UTF8.GetString(data))}");
        return Parse(data, hit.Location, hit.Ip);
    }
}

public static class Discovery
{
    public const int MaxParallelFetches = 4, MaxAnswersPerIp = 4, MaxAnswers = 32;
    /// <summary>Seconds after listening ends that descriptions may still take.</summary>
    public const double DescriptionWait = 6;

    /// <summary>
    /// The renderer <paramref name="want"/> names: an exact IP, or a name that only one renderer
    /// has (null: the only renderer). Any device can answer SSDP with any name, so an
    /// ambiguous choice is refused, not guessed.
    /// </summary>
    public static Renderer Choose(IReadOnlyList<Renderer> all, string? want)
    {
        var matches = all.Where(r => want is null || r.Ip == want
            || r.Name.Contains(want, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 1) return matches[0];
        if (matches.Count == 0) throw new RendererChoiceException($"No renderer with AVTransport matched {want ?? ""}");
        var list = string.Join(", ", matches.Select(r => $"{r.Name} ({r.Ip})"));
        throw new RendererChoiceException($"Several renderers match: {list}. Pick one with --tv IP");
    }

    /// <summary>
    /// SSDP search; each answer's description is fetched as it arrives, and
    /// <paramref name="onFound"/> hears about each renderer straight away (a TV usually answers
    /// in ~0.1s; the search keeps listening for <paramref name="timeout"/>). Renderers without
    /// AVTransport are logged and skipped.
    /// </summary>
    public static async Task<List<Renderer>> RenderersAsync(double timeout = 3, string st = Dlna.RendererST,
        Func<Renderer, Task>? onFound = null, IPAddress? localAddress = null, CancellationToken ct = default)
    {
        var answers = Channel.CreateUnbounded<SsdpResponse>();
        _ = Task.Run(() =>
        {
            try { Ssdp.Search(st, timeout, r => answers.Writer.TryWrite(r), localAddress); }
            catch (Exception e) { Log.Error($"SSDP search failed: {e.Message}"); }
            finally { answers.Writer.TryComplete(); }
        }, ct);
        // Any device can answer, as often as it likes (security review, finding 4): descriptions
        // are fetched a few at a time, at most a few per address and a few dozen in all, and
        // the whole search ends a few seconds after listening does.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeout + DescriptionWait));
        var slots = new SemaphoreSlim(MaxParallelFetches);
        var perIp = new Dictionary<string, int>();
        var output = new List<Renderer>();
        var fetches = new List<Task>();
        var total = 0;
        try
        {
            await foreach (var h in answers.Reader.ReadAllAsync(deadline.Token).ConfigureAwait(false))
            {
                var n = perIp.GetValueOrDefault(h.Ip);
                if (n >= MaxAnswersPerIp || total >= MaxAnswers)
                {
                    if (n == MaxAnswersPerIp || total == MaxAnswers) Log.Warn($"Ignoring further SSDP answers ({h.Ip} sent {n + 1}, {total + 1} in all)");
                    perIp[h.Ip] = n + 1;
                    total++;
                    continue;
                }
                perIp[h.Ip] = n + 1;
                total++;
                fetches.Add(Fetch(h));
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        await Task.WhenAll(fetches).ConfigureAwait(false);
        lock (output) return [.. output];

        async Task Fetch(SsdpResponse h)
        {
            try
            {
                await slots.WaitAsync(deadline.Token).ConfigureAwait(false);
                try
                {
                    var r = await DeviceDescription.FetchAsync(h, deadline.Token).ConfigureAwait(false);
                    var names = r.Services.Keys.Select(k => k.Split(':') is var p && p.Length >= 2 ? p[^2] : k).Order();
                    Log.Info($"Renderer: {r.Name} [{r.Model}] at {r.Ip} services=[{string.Join(", ", names)}]");
                    if (r.AVTransport is null) return;
                    lock (output) output.Add(r);
                    if (onFound is not null) await onFound(r).ConfigureAwait(false);
                }
                finally { slots.Release(); }
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or DeviceDescriptionException or WebException)
            {
                if (!ct.IsCancellationRequested) Log.Warn($"Could not read {h.Location}: {e.Message}");
            }
        }
    }
}
