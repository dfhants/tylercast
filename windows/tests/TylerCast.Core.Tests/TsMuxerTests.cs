using TylerCast.Core;

namespace TylerCast.Core.Tests;

/// <summary>Ported from the Swift TSMuxerTests; the same expectations, byte for byte.</summary>
public class TsMuxerTests
{
    internal static byte[] Hex(string s) => Convert.FromHexString(s);
    internal static int Pid(byte[] p) => ((p[1] & 0x1F) << 8) | p[2];

    /// <summary>PCR in 27 MHz from a packet's adaptation field, if it has one.</summary>
    internal static ulong? Pcr(byte[] p)
    {
        if ((p[3] & 0x20) == 0 || p[4] < 7 || (p[5] & 0x10) == 0) return null;
        var b = p[6..12].Select(x => (ulong)x).ToArray();
        var @base = (b[0] << 25) | (b[1] << 17) | (b[2] << 9) | (b[3] << 1) | (b[4] >> 7);
        return @base * 300 + (((b[4] & 1) << 8) | b[5]);
    }

    /// <summary>Payload offset (after any adaptation field).</summary>
    internal static int PayloadStart(byte[] p) => (p[3] & 0x20) != 0 ? 5 + p[4] : 4;

    internal static List<byte[]> Packets(byte[] d) =>
        Enumerable.Range(0, d.Length / 188).Select(i => d[(i * 188)..((i + 1) * 188)]).ToList();

    [Fact]
    public void CrcKnownValue() => Assert.Equal(0x0376_E6E7u, Crc.Mpeg32("123456789"u8.ToArray()));

    /// <summary>Byte-for-byte what ffmpeg's mpegts muxer wrote in Phase 1 (which the TV played).</summary>
    [Fact]
    public void PatAndPmtMatchFfmpeg()
    {
        var m = new TsMuxer();
        Assert.Equal(Hex("00b00d0001c100000001f0002ab104b2"), m.Pat());
        Assert.Equal(Hex("02b01d0001c10000e100f0001be100f00081e101f006050441432d33ce50fcbc"), m.Pmt());
    }

    [Fact]
    public void PmtWithoutAudioHasOnlyVideo()
    {
        var pmt = new TsMuxer(new TsMuxer.Config { IncludeAudio = false }).Pmt();
        Assert.Equal(3 + 9 + 5 + 4, pmt.Length); // header, PCR PID + info len + one stream, CRC
        Assert.Equal(0u, Crc.Mpeg32(pmt));       // a section with its CRC appended checks to 0
    }

    [Fact]
    public void SectionsCheckToZero()
    {
        var m = new TsMuxer();
        foreach (var s in new[] { m.Pat(), m.Pmt(), m.Sdt() }) Assert.Equal(0u, Crc.Mpeg32(s));
    }

    [Fact]
    public void EveryPacketIsWellFormed()
    {
        var m = new TsMuxer();
        m.EnqueueVideo(Enumerable.Repeat((byte)0xAB, 50_000).ToArray(), 200_000, keyframe: true);
        m.EnqueueAudio(SilentAc3.Frame, 200_000);
        var ps = Packets(m.Produce(2000));
        Assert.All(ps, p => Assert.True(p.Length == 188 && p[0] == 0x47));
        // adaptation field lengths stay inside the packet
        Assert.All(ps, p => Assert.True((p[3] & 0x20) == 0 || p[4] <= 183));
    }

    [Fact]
    public void TablesComeFirstAndRepeat()
    {
        var m = new TsMuxer(); // 10 Mbit/s: 100ms = 665 packets
        var ps = Packets(m.Produce(3000));
        Assert.True(Pid(ps[0]) == 0 && Pid(ps[1]) == 0x1000 && Pid(ps[2]) == 0x11);
        var patAt = Enumerable.Range(0, ps.Count).Where(i => Pid(ps[i]) == 0).ToList();
        Assert.True(patAt.Count >= 4);
        foreach (var (a, b) in patAt.Zip(patAt.Skip(1))) Assert.True(b - a <= 700);
    }

