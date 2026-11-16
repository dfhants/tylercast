using TylerCast.Core;

namespace TylerCast.Core.Tests;

/// <summary>The portable halves of the Windows pipeline, tested on the Mac before any Windows round.</summary>
public class FrameConverterTests
{
    private static byte[] Solid(int w, int h, byte b, byte g, byte r)
    {
        var p = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++) { p[i * 4] = b; p[i * 4 + 1] = g; p[i * 4 + 2] = r; p[i * 4 + 3] = 255; }
        return p;
    }

    // BT.709 limited range, from the standard's equations (not from the code under test).
    [Theory]
    [InlineData(255, 255, 255, 235, 128, 128)] // white
    [InlineData(0, 0, 0, 16, 128, 128)]        // black
    [InlineData(255, 0, 0, 63, 102, 240)]      // red
    [InlineData(0, 255, 0, 173, 42, 26)]       // green
    [InlineData(0, 0, 255, 32, 240, 118)]      // blue
    public void Bt709LimitedRange(int r, int g, int b, int y, int cb, int cr)
    {
        var (yy, cbb, crr) = FrameConverter.ToYCbCr(r, g, b);
        Assert.InRange(yy, y - 1, y + 1);
        Assert.InRange(cbb, cb - 1, cb + 1);
        Assert.InRange(crr, cr - 1, cr + 1);
    }

    /// <summary>A 16:10 laptop screen (2560x1600) in red: red picture centred with black side bars.</summary>
    [Fact]
    public void LaptopScreenGetsSideBars()
    {
        var c = new FrameConverter(1920, 1080);
        var nv12 = new byte[c.FrameBytes];
        var r = c.Convert(Solid(2560, 1600, 0, 0, 255), 2560, 1600, 2560 * 4, nv12, 0);
        Assert.Equal(new PixelRect(96, 0, 1728, 1080), r);
        Assert.Equal(16, nv12[540 * 1920 + 50]);            // left bar: black
        Assert.Equal(16, nv12[540 * 1920 + 1900]);          // right bar: black
        Assert.InRange(nv12[540 * 1920 + 960], 62, 64);     // middle: red's luma
        var uv = 1920 * 1080 + 270 * 1920 + 960;
        Assert.InRange(nv12[uv], 101, 103);                 // red's Cb
        Assert.InRange(nv12[uv + 1], 239, 240);             // red's Cr
        Assert.Equal(128, nv12[1920 * 1080 + 270 * 1920 + 40]); // bar chroma neutral
    }

    /// <summary>Strided input (rows padded, as a mapped GPU texture is) and a margin.</summary>
    [Fact]
    public void HonoursStrideAndMargin()
    {
        int w = 1366, h = 768, stride = 1408 * 4; // a 1366-wide screen, rows padded to 1408 pixels
        var p = new byte[stride * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++) { var o = y * stride + x * 4; p[o] = 255; p[o + 1] = 255; p[o + 2] = 255; }
        var c = new FrameConverter(1920, 1080);
        var nv12 = new byte[c.FrameBytes];
        var r = c.Convert(p, w, h, stride, nv12, 4);
        Assert.True(r.X > 0 && r.Y > 0);
        Assert.Equal(16, nv12[10 * 1920 + 960]);            // top margin: black
        Assert.Equal(235, nv12[540 * 1920 + 960]);          // picture: white
        Assert.Equal(235, nv12[(r.Y + r.Height - 1) * 1920 + r.X + r.Width - 1]); // last pixel: no padding bleed
    }

    [Fact]
    public void ScalesAHalfAndHalfImageWithoutSmearingFar()
    {
        // Left half black, right half white, 3840 wide: the output edge is at 960 ± 2.
        int w = 3840, h = 2160;
        var p = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = w / 2; x < w; x++) { var o = (y * w + x) * 4; p[o] = p[o + 1] = p[o + 2] = 255; }
        var c = new FrameConverter(1920, 1080);
        var nv12 = new byte[c.FrameBytes];
        c.Convert(p, w, h, w * 4, nv12, 0);
        var row = 500 * 1920;
        Assert.Equal(16, nv12[row + 950]);
        Assert.Equal(235, nv12[row + 970]);
    }
}

public class CursorPainterTests
{
    private static byte[] Grey(int w, int h, byte v)
    {
        var p = new byte[w * h * 4];
        Array.Fill(p, v);
        return p;
    }

