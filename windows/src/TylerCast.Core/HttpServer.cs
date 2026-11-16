using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace TylerCast.Core;

public sealed record HttpRequestHead(string Method, string Path, string Version,
    IReadOnlyList<(string Name, string Value)> Headers, string Raw)
{
    public string? Header(string name) =>
        Headers.FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>Parses a request head; null until the blank line has arrived.</summary>
    public static HttpRequestHead? Parse(ReadOnlySpan<byte> data)
    {
        var end = data.IndexOf("\r\n\r\n"u8);
        if (end < 0) return null;
        var raw = Encoding.UTF8.GetString(data[..end]);
        var lines = raw.Split("\r\n");
        var parts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        var headers = new List<(string, string)>();
        foreach (var line in lines.Skip(1))
        {
            var c = line.IndexOf(':');
            if (c < 0) continue;
            headers.Add((line[..c].Trim(), line[(c + 1)..].Trim()));
        }
        return new HttpRequestHead(parts[0], parts[1], parts.Length > 2 ? parts[2] : "HTTP/1.0", headers, raw);
    }
}

public sealed record StreamHeaders
{
    public string ContentType { get; init; } = Dlna.DefaultMime;
    public string ContentFeatures { get; init; } = Dlna.ContentFeatures();
    /// <summary>realTimeInfo.dlna.org: DLNA.ORG_TLAG=* ("this is live").</summary>
    public bool Tlag { get; init; }

    /// <summary>HTTP/1.0, no Content-Length: the body ends when the connection closes.</summary>
    public string Ok()
    {
        var h = "HTTP/1.0 200 OK\r\n"
            + $"Server: {Dlna.ServerHeader}\r\n"
            + $"Content-Type: {ContentType}\r\n"
            + "transferMode.dlna.org: Streaming\r\n"
            + $"contentFeatures.dlna.org: {ContentFeatures}\r\n";
        if (Tlag) h += "realTimeInfo.dlna.org: DLNA.ORG_TLAG=*\r\n";
        return h + "Cache-Control: no-cache\r\nConnection: close\r\n\r\n";
    }

    public static string NotFound() =>
        $"HTTP/1.0 404 Not Found\r\nServer: {Dlna.ServerHeader}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";

    public static string BadRequest() => "HTTP/1.0 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
}

