using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace TylerCast.Core;

public sealed class SoapException(string action, int httpStatus, string? upnpCode, string? upnpDescription, string body)
    : Exception(Describe(action, httpStatus, upnpCode, upnpDescription))
{
    public string Action { get; } = action;
    public int HttpStatus { get; } = httpStatus;
    public string? UpnpCode { get; } = upnpCode;
    public string? UpnpDescription { get; } = upnpDescription;
    public string Body { get; } = body;

    private static string Describe(string action, int status, string? code, string? desc) =>
        $"{action} refused: HTTP {status}" + (code is null ? "" : $", UPnP error {code}") + (desc is null ? "" : $" {desc}");
}

public static class Soap
{
    // No redirects: the call (which carries the stream URL) goes only where discovery said.
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(10),
        MaxResponseContentBufferSize = 1_000_000,
    };

    /// <summary>Escapes as Python's html.escape(quote=True), which mirror.py used.</summary>
    public static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(c switch
            {
                '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '"' => "&quot;", '\'' => "&#x27;", _ => c.ToString(),
            });
        }
        return sb.ToString();
    }

    public static string Unescape(string s) =>
        s.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&#x27;", "'")
         .Replace("&apos;", "'").Replace("&amp;", "&");

    /// <summary>Argument order matters to some renderers, so it is a list, not a dictionary.</summary>
    public static string Envelope(string service, string action, IEnumerable<(string Name, string Value)> args)
    {
        var body = string.Concat(args.Select(a => $"<{a.Name}>{Escape(a.Value)}</{a.Name}>"));
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
            + "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" "
            + "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>"
            + $"<u:{action} xmlns:u=\"{service}\">{body}</u:{action}>"
            + "</s:Body></s:Envelope>";
    }

    public static IReadOnlyDictionary<string, string> Headers(string service, string action) => new Dictionary<string, string>
    {
        ["Content-Type"] = "text/xml; charset=\"utf-8\"",
        ["SOAPACTION"] = $"\"{service}#{action}\"",
    };

    public static string Didl(string url, string protocolInfo, string title = "TylerCast") =>
        "<DIDL-Lite xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\" "
        + "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" "
        + "xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\">"
        + "<item id=\"0\" parentID=\"-1\" restricted=\"1\">"
        + $"<dc:title>{Escape(title)}</dc:title>"
        + "<upnp:class>object.item.videoItem</upnp:class>"
        + $"<res protocolInfo=\"{Escape(protocolInfo)}\">{Escape(url)}</res>"
        + "</item></DIDL-Lite>";

    /// <summary>Text of the first &lt;name&gt;…&lt;/name&gt; (any namespace prefix), unescaped.</summary>
    public static string? Element(string name, string xml)
    {
        var n = Regex.Escape(name);
        var m = Regex.Match(xml, $@"<(?:[A-Za-z0-9_]+:)?{n}(?:\s[^>]*)?>(.*?)</(?:[A-Za-z0-9_]+:)?{n}>", RegexOptions.Singleline);
        return m.Success ? Unescape(m.Groups[1].Value) : null;
    }

    public static SoapException Fault(string action, int status, string body) =>
        new(action, status, Element("errorCode", body), Element("errorDescription", body), body);

    public static async Task<string> CallAsync(Uri url, string service, string action,
        IEnumerable<(string Name, string Value)> args, bool quiet = false, CancellationToken ct = default)
    {
        var env = Envelope(service, action, args);
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        // Set as-is: HttpClient would otherwise rewrite the quoted charset and the quoted SOAPACTION.
        req.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(env));
        req.Content.Headers.TryAddWithoutValidation("Content-Type", "text/xml; charset=\"utf-8\"");
        req.Headers.TryAddWithoutValidation("SOAPACTION", $"\"{service}#{action}\"");
        req.Headers.ExpectContinue = false;
        Action<string> note = quiet ? Log.Debug : Log.Info;
        note($"SOAP -> {action} {url}");
        Log.Debug($"SOAP request body:\n{env}");
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        var status = (int)resp.StatusCode;
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (status is < 200 or >= 300)
        {
            var e = Fault(action, status, body);
            Log.Error($"SOAP <- {action} HTTP {status}  UPnP error {e.UpnpCode ?? "?"} {e.UpnpDescription ?? ""}\n{body}");
            throw e;
        }
        note($"SOAP <- {action} HTTP {status}");
        Log.Debug($"SOAP reply:\n{body}");
        return body;
    }
}

/// <summary>The three AVTransport calls used, plus state polling.</summary>
public sealed class AVTransport(Uri url)
{
    public Uri Url { get; } = url;

    public Task StopAsync(CancellationToken ct = default) =>
        Soap.CallAsync(Url, Dlna.AVTransport, "Stop", [("InstanceID", "0")], ct: ct);

    public Task SetUriAsync(string uri, string metadata, CancellationToken ct = default) =>
        Soap.CallAsync(Url, Dlna.AVTransport, "SetAVTransportURI",
            [("InstanceID", "0"), ("CurrentURI", uri), ("CurrentURIMetaData", metadata)], ct: ct);

    public Task PlayAsync(CancellationToken ct = default) =>
        Soap.CallAsync(Url, Dlna.AVTransport, "Play", [("InstanceID", "0"), ("Speed", "1")], ct: ct);

    public async Task<string?> TransportStateAsync(CancellationToken ct = default)
    {
        // every 5s; the full exchange is still in the log file
        var r = await Soap.CallAsync(Url, Dlna.AVTransport, "GetTransportInfo", [("InstanceID", "0")], quiet: true, ct: ct)
            .ConfigureAwait(false);
        return Soap.Element("CurrentTransportState", r);
    }
}