    [Fact]
    public void ColourPointerBlendsByAlpha()
    {
        var img = Grey(4, 4, 100);
        byte[] shape = [0, 0, 255, 255, /* opaque red */ 0, 0, 255, 0 /* transparent */];
        CursorPainter.Draw(img, 4, 4, 16, CursorPainter.ShapeType.Color, shape, 2, 1, 8, 1, 1);
        const int p1 = (1 * 4 + 1) * 4, p2 = (1 * 4 + 2) * 4;
        Assert.Equal(new byte[] { 0, 0, 255 }, img[p1..(p1 + 3)]);
        Assert.Equal(new byte[] { 100, 100, 100 }, img[p2..(p2 + 3)]);
    }

    [Fact]
    public void MaskedColourReplacesOrXors()
    {
        var img = Grey(2, 1, 0x0F);
        byte[] shape = [0xF0, 0xF0, 0xF0, 0x00, /* mask 0: replace */ 0xFF, 0xFF, 0xFF, 0xFF /* mask set: XOR */];
        CursorPainter.Draw(img, 2, 1, 8, CursorPainter.ShapeType.MaskedColor, shape, 2, 1, 8, 0, 0);
        Assert.Equal(0xF0, img[0]);
        Assert.Equal(0xF0, img[4]); // 0x0F ^ 0xFF
    }

    /// <summary>Monochrome: AND mask then XOR mask, 1 bit per pixel, the two masks stacked.</summary>
    [Fact]
    public void MonochromeFollowsTheAndXorTable()
    {
        var img = Grey(4, 1, 0x40);
        // Pixels: (AND,XOR) = (0,0) black, (0,1) white, (1,0) unchanged, (1,1) inverted.
        byte[] shape = [0b0011_0000, 0b0101_0000];
        CursorPainter.Draw(img, 4, 1, 16, CursorPainter.ShapeType.Monochrome, shape, 4, 2, 1, 0, 0);
        Assert.Equal(0x00, img[0]);
        Assert.Equal(0xFF, img[4]);
        Assert.Equal(0x40, img[8]);
        Assert.Equal(0xBF, img[12]);
    }

    [Fact]
    public void ClipsAtTheEdges()
    {
        var img = Grey(2, 2, 7);
        var shape = Enumerable.Repeat((byte)255, 4 * 4 * 4).ToArray();
        CursorPainter.Draw(img, 2, 2, 8, CursorPainter.ShapeType.Color, shape, 4, 4, 16, 1, -1); // mostly off-image
        Assert.Equal(255, img[4]); // (1,0) covered
        Assert.Equal(7, img[0]);   // (0,0) not
    }
}

public class PcmTimelineTests
{
    /// <summary>Ported from the Swift test: blocks are timed exactly from their first sample.</summary>
    [Fact]
    public void BlocksAreTimedFromTheirFirstSample()
    {
        var blocks = new List<(float[] B, double T)>();
        var t = new PcmTimeline(1024, (b, s) => blocks.Add((b, s)));
        var pcm = Pcm.Sine(1000, 1);
        const int step = 480 * 2; // 10ms buffers, as capture delivers them
        for (int i = 0, off = 0; off < pcm.Length; i++, off += step)
            t.Push(pcm.AsSpan(off, Math.Min(step, pcm.Length - off)), 100 + i * 0.01);
        Assert.Equal(46, blocks.Count); // 48000 / 1024
        foreach (var (a, b) in blocks.Zip(blocks.Skip(1))) Assert.Equal(1024.0 / 48000, b.T - a.T, 9);
        Assert.Equal(100, blocks[0].T, 9);
        Assert.Equal(pcm[..2048], blocks[0].B);
    }

    /// <summary>Ported from the Swift test: padded silence keeps the track continuous, and real
    /// sound joins it without a gap or overlap.</summary>
    [Fact]
    public void SilencePaddingKeepsTheTrackContinuous()
    {
        var times = new List<double>();
        var t = new PcmTimeline(1536, (_, s) => times.Add(s));
        t.PadSilence(10.0); // anchors at 9.9
        for (var i = 1; i <= 50; i++) t.PadSilence(10.0 + i * 0.02); // 1s of quiet
        var host = 10.9 + 0.03; // sound arrives 30ms off where the padding got to
        for (var i = 0; i < 100; i++)
        {
            t.Push(Pcm.Sine(440, 0.01), host);
            host += 0.01;
        }
        Assert.True(times.Count > 55);
        Assert.Equal(9.9, times[0], 9);
        foreach (var (a, b) in times.Zip(times.Skip(1))) Assert.Equal(1536.0 / 48000, b - a, 9);
    }

    [Fact]
    public void ReanchorsAfterABigJump()
    {
        var times = new List<double>();
        var t = new PcmTimeline(480, (_, s) => times.Add(s));
        t.Push(Pcm.Sine(440, 0.1), 5.0);
        t.Push(Pcm.Sine(440, 0.1), 50.0); // capture restarted much later
        Assert.Equal(5.0, times[0], 9);
        Assert.Equal(50.0, times[10], 9);
    }
}

