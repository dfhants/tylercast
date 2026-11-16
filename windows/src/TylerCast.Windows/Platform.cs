using TylerCast.Core;
using Windows.Win32;
using Windows.Win32.System.Power;
using Windows.Win32.UI.HiDpi;

namespace TylerCast.Windows;

/// <summary>Process-wide Windows set-up, done once at start.</summary>
public static class Platform
{
    public static readonly Version Minimum = new(10, 0, 19041);

    /// <summary>
    /// Call first. Checks the Windows version, makes the process per-monitor DPI aware
    /// (so a laptop at 125-150% scaling is captured at its real resolution), and asks
    /// for 1ms timer resolution (the pacer and frame clock otherwise sleep in 15.6ms steps).
    /// </summary>
    public static void Init()
    {
        var v = Environment.OSVersion.Version;
        if (v < Minimum) throw new PipelineException($"Windows {v} is too old: TylerCast needs Windows 10 2004 or Windows 11");
        PInvoke.SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        _ = PInvoke.timeBeginPeriod(1);
    }

    /// <summary>Keeps the display (and so capture) awake while mirroring; false lets it sleep again.</summary>
    public static void KeepAwake(bool on)
    {
        var flags = on
            ? EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_DISPLAY_REQUIRED | EXECUTION_STATE.ES_SYSTEM_REQUIRED
            : EXECUTION_STATE.ES_CONTINUOUS;
        if (PInvoke.SetThreadExecutionState(flags) == 0) Log.Warn("Could not change the display sleep setting");
        else Log.Info(on ? "Keeping the display awake while mirroring" : "Display may sleep again");
    }

    /// <summary>
    /// The audio kind to use when none was asked for: AAC, which the test TV plays (Mac app,
    /// and round 1 on Windows). AC-3 (what the TV lists) is opt-in with --audio ac3 until
    /// Windows' Dolby encoder is proven: it crashed the process in round 1.
    /// </summary>
    public static MirrorOptions.AudioKind DefaultAudio() => MirrorOptions.AudioKind.Aac;

    /// <summary>
    /// What can actually be sent: the computer's sound needs an output device to listen to
    /// (none when the audio driver is missing or the only device is unplugged) and an encoder
    /// for the codec. Otherwise the TV gets the silent AC-3 track, so the picture still works.
    /// </summary>
    public static MirrorOptions.AudioKind Resolve(MirrorOptions.AudioKind wanted)
    {
        if (wanted is not (MirrorOptions.AudioKind.Ac3 or MirrorOptions.AudioKind.Aac)) return wanted;
        try { using var l = new Loopback(); }
        catch (Exception e)
        {
            Log.Warn($"No sound to capture ({Captures.Describe(e)}): sending a silent track instead");
            return MirrorOptions.AudioKind.SilentAc3;
        }
        var codec = wanted == MirrorOptions.AudioKind.Aac ? AudioCodec.Aac : AudioCodec.Ac3;
        if (MfAudioEncoder.Available(codec).Count > 0) return wanted;
        var other = codec == AudioCodec.Aac ? MirrorOptions.AudioKind.Ac3 : MirrorOptions.AudioKind.Aac;
        if (MfAudioEncoder.Available(codec == AudioCodec.Aac ? AudioCodec.Ac3 : AudioCodec.Aac).Count > 0)
        {
            Log.Warn($"No {codec} encoder on this PC: using {MirrorOptions.Name(other)} instead");
            return other;
        }
        Log.Warn("No audio encoder on this PC (Windows N edition?): sending a silent track instead");
        return MirrorOptions.AudioKind.SilentAc3;
    }
}
