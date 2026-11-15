using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TylerCast.Core;

public sealed record SsdpResponse(string Ip, string Location, string Server, string ST);

/// <summary>SSDP M-SEARCH over a plain UDP socket (multicast out, unicast replies back).</summary>
public static class Ssdp
{
    public const string Address = "239.255.255.250";
    public const int Port = 1900;

    public static string SearchMessage(string st = Dlna.RendererST, int mx = 2) =>
        "M-SEARCH * HTTP/1.1\r\n"
        + $"HOST: {Address}:{Port}\r\n"
        + "MAN: \"ssdp:discover\"\r\n"
        + $"MX: {mx}\r\n"
        + $"ST: {st}\r\n\r\n";

    /// <summary>Headers of an SSDP reply, keys lowercased. Null if it has no LOCATION.</summary>
    public static SsdpResponse? ParseResponse(string text, string fromIp)
    {
        var headers = new Dictionary<string, string>();
        foreach (var line in text.Split("\r\n").Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
        }
        if (!headers.TryGetValue("location", out var loc) || loc.Length == 0) return null;
        return new SsdpResponse(fromIp, loc, headers.GetValueOrDefault("server", ""), headers.GetValueOrDefault("st", ""));
    }

    /// <summary>The socket error of the last failed M-SEARCH send, if any.</summary>
    public static SocketError? LastSendError { get; private set; }

    /// <summary>
    /// Blocking search; logs every reply. Asks twice because UDP is lossy.
    /// <paramref name="onResponse"/> is called (on this thread) for each new answer as it arrives.
    /// <paramref name="localAddress"/> picks the interface the search goes out on (a PC with
    /// Hyper-V, WSL or VPN adapters may otherwise send it somewhere the TV isn't).
    /// </summary>
    public static List<SsdpResponse> Search(string st = Dlna.RendererST, double timeout = 3,
        Action<SsdpResponse>? onResponse = null, IPAddress? localAddress = null)
    {
        LastSendError = null;
        var found = new List<SsdpResponse>();
        using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
        sock.ReceiveTimeout = 300;
        if (localAddress is not null)
        {
            sock.Bind(new IPEndPoint(localAddress, 0));
            sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, localAddress.GetAddressBytes());
        }
        var msg = Encoding.ASCII.GetBytes(SearchMessage(st));
        var dest = new IPEndPoint(IPAddress.Parse(Address), Port);
        Log.Debug($"SSDP M-SEARCH{(localAddress is null ? "" : $" via {localAddress}")}:\n{SearchMessage(st).Trim()}");
        for (var i = 0; i < 2; i++)
        {
            try { sock.SendTo(msg, dest); }
            catch (SocketException e)
            {
                LastSendError = e.SocketErrorCode;
                Log.Error($"SSDP: send failed: {e.SocketErrorCode} {e.Message}");
            }
        }

        var seen = new HashSet<string>();
        var end = DateTime.UtcNow.AddSeconds(timeout);
        var buf = new byte[65507];
        while (DateTime.UtcNow < end)
        {
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            int n;
            try { n = sock.ReceiveFrom(buf, ref from); }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.TimedOut or SocketError.WouldBlock
                                                or SocketError.ConnectionReset or SocketError.Interrupted)
            {
                // ConnectionReset: Windows reports an ICMP "port unreachable" on a UDP socket this way.
                continue;
            }
            if (n <= 0) continue;
            var ip = ((IPEndPoint)from).Address.ToString();
            var text = Encoding.UTF8.GetString(buf, 0, n);
            Log.Debug($"SSDP response from {ip}:\n{text.Trim()}");
            var r = ParseResponse(text, ip);
            if (r is null || !seen.Add(r.Location)) continue;
            Log.Info($"SSDP: {ip} {r.Location} ({r.Server})");
            found.Add(r);
            onResponse?.Invoke(r);
        }
        return found;
    }

    /// <summary>The local address the OS would use to reach <paramref name="ip"/> (no packet is sent).</summary>
    public static string? LocalAddressTowards(string ip)
    {
        if (!IPAddress.TryParse(ip, out var target)) return null;
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(new IPEndPoint(target, 9));
            return (s.LocalEndPoint as IPEndPoint)?.Address.ToString();
        }
        catch (SocketException) { return null; }
    }
}
