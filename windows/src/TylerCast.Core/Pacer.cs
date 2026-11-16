using System.Diagnostics;
using System.Globalization;

namespace TylerCast.Core;

/// <summary>
/// Fans the one TS byte stream out to every TV connection. A connection that
/// falls too far behind is dropped by its own sink rather than allowed to stall the encoder.
/// </summary>
public sealed class Broadcaster
{
    /// <summary>(chunk, offset of a keyframe start in it if any); null chunk = stream ended.</summary>
    public delegate void Sink(byte[]? chunk, int? keyOffset);

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Sink> _sinks = [];
    private bool _ended;

    public Guid Subscribe(Sink sink)
    {
        var id = Guid.NewGuid();
        bool ended;
        lock (_gate)
        {
            ended = _ended;
            if (!ended) _sinks[id] = sink;
        }
        if (ended) sink(null, null);
        return id;
    }

    public void Unsubscribe(Guid id)
    {
        lock (_gate) _sinks.Remove(id);
    }

    public int Count
    {
        get { lock (_gate) return _sinks.Count; }
    }

    public void Send(byte[] data, int? keyOffset = null)
    {
        Sink[] s;
        lock (_gate) s = [.. _sinks.Values];
        foreach (var sink in s) sink(data, keyOffset);
    }

    public void Finish()
    {
        Sink[] s;
        lock (_gate)
        {
            s = [.. _sinks.Values];
            _sinks.Clear();
            _ended = true;
        }
        foreach (var sink in s) sink(null, null);
    }
}

/// <summary>
/// Drives a <see cref="TsMuxer"/> in real time: emits exactly muxRate bits per second
/// (null-padded) to the broadcaster, so the byte position, the PCR and the
/// wall clock stay locked together. Also maps host time (what capture and the
/// frame clock use) to stream time.
///
/// The loop works out from the clock how many packets are due, so a coarse
/// sleep only makes the chunks bigger; it never makes the stream slow. (Windows
/// sleeps in ~15.6ms steps unless the app raises the timer resolution.)
/// </summary>
public sealed class Pacer(TsMuxer muxer, Broadcaster broadcaster, double delay)
{
    public TsMuxer Muxer { get; } = muxer;
    public Broadcaster Broadcaster { get; } = broadcaster;
    /// <summary>Capture-to-DTS delay: the slack that keeps frames from arriving late.</summary>
    public double Delay { get; } = delay;
    /// <summary>Called under the muxer lock before each batch, with the host time in seconds.</summary>
    public Action<double>? OnTick { get; set; }

    private readonly Lock _gate = new();
    private Thread? _thread;
    private bool _running;
    private double _startHost;
    private double _lastStats;
    public ulong BytesSent { get; private set; }
    /// <summary>The last 10-second statistics logged (null before the first).</summary>
    public TsMuxer.Stats? LastStats { get; private set; }

    /// <summary>Monotonic seconds (QueryPerformanceCounter on Windows, mach time on the Mac).</summary>
    public static double HostSeconds() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>Stream time (90 kHz) for a host time, plus the delay: the PTS to stamp
    /// on something captured at <paramref name="host"/>.</summary>
    public ulong Pts90ForHost(double host)
    {
        var t = Math.Max(0, host - _startHost + Delay);
        return TsMuxer.Base90 + (ulong)(t * 90_000);
    }

    public T WithMuxer<T>(Func<TsMuxer, T> body)
    {
        lock (_gate) return body(Muxer);
    }

    public void WithMuxer(Action<TsMuxer> body)
    {
        lock (_gate) body(Muxer);
    }

    public void Start()
    {
        _startHost = HostSeconds();
        _lastStats = _startHost;
        _running = true;
        _thread = new Thread(Loop) { Name = "ts-pacer", IsBackground = true, Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    public void Stop()
    {
        lock (_gate) _running = false;
        Broadcaster.Finish();
    }

    private void Loop()
    {
        double rate = Muxer.Settings.MuxRate;
        while (true)
        {
            byte[]? data = null;
            int? keyOffset = null;
            TsMuxer.Stats? stats = null;
            lock (_gate)
            {
                if (!_running) return;
                var now = HostSeconds();
                var target = (ulong)(Math.Max(0, now - _startHost) * rate / 1504);
                if (target > Muxer.PacketsEmitted)
                {
                    OnTick?.Invoke(now);
                    (data, keyOffset) = Muxer.ProduceChunk((int)(target - Muxer.PacketsEmitted));
                }
                if (now - _lastStats >= 10)
                {
                    stats = Muxer.TakeStats();
                    _lastStats = now;
                }
            }
            if (data is not null)
            {
                BytesSent += (ulong)data.Length;
                Broadcaster.Send(data, keyOffset);
            }
            if (stats is { } s)
            {
                LastStats = s;
                LogStats(s);
            }
            Thread.Sleep(4);
        }
    }

    internal static string Describe(TsMuxer.Stats s)
    {
        static string Ms(double v) => double.IsFinite(v) ? v.ToString("0", CultureInfo.InvariantCulture) + "ms" : "-";
        return $"TS 10s: {s.VideoFrames} video frames ({s.Keyframes} key), {s.AudioFrames} audio, "
            + $"late {s.LateFrames}, min slack {Ms(s.MinSlackMs)} (keyframes {Ms(s.MinKeyframeSlackMs)}), "
            + (s.AudioFrames > 0 ? $"audio late {s.LateAudioFrames}, min slack {Ms(s.MinAudioSlackMs)}, " : "")
            + $"max queue {s.MaxQueuedPackets} pkts, padding {(s.Packets > 0 ? 100 * s.NullPackets / s.Packets : 0)}%";
    }

    private static void LogStats(TsMuxer.Stats s)
    {
        var msg = Describe(s);
        if (s.LateFrames > 0) Log.Warn(msg + "  <- late frames: raise --delay or --muxrate, or lower --bitrate");
        else if (s.LateAudioFrames > 0) Log.Warn(msg + "  <- late audio: raise --delay");
        else Log.Info(msg);
    }
}
