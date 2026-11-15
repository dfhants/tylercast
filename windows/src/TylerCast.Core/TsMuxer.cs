namespace TylerCast.Core;

/// <summary>
/// MPEG-TS (188-byte packets) at a constant rate, laid out as ffmpeg's mpegts
/// muxer did for Phase 1 (same PIDs, stream types and descriptors), because
/// that is what the TV accepted. A port of the Swift TSMuxer; tests check the
/// two agree byte for byte where it matters (PAT, PMT).
///
/// Timing model: the stream clock is the byte position. Packet <c>i</c> carries
/// PCR = base + i·1504/muxRate seconds, and the pacer sends packet <c>i</c> at that
/// wall-clock moment, so PCR tracks real time exactly. A frame captured at
/// stream time <c>t</c> gets DTS = t + delay. Its packets go out as soon as it is
/// encoded (ahead of null padding), so it is late only if encode time plus
/// transmission time exceeds the delay. The muxer measures that for every frame
/// (<see cref="Stats"/>), which is the "dts &lt; pcr" ffmpeg used to report.
/// </summary>
public sealed class TsMuxer
{
    public const int PacketSize = 188;
    public const ushort NullPid = 0x1FFF;
    /// <summary>1s of offset so timestamps never start at 0.</summary>
    public const ulong Base90 = 90_000;

    public sealed record Config
    {
        public int MuxRate { get; init; } = 10_000_000;
        public bool IncludeAudio { get; init; } = true;
        public AudioCodec AudioCodec { get; init; } = AudioCodec.Ac3;
        public ushort PmtPid { get; init; } = 0x1000;
        public ushort VideoPid { get; init; } = 0x100;
        public ushort AudioPid { get; init; } = 0x101;
        public ushort ProgramNumber { get; init; } = 1;
        /// <summary>PAT/PMT/SDT repeat (also sent right before every keyframe).</summary>
        public double TablesInterval { get; init; } = 0.1;
        /// <summary>Standalone PCR packets on the video PID (spec max 0.1s).</summary>
        public double PcrInterval { get; init; } = 0.02;
        public string ServiceName { get; init; } = "TylerCast";
    }

    public struct Stats
    {
        public int VideoFrames;
        public int AudioFrames;
        public int Keyframes;
        /// <summary>Frames whose last packet left after PCR had passed their DTS.</summary>
        public int LateFrames;
        /// <summary>Smallest DTS − PCR at a frame's last packet, in milliseconds.</summary>
        public double MinSlackMs;
        public double MinKeyframeSlackMs;
        public int MaxQueuedPackets;
        public int NullPackets;
        public int Packets;

        public static Stats Empty => new() { MinSlackMs = double.PositiveInfinity, MinKeyframeSlackMs = double.PositiveInfinity };
    }

    private abstract record Queued;
    private sealed record TablesMarker : Queued;
    private sealed record PacketItem(byte[] Packet, ushort Pid, ulong? Dts90, bool IsLast, bool Keyframe) : Queued;

    public Config Settings { get; }
    public ulong PacketsEmitted { get; private set; }

    private readonly Dictionary<ushort, byte> _cc = [];
    private readonly Queue<Queued> _queue = new();
    private readonly Queue<byte[]> _pendingTables = new();
    private ulong _nextTablesAt;
    private ulong _nextPcrAt;
    private Stats _stats = Stats.Empty;
    private ulong? _lastKeyStart;

    public TsMuxer(Config? config = null) => Settings = config ?? new Config();

    // ------------------------------------------------------------------ clock

    /// <summary>PCR (27 MHz) of packet index <paramref name="i"/>.</summary>
    public ulong Pcr27AtPacket(ulong i) => Base90 * 300 + i * 1504 * 27_000_000 / (ulong)Settings.MuxRate;
    /// <summary>Stream time (90 kHz) at packet index <paramref name="i"/>.</summary>
    public ulong Time90AtPacket(ulong i) => Pcr27AtPacket(i) / 300;
    public ulong Now90 => Time90AtPacket(PacketsEmitted);
    private ulong PacketsForSeconds(double s) => (ulong)Math.Max(1, s * Settings.MuxRate / 1504);

    public int QueuedPackets => _queue.Count;

    public Stats TakeStats()
    {
        var s = _stats;
        _stats = Stats.Empty;
        return s;
    }

    // ------------------------------------------------------------------ input