/// <summary>
/// HTTP/1.0 server for <c>/stream.ts</c>. Every GET joins the one running stream
/// (the TV sends HEAD, then a short GET it drops, then the real GET).
/// </summary>
public sealed class StreamServer(Broadcaster broadcaster, StreamHeaders headers, int port = 8090, string path = "/stream.ts",
    int maxBacklogBytes = 4_000_000, string? allowedPeer = null, string? bindHost = null)
{
    public int Port { get; private set; } = port;
    public string Path { get; } = path;
    /// <summary>When set, only this IPv4 address (the TV) may connect; anyone else is
    /// disconnected before a byte is read. The stream is the live screen and sound.</summary>
    public string? AllowedPeer { get; } = allowedPeer is null ? null : Normalize(allowedPeer);
    /// <summary>When set, listen only on this local address (the interface facing the
    /// TV), not on every interface.</summary>
    public string? BindHost { get; } = bindHost;

    /// <summary>"::ffff:192.0.2.5" and "192.0.2.5%en0" -> "192.0.2.5".</summary>
    public static string Normalize(string ip)
    {
        var s = ip.Split('%')[0];
        return s.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase) ? s[7..] : s;
    }

    /// <summary>A random, unguessable stream path for one session (122 random bits).</summary>
    public static string RandomPath() => $"/{Guid.NewGuid():D}.ts";
    public StreamHeaders Headers { get; set; } = headers;
    public Action<HttpRequestHead, string>? OnRequest { get; set; }
    /// <summary>
    /// When set, a new GET calls this (the pipeline forces a keyframe) and its
    /// data starts at the next keyframe, not mid-GOP. Many TVs hold their clock
    /// from connect until the first keyframe, so a mid-GOP join adds up to a
    /// whole keyframe interval (2s) of permanent delay.
    /// </summary>
    public Action? OnJoin { get; set; }

    private TcpListener? _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private readonly HashSet<Socket> _connections = [];

    /// <summary>
    /// Starts listening (port 0 picks a free one; <see cref="Port"/> then says which).
    /// The TV's last connection can hold the port for a while after a session, so
    /// "address in use" is retried for up to <paramref name="waitForPort"/> seconds.
    /// </summary>
    public void Start(double waitForPort = 35)
    {
        var deadline = DateTime.UtcNow.AddSeconds(waitForPort);
        var warned = false;
        while (true)
        {
            try
            {
                var l = new TcpListener(BindHost is null ? IPAddress.Any : IPAddress.Parse(BindHost), Port);
                // Windows: no other socket may bind this port alongside ours (and hear the TV).
                if (OperatingSystem.IsWindows()) l.ExclusiveAddressUse = true;
                l.Start();
                _listener = l;
                Port = ((IPEndPoint)l.LocalEndpoint).Port;
                break;
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse && DateTime.UtcNow < deadline)
            {
                if (!warned)
                {
                    Log.Warn($"Port {Port} busy (the TV's last connection is closing, or another copy is running); waiting up to {(int)waitForPort}s");
                    warned = true;
                }
                Thread.Sleep(1000);
            }
        }
        Log.Info($"HTTP server listening on {BindHost ?? "all interfaces"} port {Port}"
            + (AllowedPeer is null ? "" : $", for {AllowedPeer} only"));
        _ = AcceptLoop(_listener);
    }

    public void Stop()
    {
        _stop.Cancel();
        _listener?.Stop();
        _listener = null;
        Socket[] all;
        lock (_gate)
        {
            all = [.. _connections];
            _connections.Clear();
        }
        foreach (var s in all) s.Dispose();
    }

    /// <summary>When each refused address was last logged (a flood shouldn't fill the log).</summary>
    private readonly Dictionary<string, DateTime> _refusedLogged = [];

    private async Task AcceptLoop(TcpListener l)
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket s;
            try { s = await l.AcceptSocketAsync(_stop.Token).ConfigureAwait(false); }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            if (AllowedPeer is not null)
            {
                var from = Normalize(Peer(s));
                if (from != AllowedPeer)
                {
                    var now = DateTime.UtcNow;
                    if (!_refusedLogged.TryGetValue(from, out var last) || now - last > TimeSpan.FromSeconds(10))
                    {
                        _refusedLogged[from] = now;
                        Log.Warn($"Refused a connection from {from}: only the TV ({AllowedPeer}) may watch");
                    }
                    s.Dispose();
                    continue;
                }
            }
            s.NoDelay = true;
            lock (_gate) _connections.Add(s);
            _ = HandleConnection(s);
        }
    }

    private void Forget(Socket s)
    {
        lock (_gate) _connections.Remove(s);
        s.Dispose();
    }

    private static string Peer(Socket s) => (s.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";

    private async Task HandleConnection(Socket s)
    {
        var from = Peer(s);
        HttpRequestHead? req = null;
        try
        {
            var buf = new byte[65536];
            var n = 0;
            while (req is null)
            {
                var read = await s.ReceiveAsync(buf.AsMemory(n), _stop.Token).ConfigureAwait(false);
                if (read == 0) break;
                n += read;
                req = HttpRequestHead.Parse(buf.AsSpan(0, n));
                if (req is null && n >= buf.Length) break;
            }
            if (req is null)
            {
                if (n > 0) await Finish(s, StreamHeaders.BadRequest()).ConfigureAwait(false);
                else Forget(s);
                return;
            }
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException or ObjectDisposedException)
        {
            Forget(s);
            return;
        }

        Log.Info($"TV {req.Method} {req.Path} from {from}");
        Log.Debug($"TV request headers:\n{req.Raw}");
        OnRequest?.Invoke(req, from);
        var target = req.Path.Split('?')[0];
        if (target != Path || (req.Method != "GET" && req.Method != "HEAD"))
        {
            await Finish(s, StreamHeaders.NotFound()).ConfigureAwait(false);
            return;
        }
        if (req.Method == "HEAD")
        {
            await Finish(s, Headers.Ok()).ConfigureAwait(false);
            return;
        }
        Join(s, from, Encoding.ASCII.GetBytes(Headers.Ok()));
        OnJoin?.Invoke();
    }

    private async Task Finish(Socket s, string text)
    {
        try
        {
            await s.SendAsync(Encoding.ASCII.GetBytes(text), SocketFlags.None).ConfigureAwait(false);
            s.Shutdown(SocketShutdown.Send);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
        Forget(s);
    }

    /// <summary>Streams the live TS to one connection until either side ends it.</summary>
    private void Join(Socket s, string from, byte[] head)
    {
        var gate = new Lock();
        long pending = 0, sent = 0;
        var closed = false;
        var alignToKeyframe = OnJoin is not null;
        var waiting = alignToKeyframe;
        var joined = Stopwatch.StartNew();
        var lastReport = Stopwatch.StartNew();
        var outbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        outbox.Writer.TryWrite(head);
        Guid id = default;

        // graceful: send what is queued, then end the body (the stream itself ended).
        // Otherwise (TV gone, error, too far behind) the socket is closed at once.
        void Close(string why, bool graceful = false)
        {
            long total;
            lock (gate)
            {
                if (closed) return;
                closed = true;
                total = sent;
            }
            broadcaster.Unsubscribe(id);
            outbox.Writer.TryComplete();
            Log.Info($"TV connection from {from} ended after {(total / 1e6).ToString("0.0", CultureInfo.InvariantCulture)} MB: {why}");
            if (!graceful) Forget(s);
        }

        id = broadcaster.Subscribe((chunk, keyOffset) =>
        {
            if (chunk is null) { Close("stream ended", graceful: true); return; }
            var data = chunk;
            long? backlog = null;
            bool first;
            lock (gate)
            {
                if (closed) return;
                if (waiting)
                {
                    if (keyOffset is { } k)
                    {
                        data = chunk[k..];
                        waiting = false;
                        Log.Info($"TV {from} starts at a keyframe after {joined.ElapsedMilliseconds}ms");
                    }
                    else if (joined.Elapsed.TotalSeconds > 3)
                    {
                        waiting = false;
                        Log.Warn($"TV {from}: no keyframe within 3s; joining mid-stream");
                    }
                    else return;
                }
                if (pending > maxBacklogBytes) { first = false; data = []; }
                else
                {
                    pending += data.Length;
                    first = sent == 0;
                    sent += data.Length;
                    if (lastReport.Elapsed.TotalSeconds >= 5) { lastReport.Restart(); backlog = pending; }
                }
            }
            if (data.Length == 0)
            {
                Close($"fell {maxBacklogBytes / 1_000_000} MB behind; dropped");
                return;
            }
            if (first) Log.Info($"First bytes sent to TV ({from})");
            // Bytes handed to the network but not yet taken by the TV: at a constant
            // mux rate this is pure added delay (1.25 MB = 1s at 10 Mbit/s).
            if (backlog is { } b) Log.Info($"TV {from} backlog {b / 1000} kB");
            outbox.Writer.TryWrite(data);
        });

        // Writer: sends queued chunks in order.
        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var d in outbox.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    await s.SendAsync(d, SocketFlags.None).ConfigureAwait(false);
                    if (!ReferenceEquals(d, head)) lock (gate) pending -= d.Length;
                }
                // Outbox completed: everything queued has gone, so end the body cleanly.
                s.Shutdown(SocketShutdown.Send);
                Forget(s);
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                Close(e is SocketException se ? se.SocketErrorCode.ToString() : "closed");
            }
        });

        // The TV never sends a body; a completed read means it hung up.
        _ = Task.Run(async () =>
        {
            var b = new byte[1024];
            try
            {
                while (true)
                {
                    var n = await s.ReceiveAsync(b, SocketFlags.None).ConfigureAwait(false);
                    if (n == 0) break;
                }
                Close("TV closed the connection");
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                Close(e is SocketException se ? se.SocketErrorCode.ToString() : "closed");
            }
        });
    }
}
