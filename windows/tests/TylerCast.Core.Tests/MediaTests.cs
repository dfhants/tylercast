using TylerCast.Core;

namespace TylerCast.Core.Tests;

/// <summary>Ported from the Swift VideoTests and AudioTests (the parts with no Apple encoder).</summary>
public class MediaTests
{
    [Fact]
    public void MacBookScreenFitsInsideLevel41() =>
        Assert.Equal(new PixelRect(130, 0, 1660, 1080), Geometry.Fit(2940, 1912, 1920, 1080, 0));

    [Fact]
    public void MarginShrinksPictureEvenly()
    {
        var r = Geometry.Fit(2940, 1912, 1920, 1080, 4);
        Assert.True(r.Height <= 1080 * 0.92 && r.Height > 1080 * 0.91);
        Assert.True(r.Width % 2 == 0 && r.Height % 2 == 0 && r.X % 2 == 0 && r.Y % 2 == 0);
        Assert.True(Math.Abs(r.MidX - 960) <= 1 && Math.Abs(r.MidY - 540) <= 1);
    }

    [Fact]
    public void SixteenNineFillsFrame() => Assert.Equal(new PixelRect(0, 0, 1920, 1080), Geometry.Fit(3840, 2160, 1920, 1080, 0));

    /// <summary>A common Windows laptop: 2560x1600 (16:10) gets thin side bars.</summary>
    [Fact]
    public void SixteenTenGetsSideBars() => Assert.Equal(new PixelRect(96, 0, 1728, 1080), Geometry.Fit(2560, 1600, 1920, 1080, 0));

    [Fact]
    public void AvccToAnnexB()
    {
        byte[] avcc = [0, 0, 0, 2, 0x09, 0xF0, 0, 0, 0, 3, 0x65, 1, 2, 0, 0, 0, 1, 0x06];
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x65, 1, 2, 0, 0, 0, 1, 0x06 }, H264.AnnexBFromAvcc(avcc));
    }

    [Fact]
    public void AdtsHeader()
    {
        var h = Adts.Header(300);
        Assert.Equal(7, h.Length);
        Assert.True(h[0] == 0xFF && (h[1] & 0xF0) == 0xF0); // sync
        Assert.Equal(1, h[2] >> 6);                          // AAC-LC
        Assert.Equal(3, (h[2] >> 2) & 0x0F);                 // 48 kHz
        Assert.Equal(2, ((h[2] & 1) << 2) | (h[3] >> 6));   // stereo
        Assert.Equal(307, ((h[3] & 3) << 11) | (h[4] << 3) | (h[5] >> 5));
    }

    [Fact]
    public void PmtAnnouncesAacAsAdts()
    {
        var pmt = new TsMuxer(new TsMuxer.Config { AudioCodec = AudioCodec.Aac }).Pmt();
        // ... 0x0F, PID 0x101, no descriptors (as ffmpeg's mpegts muxer).
        Assert.True(pmt.AsSpan().IndexOf(new byte[] { 0x0F, 0xE1, 0x01, 0xF0, 0x00 }) >= 0);
        Assert.True(new TsMuxer().Pmt().AsSpan().IndexOf(new byte[] { 0x0F, 0xE1, 0x01 }) < 0);
        Assert.Equal("AVC_TS_MP_HD_AAC_MULT5_ISO", Dlna.ProfileFor(AudioCodec.Aac));
    }

    [Fact]
    public void TestPatternHasBarsAndAMovingBox()
    {
        var tp = new TestPattern(1920, 1080);
        var f0 = tp.Frame(0);
        Assert.Equal(1920 * 1080 * 3 / 2, f0.Length);
        // top row: the eight bars' luma, left to right
        for (var b = 0; b < 8; b++) Assert.Equal(TestPattern.Bars[b].Y, f0[b * 240 + 120]);
        // chroma of the first bars (row 0 of the UV plane)
        Assert.Equal(TestPattern.Bars[1].Cb, f0[1920 * 1080 + 240 + 120]);
        Assert.Equal(TestPattern.Bars[1].Cr, f0[1920 * 1080 + 240 + 121]);
        // the box: white at the left at frame 0, moved right by frame 50
        var row = 1080 * 3 / 4;
        Assert.Equal(235, f0[row * 1920 + 10]);
        var f50 = tp.Frame(50);
        Assert.Equal(16, f50[row * 1920 + 10]);
        Assert.Equal(235, f50[row * 1920 + 960]);
    }
}

public class PacerTests
{
    [Fact]
    public void SilentSourceFillsTimeWithoutGapsOrRepeats()
    {
        var pacer = new Pacer(new TsMuxer(), new Broadcaster(), delay: 0.15);
        var src = new SilentAudioSource();
        var t0 = 100.0;
        src.Tick(t0, pacer);
        src.Tick(t0 + 0.5, pacer);
        src.Tick(t0 + 1.0, pacer);
        // 1s at 32ms per frame: 32 frames (0, 0.032, … 0.992), each one AC-3 PES of 776 bytes = 5 packets
        var ps = TsMuxerTests.Packets(pacer.Muxer.Produce(400)).Where(p => TsMuxerTests.Pid(p) == 0x101).ToList();
        var starts = ps.Where(p => (p[1] & 0x40) != 0).Select(p => TsMuxer.DecodeTimestamp(p.AsSpan(TsMuxerTests.PayloadStart(p) + 9, 5))).ToList();
        Assert.Equal(32, starts.Count);
        foreach (var (a, b) in starts.Zip(starts.Skip(1))) Assert.InRange((long)(b - a), 2879, 2881); // 32ms in 90 kHz
    }

    [Fact]
    public void BroadcasterEndsLateSubscribersAtOnce()
    {
        var b = new Broadcaster();
        var got = new List<byte[]?>();
        b.Subscribe((d, _) => got.Add(d));
        b.Send([1, 2, 3]);
        b.Finish();
        var late = false;
        b.Subscribe((d, _) => late = d is null);
        Assert.Equal(2, got.Count);
        Assert.Null(got[1]);
        Assert.True(late);
        Assert.Equal(0, b.Count);
    }

    [Fact]
    public void PacerSendsAtTheMuxRate()
    {
        var b = new Broadcaster();
        long bytes = 0;
        b.Subscribe((d, _) => { if (d is not null) Interlocked.Add(ref bytes, d.Length); });
        var pacer = new Pacer(new TsMuxer(new TsMuxer.Config { MuxRate = 4_000_000 }), b, delay: 0.15);
        var start = Pacer.HostSeconds();
        pacer.Start();
        Thread.Sleep(1000);
        pacer.Stop();
        var seconds = Pacer.HostSeconds() - start;
        // 4 Mbit/s = 500 kB/s; allow for scheduling at both ends
        var rate = Interlocked.Read(ref bytes) * 8 / seconds;
        Assert.InRange(rate, 3_700_000, 4_100_000);
    }

    [Fact]
    public void StatsLineReadsLikeTheMacApp()
    {
        var s = TsMuxer.Stats.Empty;
        s.VideoFrames = 248; s.Keyframes = 5; s.AudioFrames = 313; s.MinSlackMs = 209.4; s.MinKeyframeSlackMs = 209.4;
        s.MaxQueuedPackets = 1153; s.NullPackets = 79; s.Packets = 100;
        Assert.Equal("TS 10s: 248 video frames (5 key), 313 audio, late 0, min slack 209ms (keyframes 209ms), "
            + "max queue 1153 pkts, padding 79%", Pacer.Describe(s));
    }
}