public class PcmTests
{
    [Fact]
    public void ToneLevelHearsItsOwnFrequencyOnly()
    {
        var s = Pcm.Sine(1000, 0.5, 0.5f);
        Assert.InRange(Pcm.ToneLevel(s, 2, 48_000, 1000), 0.48, 0.52);
        Assert.InRange(Pcm.ToneLevel(s, 2, 48_000, 1300), 0, 0.02);
        Assert.Equal(0, Pcm.ToneLevel(new float[9600], 2, 48_000, 1000), 6);
    }

    /// <summary>A 44.1 kHz 5.1 laptop mix to 48 kHz stereo, in uneven chunks, keeps the tone.</summary>
    [Fact]
    public void ResamplesMultichannel44k1To48kSeamlessly()
    {
        const int rate = 44_100, ch = 6;
        var n = rate; // 1s
        var src = new float[n * ch];
        for (var i = 0; i < n; i++)
        {
            var v = 0.5f * (float)Math.Sin(2 * Math.PI * 1000 * i / rate);
            src[i * ch] = v;         // front left
            src[i * ch + 1] = v;     // front right
            src[i * ch + 2] = 0.9f;  // centre: ignored
        }
        double phase = 0;
        (float, float) last = (0, 0);
        var outp = new List<float>();
        var chunks = new[] { 441, 1000, 7, 4410, 20000 };
        int off = 0, k = 0;
        while (off < n)
        {
            var len = Math.Min(chunks[k++ % chunks.Length], n - off);
            outp.AddRange(Pcm.ToStereo48k(src.AsSpan(off * ch, len * ch), ch, rate, ref phase, ref last));
            off += len;
        }
        var frames = outp.Count / 2;
        Assert.InRange(frames, 47_990, 48_010);
        var tail = outp.Skip(4800).ToArray(); // skip the first 0.1s
        Assert.InRange(Pcm.ToneLevel(tail, 2, 48_000, 1000), 0.47, 0.52);
        // No clicks at chunk boundaries: the signal never jumps more than one step of a 1 kHz sine.
        var maxStep = 2 * Math.PI * 1000 / 48_000 * 0.5 * 1.1;
        for (var i = 2; i < outp.Count; i += 2) Assert.True(Math.Abs(outp[i] - outp[i - 2]) <= maxStep, $"jump at {i / 2}");
    }

    [Fact]
    public void Stereo48kPassesThrough()
    {
        var s = Pcm.Sine(440, 0.01);
        double ph = 0;
        (float, float) last = (0, 0);
        Assert.Equal(s, Pcm.ToStereo48k(s, 2, 48_000, ref ph, ref last));
    }

    [Fact]
    public void Int16AndWav()
    {
        Assert.Equal(new byte[] { 0xFF, 0x7F, 0x01, 0x80, 0, 0 }, Pcm.ToInt16([1f, -1f, 0f]));
        var wav = Pcm.Wav(Pcm.Sine(1000, 0.1));
        Assert.Equal("RIFF"u8.ToArray(), wav[..4]);
        Assert.Equal(44 + 4800 * 4, wav.Length);
    }
}

public class AccessUnitFixerTests
{
    private static readonly byte[] Sps = [0x67, 0x4D, 0x40, 0x29, 0xAA];
    private static readonly byte[] Pps = [0x68, 0xEE, 0x3C, 0x80];
    private static byte[] Nal(params byte[] n) => [0, 0, 0, 1, .. n];

    [Fact]
    public void AddsAudAndRepeatsParameterSetsBeforeEveryIdr()
    {
        var f = new AccessUnitFixer();
        // First keyframe carries SPS/PPS (no AUD, as Microsoft's encoder writes it).
        var (k1, key1) = f.Fix([.. Nal(Sps), .. Nal(Pps), .. Nal(0x65, 1, 2)]);
        Assert.True(key1);
        Assert.Equal([.. AnnexB.Aud, .. Nal(Sps), .. Nal(Pps), .. Nal(0x65, 1, 2)], k1);
        // A P-frame: just the AUD added.
        var (p, keyP) = f.Fix(Nal(0x41, 9));
        Assert.False(keyP);
        Assert.Equal([.. AnnexB.Aud, .. Nal(0x41, 9)], p);
        // A later keyframe without SPS/PPS gets them back.
        var (k2, _) = f.Fix(Nal(0x65, 3));
        Assert.Equal([.. AnnexB.Aud, .. Nal(Sps), .. Nal(Pps), .. Nal(0x65, 3)], k2);
    }

