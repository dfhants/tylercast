using System.Diagnostics;
using System.Runtime.InteropServices;
using TylerCast.Core;

namespace TylerCast.Windows;

/// <summary>What happened when one encoder was tried (for selftest and the log).</summary>
public sealed record EncoderTrial(EncoderInfo Info, bool Ok, string Detail, double MsPerFrame, int Frames, int Keyframes,
    byte[] TransportStream, IReadOnlyList<string> Refused);

public static class EncoderPicker
{
    /// <summary>The encoders Windows offers for NV12 → H.264, best first.</summary>
    public static List<EncoderInfo> List()
    {
        try { return MfH264Encoder.Enumerate().Select(e => { Marshal.ReleaseComObject(e.Activate); return e.Info; }).ToList(); }
        catch (Exception e)
        {
            Log.Error($"Listing H.264 encoders failed: {Captures.Describe(e)}");
            return [];
        }
    }

    /// <summary>
    /// Encodes <paramref name="frames"/> test-pattern frames with one encoder and checks the result: it must
    /// produce access units, the first one a keyframe with SPS/PPS (after fixing), within a few seconds.
    /// The output is muxed into a TS file for checking on the Mac.
    /// </summary>
    public static EncoderTrial Try(EncoderInfo info, VideoSettings v, int frames = 50)
    {
        var all = MfH264Encoder.Enumerate();
        try
        {
            var activate = all.First(e => e.Info.Index == info.Index && e.Info.Name == info.Name).Activate;
            var tp = new TestPattern(v.Width, v.Height);
            var fixer = new AccessUnitFixer();
            var mux = new OfflineMux(new TsMuxer.Config { IncludeAudio = false, MuxRate = 20_000_000 });
            var outputs = 0;
            var keys = 0;
            var firstIsKey = (bool?)null;
            string? failure = null;
            var done = new ManualResetEventSlim();
            using var enc = new MfH264Encoder(info, activate, v);
            if (enc.SequenceHeader is { } sh) fixer.Learn(sh);
            enc.Output += (au, time, _) =>
            {
                var (fixedAu, key) = fixer.Fix(au);
                firstIsKey ??= key;
                outputs++;
                if (key) keys++;
                mux.Video(fixedAu, time / 1e7, key);
                if (outputs >= frames - 5) done.Set();
            };
            enc.Failed += why => { failure = why; done.Set(); };
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < frames; i++) enc.Encode(tp.Frame(i), i, forceKeyframe: false);
            var fed = sw.Elapsed.TotalMilliseconds;
            done.Wait(TimeSpan.FromSeconds(5));
            var ms = fed / frames;
            var ok = failure is null && outputs > 0 && firstIsKey == true;
            var detail = failure ?? (outputs == 0 ? "no output in 5s"
                : firstIsKey != true ? "first output was not a keyframe"
                : $"{outputs} frames out of {frames}, {keys} keyframe(s)");
            return new EncoderTrial(info, ok, detail, ms, outputs, keys, mux.ToArray(), enc.Refused);
        }
        catch (Exception e)
        {
            return new EncoderTrial(info, false, Captures.Describe(e), 0, 0, 0, [], []);
        }
        finally
        {
            foreach (var (_, a) in all) Marshal.ReleaseComObject(a);
        }
    }

    /// <summary>The first encoder that passes <see cref="Try"/> (or the one named by <paramref name="prefer"/>, if it passes).</summary>
    public static EncoderInfo Pick(VideoSettings v, string? prefer = null)
    {
        var list = List();
        if (list.Count == 0) throw new PipelineException("Windows offers no H.264 encoder (Windows N edition without the Media Feature Pack?)");
        var order = prefer is null ? list
            : list.Where(e => e.Name.Contains(prefer, StringComparison.OrdinalIgnoreCase)).Concat(list).Distinct().ToList();
        foreach (var e in order)
        {
            var t = Try(e, v, frames: 15);
            Log.Info($"Encoder trial: {e.Name}: {(t.Ok ? "OK" : "failed")}: {t.Detail}");
            if (t.Ok) return e;
        }
        throw new PipelineException("no H.264 encoder on this PC works with the TV's settings (see the log)");
    }
}

/// <summary>
/// The screen as H.264: capture (Desktop Duplication or GDI) → NV12 (scaled,
/// side-barred, margin) → Media Foundation encoder → access units, at a steady
/// 25fps from the frame clock. A still screen repeats its last picture.
/// </summary>
public sealed class WindowsVideoSource(MirrorOptions options, string? captureMethod = null, string? encoder = null) : IVideoSource
{
    public string Name { get; private set; } = "screen";
    public double MarginPercent { get; set; } = options.MarginPercent;

    private IScreenCapture? _capture;
    private MfH264Encoder? _encoder;
    private FrameClock? _clock;
    private readonly AccessUnitFixer _fixer = new();
    private ScreenImage? _image;
    private byte[]? _nv12;
    private FrameConverter? _converter;
    private bool _dirty = true;
    private double _lastMargin = double.NaN;
    private long _framesEncoded;
    private double _convertMs;

    public void Start(Action<byte[], double, bool> onAccessUnit, Action<string>? onError = null)
    {
        var v = options.Video;
        _capture = Captures.Open(captureMethod);
        _image = new ScreenImage(_capture.Width, _capture.Height);
        _converter = new FrameConverter(v.Width, v.Height);
        _nv12 = new byte[_converter.FrameBytes];

        var info = EncoderPicker.Pick(v, encoder);
        var all = MfH264Encoder.Enumerate();
        try { _encoder = new MfH264Encoder(info, all.First(e => e.Info.Index == info.Index).Activate, v); }
        finally { foreach (var (_, a) in all) Marshal.ReleaseComObject(a); }
        if (_encoder.SequenceHeader is { } sh) _fixer.Learn(sh);
        Name = $"screen ({_capture.Name}) → {info.Name}";

        var fps = v.Fps;
        _encoder.Output += (au, time, _) =>
        {
            var (fixedAu, key) = _fixer.Fix(au);
            // Sample time is index × frame duration, and frame i stands for start + i/fps.
            onAccessUnit(fixedAu, _clock!.StartHost + time / 1e7, key);
        };
        _encoder.Failed += why => onError?.Invoke(why);

        _clock = new FrameClock(fps, (index, host, force) =>
        {
            try
            {
                var sw = Stopwatch.StartNew();
                if (_capture.Capture(_image, 0)) _dirty = true;
                if (_dirty || MarginPercent != _lastMargin)
                {
                    _converter.Convert(_image.Pixels, _image.Width, _image.Height, _image.Stride, _nv12, MarginPercent);
                    _lastMargin = MarginPercent;
                    _dirty = false;
                }
                _convertMs = _convertMs * 0.95 + sw.Elapsed.TotalMilliseconds * 0.05;
                _encoder.Encode(_nv12, index, force);
                if (++_framesEncoded % (fps * 10) == 0)
                    Log.Info($"Video 10s: capture+convert {_convertMs:0.0}ms/frame average ({1000.0 / fps:0}ms budget)");
            }
            catch (Exception e)
            {
                Log.Error($"Screen capture failed: {Captures.Describe(e)}");
                _clock?.Stop();
                onError?.Invoke($"screen capture failed: {e.Message}");
            }
        });
        _clock.Start();
    }

    public void RequestKeyframe() => _clock?.RequestKeyframe();

    public void Stop()
    {
        _clock?.Stop();
        Thread.Sleep(60); // let a frame in progress finish
        _encoder?.Dispose();
        _encoder = null;
        _capture?.Dispose();
        _capture = null;
    }

    public void Dispose() => Stop();
}