    [Fact]
    public void PcrFollowsByteRateAndRepeats()
    {
        var m = new TsMuxer();
        var ps = Packets(m.Produce(20_000));
        var pcrs = Enumerable.Range(0, ps.Count).Select(i => (i, v: Pcr(ps[i]))).Where(x => x.v is not null)
            .Select(x => (x.i, v: x.v!.Value)).ToList();
        Assert.True(pcrs.Count > 50);
        foreach (var (i, v) in pcrs)
        {
            Assert.Equal(0x100, Pid(ps[i])); // on the video PID, as the PMT says
            Assert.Equal(m.Pcr27AtPacket((ulong)i), v);
        }
        foreach (var (a, b) in pcrs.Zip(pcrs.Skip(1)))
        {
            var gap = (double)(b.v - a.v) / 27_000_000;
            Assert.True(gap > 0 && gap <= 0.04); // spec max 0.1s
        }
        // exactly muxRate: 1504 bits per packet at 10 Mbit/s
        var (i0, p0) = pcrs[0];
        var (i1, p1) = pcrs[^1];
        var rate = (double)(i1 - i0) * 1504 / ((double)(p1 - p0) / 27_000_000);
        Assert.True(Math.Abs(rate - 10_000_000) < 1000);
    }

    [Fact]
    public void NullPaddingWhenIdle()
    {
        var m = new TsMuxer();
        var nulls = Packets(m.Produce(1000)).Where(p => Pid(p) == 0x1FFF).ToList();
        Assert.True(nulls.Count > 900);
        Assert.All(nulls, p => Assert.Equal(TsMuxer.NullPacket, p));
    }

    [Fact]
    public void ContinuityCountersIncrementPerPid()
    {
        var m = new TsMuxer();
        for (var k = 0; k < 10; k++)
        {
            m.EnqueueVideo(Enumerable.Repeat((byte)k, 3000).ToArray(), 100_000 + (ulong)k * 3600, keyframe: k == 0);
            m.EnqueueAudio(SilentAc3.Frame, 100_000 + (ulong)k * 2880);
        }
        var last = new Dictionary<int, int>();
        foreach (var p in Packets(m.Produce(5000)).Where(p => Pid(p) != 0x1FFF))
        {
            int c = p[3] & 0x0F;
            var hasPayload = (p[3] & 0x10) != 0;
            if (last.TryGetValue(Pid(p), out var prev)) Assert.Equal(hasPayload ? (prev + 1) & 0x0F : prev, c);
            last[Pid(p)] = c;
        }
    }

