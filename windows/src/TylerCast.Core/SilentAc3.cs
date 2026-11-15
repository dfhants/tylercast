namespace TylerCast.Core;

/// <summary>
/// One pre-encoded AC-3 frame of digital silence (48 kHz stereo, 192 kbit/s,
/// 1536 samples = 32 ms, 768 bytes; made once with
/// <c>ffmpeg -f lavfi -i anullsrc=r=48000:cl=stereo -c:a ac3 -b:a 192k</c>), repeated.
/// Each AC-3 frame is self-contained (sync word + CRCs), so repeating one is a
/// valid stream matching AVC_TS_HD_50_AC3_ISO. The same bytes as the Swift app's SilentAC3.
/// Used for the test pattern, and whenever there is no sound to send.
/// </summary>
public static class SilentAc3
{
    public const int SamplesPerFrame = 1536;
    public const int SampleRate = 48_000;
    public const double FrameDuration = (double)SamplesPerFrame / SampleRate;
    public static readonly byte[] Frame = Convert.FromBase64String(
        "C3dQUhRAQ+EG9GNwgICCEBAQQVx8+fPnz58+fPnz58+fPnz58+fPnz58+fPv+dXz58+fPnz58+fPnz58+fPnz58+fPnz58+fPnz58+fPnz58+fPj/nV8+fPnz58+fPnz58+fPnz58+fPnz58+fPnz58+fPnz58+fPnz4y/xRIkSAAAAAAHjbbbbx48eO7u7gAAAAAAAAAAAAAAAAAAAAAB3d3d48eNttvnzWta1rWta1rWta1oAAAAB422228ePHju7u4AAAAAAAAAAAAAAAAAAAAAAd3d3ePHjbbb581rWta1rWta1rWtaYAAAAAAAHjbbbbx48eO7u7gAAAAAAAAAAAAAAAAAAAAAB3d3d48eNttvnzWta1rWta1rWta1oAAAAB422228ePHju7u4AAAAAAAAAAAAAAAAAAAAAAd3d3ePHjbbb581rWta1rWta1rWtaYAAAAAAAHjbbbbx48eO7u7gAAAAAAAAAAAAAAAAAAAAAB3d3d48eNttvnzWta1rWta1rWta1oAAAAB422228ePHju7u4AAAAAAAAAAAAAAAAAAAAAAd3d3ePHjbbb581rWta1rWta1rWtaYAAAAAAAHjbbbbx48eO7u7gAAAAAAAAAAAAAAAAAAAAAB3d3d48eNttvnzWta1rWta1rWta1oAAAAB422228ePHju7u4AAAAAAAAAAAAAAAAAAAAAAd3d3ePHjbbb581rWta1rWta1rWtaYAAAAAAAHjbbbbx48eO7u7gAAAAAAAAAAAAAAAAAAAAAB3d3d48eNttvnzWta1rWta1rWta1oAAAAB422228ePHju7u4AAAAAAAAAAAAAAAAAAAAAAd3d3ePHjbbb581rWta1rWta1rWtaYAAAAAAAHjbbbbx48eO7u7gAAAAAAAAAAAAAAAAAAAAAB3d3d48eNttvnzWta1rWta1rWta1oAAAAB422228ePHju7u4AAAAAAAAAAAAAAAAAAAAAAd3d3ePHjbbb581rWta1rWta1rWtaPR7");
}

/// <summary>Feeds silent AC-3 frames into the pacer's muxer in real time.</summary>
public sealed class SilentAudioSource
{
    private double? _next;

    /// <summary>Call from <see cref="Pacer.OnTick"/> (muxer lock held).</summary>
    public void Tick(double now, Pacer pacer)
    {
        var t = _next ?? now;
        while (t <= now)
        {
            pacer.Muxer.EnqueueAudio(SilentAc3.Frame, pacer.Pts90ForHost(t));
            t += SilentAc3.FrameDuration;
        }
        _next = t;
    }
}