    [Fact]
    public void KeepsTheEncodersAudFirst()
    {
        var f = new AccessUnitFixer();
        f.Learn([.. Nal(Sps), .. Nal(Pps)]);
        var (k, _) = f.Fix([.. AnnexB.Aud, .. Nal(0x65, 3)]);
        Assert.Equal([.. AnnexB.Aud, .. Nal(Sps), .. Nal(Pps), .. Nal(0x65, 3)], k);
    }

    [Fact]
    public void ThreeByteStartCodesAndTrailingZeros()
    {
        var f = new AccessUnitFixer();
        f.Fix([0, 0, 1, .. Sps, 0, 0, 0, 1, .. Pps, 0, 0, 0, 1, 0x65, 1]);
        var (k, _) = f.Fix(Nal(0x65, 2));
        Assert.Equal([.. AnnexB.Aud, .. Nal(Sps), .. Nal(Pps), .. Nal(0x65, 2)], k);
    }
}

public class AudioPipelineTests
{
    private sealed class FakeAac : IAudioSource
    {
        public string Name => "fake AAC";
        public AudioCodec Codec => AudioCodec.Aac;
        private Timer? _t;
        public void Start(Action<byte[], double> onFrame, Action<string>? onError = null)
        {
            var host = Pacer.HostSeconds();
            var k = 0;
            _t = new Timer(_ => onFrame([.. Adts.Header(10), .. new byte[10]], host + k++ * 1024.0 / 48000), null, 0, 21);
        }
        public void Stop() => _t?.Dispose();
        public void Dispose() => Stop();
    }

    [Fact]
    public async Task SoundSourceFramesReachTheStream()
    {
        var o = new MirrorOptions { Audio = MirrorOptions.AudioKind.Aac };
        var p = new Pipeline(o, new ClipVideoSource(), new FakeAac());
        var bytes = new List<byte>();
        p.Broadcaster.Subscribe((d, _) => { if (d is not null) lock (bytes) bytes.AddRange(d); });
        await p.StartAsync(serve: false);
        await Task.Delay(600);
        p.Stop();
        byte[] all;
        lock (bytes) all = [.. bytes];
        var ps = TsMuxerTests.Packets(all[..(all.Length / 188 * 188)]);
        // AAC announced (stream type 0x0F) and carried as audio PES 0xC0 on PID 0x101.
        var pmt = ps.First(x => TsMuxerTests.Pid(x) == 0x1000);
        Assert.True(pmt.AsSpan().IndexOf(new byte[] { 0x0F, 0xE1, 0x01 }) > 0);
        var audioStarts = ps.Where(x => TsMuxerTests.Pid(x) == 0x101 && (x[1] & 0x40) != 0).ToList();
        Assert.True(audioStarts.Count > 10);
        Assert.Equal(0xC0, audioStarts[0][TsMuxerTests.PayloadStart(audioStarts[0]) + 3]);
    }

    [Fact]
    public async Task RefusesAMismatchedSoundSource()
    {
        var p = new Pipeline(new MirrorOptions { Audio = MirrorOptions.AudioKind.Ac3 }, new ClipVideoSource(), new FakeAac());
        await Assert.ThrowsAsync<PipelineException>(() => p.StartAsync(serve: false));
    }

    [Fact]
    public void OfflineMuxWritesWholePackets()
    {
        var m = new OfflineMux();
        var clip = new ClipVideoSource();
        for (var i = 0; i < 10; i++) m.Video(clip.AccessUnits[i].Au, i / 25.0, clip.AccessUnits[i].Key);
        m.Audio(SilentAc3.Frame, 0);
        var d = m.ToArray();
        Assert.Equal(0, d.Length % 188);
        Assert.Equal(0, TsMuxerTests.Pid(d[..188]));
    }
}

public class Ac3Tests
{
    [Fact]
    public void SilentFrameIs768Bytes() => Assert.Equal(768, Ac3.FrameSize(SilentAc3.Frame)); // 192 kbit/s, 48 kHz

    [Fact]
    public void SplitsBackToBackFrames()
    {
        byte[] three = [.. SilentAc3.Frame, .. SilentAc3.Frame, .. SilentAc3.Frame];
        var frames = Ac3.Split(three);
        Assert.Equal(3, frames.Count);
        Assert.All(frames, f => Assert.Equal(SilentAc3.Frame, f));
    }

    [Fact]
    public void PassesThroughWhatItDoesNotRecognise()
    {
        var odd = new byte[] { 1, 2, 3, 4, 5, 6 };
        Assert.Equal([odd], Ac3.Split(odd));
    }
}