    [Fact]
    public void VideoPesCarriesPtsAndKeyframeFlags()
    {
        var m = new TsMuxer();
        byte[] au = [0, 0, 0, 1, 0x09, 0xF0, 0, 0, 0, 1, 0x65, .. Enumerable.Repeat((byte)7, 1000)];
        m.EnqueueVideo(au, 123_456, keyframe: true);
        var ps = Packets(m.Produce(700));
        var starts = Enumerable.Range(0, ps.Count).Where(i => Pid(ps[i]) == 0x100 && (ps[i][1] & 0x40) != 0).ToList();
        Assert.Single(starts);
        var i = starts[0];
        // The keyframe is preceded by PAT, PMT, SDT and a PCR, so a TV joining here can decode at once.
        Assert.True(Pid(ps[i - 4]) == 0 && Pid(ps[i - 3]) == 0x1000 && Pid(ps[i - 2]) == 0x11 && Pcr(ps[i - 1]) is not null);
        var p = ps[i];
        Assert.True((p[3] & 0x20) != 0 && (p[5] & 0x40) != 0); // random_access_indicator
        var pes = p[PayloadStart(p)..];
        Assert.Equal(new byte[] { 0, 0, 1, 0xE0 }, pes[..4]);
        Assert.True(pes[4] == 0 && pes[5] == 0);   // unbounded video PES, as ffmpeg
        Assert.True(pes[7] == 0x80 && pes[8] == 5); // PTS only
        Assert.Equal(123_456ul, TsMuxer.DecodeTimestamp(pes.AsSpan(9, 5)));
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x09, 0xF0 }, pes[14..20]);
        // Reassembled payload is the access unit.
        var payload = pes[14..].ToList();
        foreach (var q in ps.Skip(i + 1).Where(q => Pid(q) == 0x100 && (q[3] & 0x10) != 0)) payload.AddRange(q[PayloadStart(q)..]);
        Assert.Equal(au, payload);
    }

    [Fact]
    public void AudioPesIsBoundedPrivateStream1()
    {
        var m = new TsMuxer();
        m.EnqueueAudio(SilentAc3.Frame, 99_999);
        var first = Packets(m.Produce(700)).First(p => Pid(p) == 0x101 && (p[1] & 0x40) != 0);
        var pes = first[PayloadStart(first)..];
        Assert.Equal(new byte[] { 0, 0, 1, 0xBD }, pes[..4]);
        Assert.Equal(3 + 5 + 768, (pes[4] << 8) | pes[5]);
        Assert.Equal(99_999ul, TsMuxer.DecodeTimestamp(pes.AsSpan(9, 5)));
        Assert.True(pes[14] == 0x0B && pes[15] == 0x77); // AC-3 sync word
    }

    [Theory]
    [InlineData(0ul)]
    [InlineData(1ul)]
    [InlineData(90_000ul)]
    [InlineData(0x1_FFFF_FFFFul)]
    [InlineData(8_589_934_591ul / 3)]
    public void TimestampRoundTrip(ulong t) => Assert.Equal(t, TsMuxer.DecodeTimestamp(TsMuxer.Timestamp(2, t)));

    [Fact]
    public void LateFramesAreCounted()
    {
        var m = new TsMuxer();
        // A 1 MB keyframe whose DTS is only 100ms ahead: at 10 Mbit/s it takes 0.8s to send.
        m.EnqueueVideo(new byte[1_000_000], m.Now90 + 9000, keyframe: true);
        _ = m.Produce(7000);
        var s = m.TakeStats();
        Assert.True(s.VideoFrames == 1 && s.LateFrames == 1 && s.MinKeyframeSlackMs < -500);
        // The same frame with 1s of slack is on time.
        m.EnqueueVideo(new byte[1_000_000], m.Now90 + 90_000, keyframe: true);
        _ = m.Produce(7000);
        var s2 = m.TakeStats();
        Assert.True(s2.LateFrames == 0 && s2.MinSlackMs > 100);
        // A late audio frame counts as late audio, not as a late video frame.
        m.EnqueueAudio(new byte[768], m.Now90);
        _ = m.Produce(50);
        var s3 = m.TakeStats();
        Assert.True(s3.LateFrames == 0 && s3.LateAudioFrames == 1 && s3.MinAudioSlackMs < 0);
    }

    [Fact]
    public void KeyOffsetMarksTablesPcrThenKeyframe()
    {
        var m = new TsMuxer();
        _ = m.ProduceChunk(1000); // past the startup tables
        m.EnqueueVideo(Enumerable.Repeat((byte)9, 5000).ToArray(), m.Now90 + 18_000, keyframe: false);
        m.EnqueueVideo(Enumerable.Repeat((byte)7, 5000).ToArray(), m.Now90 + 21_600, keyframe: true);
        var (d, k) = m.ProduceChunk(300);
        Assert.NotNull(k);
        var ps = Packets(d[k.Value..]);
        Assert.True(Pid(ps[0]) == 0 && Pid(ps[1]) == 0x1000 && Pid(ps[2]) == 0x11);
        Assert.NotNull(Pcr(ps[3]));
        Assert.True(Pid(ps[4]) == 0x100 && (ps[4][1] & 0x40) != 0 && (ps[4][5] & 0x40) != 0); // PES start, random access
        // a chunk without a keyframe has no offset
        Assert.Null(m.ProduceChunk(300).KeyOffset);
    }

    [Fact]
    public void SilentAc3FrameIsOneSyncFrame()
    {
        Assert.Equal(768, SilentAc3.Frame.Length);
        Assert.True(SilentAc3.Frame[0] == 0x0B && SilentAc3.Frame[1] == 0x77);
        Assert.Equal(0x14, SilentAc3.Frame[4]); // fscod 48 kHz, frmsizecod 192 kbit/s
    }

    /// <summary>The same bytes as the Swift app's frame: guards against a copy error in the base64.</summary>
    [Fact]
    public void SilentAc3FrameCrcMatchesSwiftCopy() => Assert.Equal("66ECD57B", // computed from the Swift source's base64 with Python
 Crc.Mpeg32(SilentAc3.Frame).ToString("X8"));
}