    /// <summary>
    /// One H.264 access unit in Annex B form (start codes; AUD, and SPS/PPS on
    /// keyframes, already included). <paramref name="pts90"/> is in stream time; PTS = DTS
    /// because there are no B-frames.
    /// </summary>
    public void EnqueueVideo(ReadOnlySpan<byte> annexB, ulong pts90, bool keyframe)
    {
        if (keyframe) _queue.Enqueue(new TablesMarker());
        var pes = PesPacket(0xE0, pts90, annexB, bounded: false);
        Packetize(pes, Settings.VideoPid, pts90, randomAccess: keyframe);
    }

    /// <summary>One audio frame: an AC-3 sync frame, or an ADTS AAC frame.</summary>
    public void EnqueueAudio(ReadOnlySpan<byte> frame, ulong pts90)
    {
        if (!Settings.IncludeAudio) return;
        byte streamId = Settings.AudioCodec == AudioCodec.Aac ? (byte)0xC0 : (byte)0xBD;
        var pes = PesPacket(streamId, pts90, frame, bounded: true);
        Packetize(pes, Settings.AudioPid, pts90, randomAccess: false);
    }

    private void Packetize(byte[] pes, ushort pid, ulong dts90, bool randomAccess)
    {
        var off = 0;
        var first = true;
        while (off < pes.Length)
        {
            // Room for payload: 184, less an adaptation field if this packet needs one.
            var afMin = first && randomAccess ? 2 : 0;
            var n = Math.Min(pes.Length - off, 184 - afMin);
            var p = new byte[PacketSize];
            var afLen = 184 - n; // total adaptation-field bytes incl. its length byte
            p[0] = 0x47;
            p[1] = (byte)((first ? 0x40 : 0) | ((pid >> 8) & 0x1F));
            p[2] = (byte)(pid & 0xFF);
            p[3] = afLen > 0 ? (byte)0x30 : (byte)0x10; // CC filled in at emit time
            var k = 4;
            if (afLen > 0)
            {
                p[k++] = (byte)(afLen - 1);
                if (afLen > 1)
                {
                    p[k++] = first && randomAccess ? (byte)0x40 : (byte)0x00;
                    for (var j = 0; j < afLen - 2; j++) p[k++] = 0xFF;
                }
            }
            Array.Copy(pes, off, p, k, n);
            off += n;
            _queue.Enqueue(new PacketItem(p, pid, dts90, off >= pes.Length, randomAccess));
            first = false;
        }
    }

    // ------------------------------------------------------------------ output

    public byte[] Produce(int count) => ProduceChunk(count).Data;

    /// <summary>
    /// <c>KeyOffset</c>: byte offset in <c>Data</c> where a keyframe's PAT/PMT/SDT start,
    /// if one does: the point a newly connected TV can begin decoding at once.
    /// </summary>
    public (byte[] Data, int? KeyOffset) ProduceChunk(int count)
    {
        var d = new byte[count * PacketSize];
        var start = PacketsEmitted;
        _lastKeyStart = null;
        for (var i = 0; i < count; i++) NextPacket().CopyTo(d, i * PacketSize);
        return (d, _lastKeyStart is { } k ? (int)(k - start) * PacketSize : null);
    }

    public byte[] NextPacket()
    {
        _stats.Packets++;
        _stats.MaxQueuedPackets = Math.Max(_stats.MaxQueuedPackets, QueuedPackets);
        try
        {
            if (_pendingTables.Count > 0) return Stamp(_pendingTables.Dequeue());
            if (PacketsEmitted >= _nextTablesAt)
            {
                ScheduleTables();
                return Stamp(_pendingTables.Dequeue());
            }
            if (PacketsEmitted >= _nextPcrAt)
            {
                _nextPcrAt = PacketsEmitted + PacketsForSeconds(Settings.PcrInterval);
                return PcrPacket();
            }
            if (_queue.TryDequeue(out var item))
            {
                switch (item)
                {
                    case TablesMarker:
                        // A keyframe follows: tables, then a PCR, then the frame, so a
                        // TV joining here has everything it needs in the first 4 packets.
                        _lastKeyStart ??= PacketsEmitted;
                        ScheduleTables();
                        _nextPcrAt = PacketsEmitted + 3;
                        return Stamp(_pendingTables.Dequeue());
                    case PacketItem pi:
                        if (pi.IsLast && pi.Dts90 is { } dts) Account(dts, pi.Pid, pi.Keyframe);
                        return Stamp(pi.Packet);
                }
            }
            _stats.NullPackets++;
            return NullPacket;
        }
        finally
        {
            PacketsEmitted++;
        }
    }

