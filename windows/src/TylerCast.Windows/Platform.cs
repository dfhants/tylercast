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

    /// <summary>The audio kind to use when none was asked for: AC-3 (what the TV lists) if Windows can encode it, else AAC.</summary>
    public static MirrorOptions.AudioKind DefaultAudio() =>
        MfAudioEncoder.Available(AudioCodec.Ac3).Count > 0 ? MirrorOptions.AudioKind.Ac3 : MirrorOptions.AudioKind.Aac;
}
