namespace TylerCast.Core;

/// <summary>
/// Where the pipeline's H.264 comes from: a live encoder (Windows: capture +
/// Media Foundation) or the pre-encoded test-pattern clip.
/// </summary>
public interface IVideoSource : IDisposable
{
    /// <summary>A short name for the log ("test pattern clip", "Media Foundation: NVIDIA …").</summary>
    string Name { get; }

    /// <summary>
    /// Starts producing access units: Annex B with AUD (and SPS/PPS before each
    /// keyframe), the host time the frame stands for, and whether it is a keyframe.
    /// Called on the source's own thread. <paramref name="onError"/>: the source has failed
    /// for good (capture lost, encoder died); the session stops and says why.
    /// </summary>
    void Start(Action<byte[], double, bool> onAccessUnit, Action<string>? onError = null);

    /// <summary>The next frame should be a keyframe (a TV has just connected).</summary>
    void RequestKeyframe();

    void Stop();
}

/// <summary>
/// Calls <c>onFrame(index, host, forceKeyframe)</c> at exactly <c>fps</c>, on its own
/// thread, whatever the screen is doing: the TV's profile expects a steady rate,
/// so a still screen repeats its last picture. Frame <c>i</c> stands for host time
/// start + i/fps. Late ticks skip ahead rather than bunch up.
/// </summary>
public sealed class FrameClock(int fps, Action<int, double, bool> onFrame)
{
    private Thread? _thread;
    private volatile bool _running;
    private int _keyframeRequested;
    public double StartHost { get; private set; }

    public void RequestKeyframe() => Interlocked.Exchange(ref _keyframeRequested, 1);

    public void Start()
    {
        StartHost = Pacer.HostSeconds();
        _running = true;
        _thread = new Thread(Loop) { Name = "frame-clock", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop() => _running = false;

    private void Loop()
    {
        var last = -1;
        var period = 1.0 / fps;
        while (_running)
        {
            var now = Pacer.HostSeconds();
            var index = (int)((now - StartHost) * fps);
            if (index > last)
            {
                last = index;
                var force = Interlocked.Exchange(ref _keyframeRequested, 0) == 1;
                try { onFrame(index, StartHost + index * period, force); }
                catch (Exception e) { Log.Error($"Frame {index}: {e.Message}"); }
            }
            // Wake about four times a frame (10ms at 25fps); the clock, not the sleep, decides.
            Thread.Sleep(Math.Max(1, (int)(period * 1000 / 4)));
        }
    }
}

/// <summary>
/// Plays the embedded, pre-encoded test-pattern clip (100 frames, IDR at 0 and 50)
/// in a loop at 25fps. Needs no encoder, so it tests the whole TV path (muxer,
/// pacer, HTTP, SOAP) on any machine. A keyframe request jumps to the next IDR.
/// </summary>
public sealed class ClipVideoSource : IVideoSource
{
    public const int Fps = 25;
    public string Name => "test pattern clip (pre-encoded H.264, looped)";
    public IReadOnlyList<(byte[] Au, bool Key)> AccessUnits { get; }

    private FrameClock? _clock;
    private int _position;

    public ClipVideoSource() : this(LoadEmbedded()) { }

    public ClipVideoSource(byte[] annexB)
    {
        AccessUnits = SplitAccessUnits(annexB);
        if (AccessUnits.Count == 0 || !AccessUnits[0].Key) throw new InvalidDataException("the clip must start with a keyframe");
    }

    public static byte[] LoadEmbedded()
    {
        using var s = typeof(ClipVideoSource).Assembly.GetManifestResourceStream("TestPattern.h264")
            ?? throw new InvalidOperationException("TestPattern.h264 is not embedded");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Annex B stream to access units, split at each AUD (NAL type 9); a unit
    /// with an IDR slice (type 5) is a keyframe.</summary>
    public static List<(byte[] Au, bool Key)> SplitAccessUnits(ReadOnlySpan<byte> d)
    {
        // NAL start positions (index of the first byte of the start code) and types.
        var nals = new List<(int Start, int Type)>();
        for (var i = 0; i + 3 < d.Length; i++)
        {
            if (d[i] != 0 || d[i + 1] != 0) continue;
            int sc = d[i + 2] == 1 ? 3 : d[i + 2] == 0 && d[i + 3] == 1 ? 4 : 0;
            if (sc == 0 || i + sc >= d.Length) continue;
            nals.Add((i, d[i + sc] & 0x1F));
            i += sc;
        }
        var aus = new List<(byte[], bool)>();
        int auStart = -1;
        var key = false;
        foreach (var (start, type) in nals)
        {
            if (type == 9)
            {
                if (auStart >= 0) aus.Add((d[auStart..start].ToArray(), key));
                auStart = start;
                key = false;
            }
            if (type == 5) key = true;
        }
        if (auStart >= 0) aus.Add((d[auStart..].ToArray(), key));
        return aus;
    }

    public void Start(Action<byte[], double, bool> onAccessUnit, Action<string>? onError = null)
    {
        _clock = new FrameClock(Fps, (_, host, force) =>
        {
            var p = _position;
            if (force && !AccessUnits[p].Key)
            {
                // Jump ahead to the next IDR: the box skips, the TV can start at once.
                while (!AccessUnits[p].Key) p = (p + 1) % AccessUnits.Count;
            }
            var (au, key) = AccessUnits[p];
            _position = (p + 1) % AccessUnits.Count;
            onAccessUnit(au, host, key);
        });
        _clock.Start();
    }

    public void RequestKeyframe() => _clock?.RequestKeyframe();

    public void Stop() => _clock?.Stop();

    public void Dispose() => Stop();
}