    private void Account(ulong dts, ushort pid, bool keyframe)
    {
        // PCR of this packet's last byte ≈ the next packet's.
        var slackMs = ((double)dts - Time90AtPacket(PacketsEmitted + 1)) / 90;
        if (pid == Settings.VideoPid)
        {
            _stats.VideoFrames++;
            if (keyframe)
            {
                _stats.Keyframes++;
                _stats.MinKeyframeSlackMs = Math.Min(_stats.MinKeyframeSlackMs, slackMs);
            }
        }
        else
        {
            _stats.AudioFrames++;
        }
        _stats.MinSlackMs = Math.Min(_stats.MinSlackMs, slackMs);
        if (slackMs < 0) _stats.LateFrames++;
    }

    private void ScheduleTables()
    {
        _pendingTables.Clear();
        _pendingTables.Enqueue(PsiPacket(0, Pat()));
        _pendingTables.Enqueue(PsiPacket(Settings.PmtPid, Pmt()));
        _pendingTables.Enqueue(PsiPacket(0x11, Sdt()));
        _nextTablesAt = PacketsEmitted + PacketsForSeconds(Settings.TablesInterval);
    }

    /// <summary>Sets the continuity counter (payload-bearing packets increment per PID). Packets are used once, so in place.</summary>
    private byte[] Stamp(byte[] p)
    {
        var pid = (ushort)(((p[1] & 0x1F) << 8) | p[2]);
        byte c = _cc.TryGetValue(pid, out var prev) ? (byte)((prev + 1) & 0x0F) : (byte)0;
        _cc[pid] = c;
        p[3] = (byte)((p[3] & 0xF0) | c);
        return p;
    }

    /// <summary>Adaptation-field-only packet carrying the PCR. No payload, so the
    /// continuity counter repeats the PID's last value.</summary>
    private byte[] PcrPacket()
    {
        var pid = Settings.VideoPid;
        byte c = _cc.TryGetValue(pid, out var v) ? v : (byte)0x0F;
        var p = new byte[PacketSize];
        p[0] = 0x47;
        p[1] = (byte)((pid >> 8) & 0x1F);
        p[2] = (byte)(pid & 0xFF);
        p[3] = (byte)(0x20 | c);
        p[4] = 183;
        p[5] = 0x10;
        PcrBytes(Pcr27AtPacket(PacketsEmitted)).CopyTo(p, 6);
        Array.Fill(p, (byte)0xFF, 12, PacketSize - 12);
        return p;
    }

    public static byte[] PcrBytes(ulong pcr27)
    {
        var b = (pcr27 / 300) & 0x1_FFFF_FFFF;
        var ext = pcr27 % 300;
        return [(byte)((b >> 25) & 0xFF), (byte)((b >> 17) & 0xFF), (byte)((b >> 9) & 0xFF),
                (byte)((b >> 1) & 0xFF), (byte)(((b & 1) << 7) | 0x7E | ((ext >> 8) & 1)), (byte)(ext & 0xFF)];
    }

    /// <summary>The shared null packet. Never stamped or modified.</summary>
    public static readonly byte[] NullPacket = MakeNull();

    private static byte[] MakeNull()
    {
        var p = new byte[PacketSize];
        p[0] = 0x47; p[1] = 0x1F; p[2] = 0xFF; p[3] = 0x10;
        Array.Fill(p, (byte)0xFF, 4, 184);
        return p;
    }

    // ------------------------------------------------------------------ PES / PSI

    /// <summary>PES with PTS only (PTS = DTS). Video is unbounded (length 0) as ffmpeg writes it.</summary>
    public static byte[] PesPacket(byte streamId, ulong pts90, ReadOnlySpan<byte> payload, bool bounded)
    {
        const int headerData = 5;
        var len = bounded ? Math.Min(3 + headerData + payload.Length, 0xFFFF) : 0;
        var p = new byte[9 + headerData + payload.Length];
        p[0] = 0; p[1] = 0; p[2] = 1; p[3] = streamId;
        p[4] = (byte)(len >> 8); p[5] = (byte)(len & 0xFF);
        p[6] = 0x80; p[7] = 0x80; p[8] = headerData;
        Timestamp(0x2, pts90).CopyTo(p, 9);
        payload.CopyTo(p.AsSpan(14));
        return p;
    }

    public static byte[] Timestamp(byte prefix, ulong t)
    {
        var v = t & 0x1_FFFF_FFFF;
        return [(byte)((prefix << 4) | (byte)((v >> 29) & 0x0E) | 1),
                (byte)((v >> 22) & 0xFF), (byte)(((v >> 14) & 0xFE) | 1),
                (byte)((v >> 7) & 0xFF), (byte)(((v << 1) & 0xFE) | 1)];
    }

    public static ulong DecodeTimestamp(ReadOnlySpan<byte> b) =>
        ((ulong)(b[0] & 0x0E) << 29) | ((ulong)b[1] << 22) | ((ulong)(b[2] & 0xFE) << 14) | ((ulong)b[3] << 7) | ((ulong)b[4] >> 1);

    /// <summary>A long-form section: table_id, length, the given body (from the
    /// extension field on), then CRC32.</summary>
    internal static byte[] Section(byte tableId, ushort idExt, ReadOnlySpan<byte> body, bool privateBit = false)
    {
        var length = 5 + body.Length + 4;
        var s = new List<byte>(3 + length)
        {
            tableId,
            privateBit ? (byte)(0xF0 | ((length >> 8) & 0x0F)) : (byte)(0xB0 | ((length >> 8) & 0x0F)),
            (byte)(length & 0xFF),
            (byte)(idExt >> 8), (byte)(idExt & 0xFF), 0xC1, 0x00, 0x00,
        };
        s.AddRange(body);
        var crc = Crc.Mpeg32(s);
        s.AddRange([(byte)(crc >> 24), (byte)((crc >> 16) & 0xFF), (byte)((crc >> 8) & 0xFF), (byte)(crc & 0xFF)]);
        return [.. s];
    }

    internal byte[] Pat()
    {
        var c = Settings;
        return Section(0x00, 1, [(byte)(c.ProgramNumber >> 8), (byte)(c.ProgramNumber & 0xFF),
                                 (byte)(0xE0 | (c.PmtPid >> 8)), (byte)(c.PmtPid & 0xFF)]);
    }

    internal byte[] Pmt()
    {
        ushort v = Settings.VideoPid, a = Settings.AudioPid;
        var body = new List<byte>
        {
            (byte)(0xE0 | (v >> 8)), (byte)(v & 0xFF), 0xF0, 0x00,               // PCR PID, no program info
            0x1B, (byte)(0xE0 | (v >> 8)), (byte)(v & 0xFF), 0xF0, 0x00,         // H.264
        };
        if (Settings.IncludeAudio)
        {
            if (Settings.AudioCodec == AudioCodec.Ac3)
                // AC-3, ATSC stream type 0x81 with an "AC-3" registration descriptor (as ffmpeg).
                body.AddRange([0x81, (byte)(0xE0 | (a >> 8)), (byte)(a & 0xFF), 0xF0, 0x06, 0x05, 0x04, 0x41, 0x43, 0x2D, 0x33]);
            else
                // AAC in ADTS, stream type 0x0F, no descriptors (as ffmpeg).
                body.AddRange([0x0F, (byte)(0xE0 | (a >> 8)), (byte)(a & 0xFF), 0xF0, 0x00]);
        }
        return Section(0x02, Settings.ProgramNumber, [.. body]);
    }

    internal byte[] Sdt()
    {
        var provider = "TylerCast"u8.ToArray();
        var name = System.Text.Encoding.UTF8.GetBytes(Settings.ServiceName);
        var desc = new List<byte> { 0x48, (byte)(3 + provider.Length + name.Length), 0x01, (byte)provider.Length };
        desc.AddRange(provider);
        desc.Add((byte)name.Length);
        desc.AddRange(name);
        var pn = Settings.ProgramNumber;
        var body = new List<byte>
        {
            0xFF, 0x01, 0xFF, // original_network_id 0xFF01, reserved
            (byte)(pn >> 8), (byte)(pn & 0xFF), 0xFC,
            (byte)(0x80 | ((desc.Count >> 8) & 0x0F)), (byte)(desc.Count & 0xFF), // running, not scrambled
        };
        body.AddRange(desc);
        return Section(0x42, 1, [.. body], privateBit: true);
    }

    internal static byte[] PsiPacket(ushort pid, byte[] section)
    {
        var p = new byte[PacketSize];
        p[0] = 0x47; p[1] = (byte)(0x40 | ((pid >> 8) & 0x1F)); p[2] = (byte)(pid & 0xFF); p[3] = 0x10; p[4] = 0x00;
        section.CopyTo(p, 5);
        Array.Fill(p, (byte)0xFF, 5 + section.Length, PacketSize - 5 - section.Length);
        return p;
    }
}

public static class Crc
{
    /// <summary>CRC-32/MPEG-2 (poly 0x04C11DB7, init 0xFFFFFFFF, no reflection, no final xor).</summary>
    public static uint Mpeg32(IEnumerable<byte> bytes)
    {
        var crc = 0xFFFF_FFFFu;
        foreach (var b in bytes)
        {
            crc ^= (uint)b << 24;
            for (var i = 0; i < 8; i++) crc = (crc & 0x8000_0000) != 0 ? (crc << 1) ^ 0x04C1_1DB7 : crc << 1;
        }
        return crc;
    }
}
