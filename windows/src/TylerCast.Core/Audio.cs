namespace TylerCast.Core;

/// <summary>
/// Where the pipeline's sound comes from (Windows: WASAPI loopback + a Media
/// Foundation encoder). Frames are complete: ADTS AAC, or AC-3 sync frames.
/// </summary>
public interface IAudioSource : IDisposable
{
    string Name { get; }
    AudioCodec Codec { get; }
    /// <summary>Starts producing encoded frames with the host time of their first sample.</summary>
    void Start(Action<byte[], double> onFrame, Action<string>? onError = null);
    void Stop();
}

/// <summary>
/// Turns captured PCM, which arrives in uneven chunks, with gaps (loopback capture
/// sends nothing while the computer is quiet) and small timing jitter, into one
/// continuous 48 kHz stereo timeline, cut into fixed blocks for the encoder.
/// Block k starts at anchor + k·BlockSamples/48000 exactly. A port of the Swift
/// AACEncoder's push/padSilence logic, without the encoder.
/// </summary>
public sealed class PcmTimeline(int blockSamples, Action<float[], double> onBlock)
{
    public const int SampleRate = 48_000;
    public int BlockSamples { get; } = blockSamples;

    private readonly Lock _gate = new();
    private readonly List<float> _fifo = [];
    private double? _anchor;
    private long _samplesIn;
    private long _blocksOut;

    /// <summary>Host time the timeline started at (block 0), once it has.</summary>
    public double? Anchor { get { lock (_gate) return _anchor; } }

    /// <summary>Adds interleaved stereo 48 kHz samples whose first sample was captured at <paramref name="host"/>.</summary>
    public void Push(ReadOnlySpan<float> samples, double host)
    {
        lock (_gate) PushLocked(samples.ToArray(), host);
    }

    /// <summary>
    /// Fills silence up to <paramref name="host"/> − <paramref name="lag"/> when no sound has arrived, so
    /// the TV always gets a continuous audio track. Call every few tens of milliseconds.
    /// </summary>
    public void PadSilence(double host, double lag = 0.1)
    {
        lock (_gate)
        {
            var target = host - lag;
            if (_anchor is not { } anchor)
            {
                _anchor = target;
                _samplesIn = 0;
                _blocksOut = 0;
                return;
            }
            var have = anchor + (double)_samplesIn / SampleRate;
            var missing = (int)((target - have) * SampleRate);
            if (missing > 0) PushLocked(new float[missing * 2], have);
        }
    }

    private void PushLocked(float[] samples, double host)
    {
        if (_anchor is { } anchor)
        {
            var expected = anchor + (double)_samplesIn / SampleRate;
            var diff = host - expected;
            if (Math.Abs(diff) > 0.5)
            {
                // A real jump (capture restarted): start a new timeline here.
                Log.Debug($"Audio: timing jump of {(int)(diff * 1000)}ms; re-anchoring");
                _fifo.Clear();
                _anchor = null;
            }
            else if (diff > 0.002)
            {
                // Arrived a little late (e.g. after padded silence): fill the hole.
                samples = [.. new float[(int)(diff * SampleRate) * 2], .. samples];
            }
            else if (diff < -0.002)
            {
                // Overlaps what's already queued (padding ran ahead): drop the overlap.
                var drop = Math.Min((int)(-diff * SampleRate) * 2, samples.Length);
                samples = samples[drop..];
                if (samples.Length == 0) return;
            }
        }
        if (_anchor is null)
        {
            _anchor = host;
            _samplesIn = 0;
            _blocksOut = 0;
        }
        _samplesIn += samples.Length / 2;
        _fifo.AddRange(samples);
        var n = BlockSamples * 2;
        while (_fifo.Count >= n)
        {
            var block = _fifo.GetRange(0, n).ToArray();
            _fifo.RemoveRange(0, n);
            var start = _anchor!.Value + (double)(_blocksOut * BlockSamples) / SampleRate;
            _blocksOut++;
            onBlock(block, start);
        }
    }
}

public static class Pcm
{
    /// <summary>
    /// Any channel count and rate to interleaved stereo 48 kHz (linear interpolation).
    /// Mono is copied to both sides; extra channels (5.1 laptops) keep front left/right.
    /// <paramref name="phase"/> carries the fractional position between calls so a stream resamples seamlessly.
    /// </summary>
    public static float[] ToStereo48k(ReadOnlySpan<float> input, int channels, int rate, ref double phase, ref (float L, float R) last)
    {
        var frames = input.Length / channels;
        if (rate == 48_000 && channels == 2) return input.ToArray();
        // Stereo frames at the source rate, with the previous call's last frame in front.
        var st = new float[(frames + 1) * 2];
        st[0] = last.L;
        st[1] = last.R;
        for (var f = 0; f < frames; f++)
        {
            var l = input[f * channels];
            var r = channels > 1 ? input[f * channels + 1] : l;
            st[(f + 1) * 2] = l;
            st[(f + 1) * 2 + 1] = r;
        }
        if (frames > 0) last = (st[frames * 2], st[frames * 2 + 1]);
        if (rate == 48_000) return st[2..];
        var step = (double)rate / 48_000;
        var outList = new List<float>((int)(frames / step) * 2 + 4);
        // Position 1.0 is the first new frame; phase continues from the last call.
        var pos = 1 + phase;
        while (pos <= frames)
        {
            var i = (int)pos;
            var t = (float)(pos - i);
            var j = Math.Min(i + 1, frames);
            outList.Add(st[i * 2] + (st[j * 2] - st[i * 2]) * t);
            outList.Add(st[i * 2 + 1] + (st[j * 2 + 1] - st[i * 2 + 1]) * t);
            pos += step;
        }
        phase = pos - 1 - frames;
        return [.. outList];
    }

    /// <summary>Float [-1, 1] to 16-bit little-endian PCM (what Windows' audio encoders take).</summary>
    public static byte[] ToInt16(ReadOnlySpan<float> s)
    {
        var b = new byte[s.Length * 2];
        for (var i = 0; i < s.Length; i++)
        {
            var v = (short)Math.Clamp((int)MathF.Round(s[i] * 32767f), short.MinValue, short.MaxValue);
            b[i * 2] = (byte)v;
            b[i * 2 + 1] = (byte)(v >> 8);
        }
        return b;
    }

    /// <summary>
    /// Strength of one frequency in a mono or interleaved-stereo (left used) signal, as the
    /// amplitude of a sine at that frequency (Goertzel). Used by selftest to hear its own tone.
    /// </summary>
    public static double ToneLevel(ReadOnlySpan<float> samples, int channels, int rate, double hz)
    {
        var n = samples.Length / channels;
        if (n == 0) return 0;
        var w = 2 * Math.PI * hz / rate;
        var c = 2 * Math.Cos(w);
        double s1 = 0, s2 = 0;
        for (var i = 0; i < n; i++)
        {
            var s0 = samples[i * channels] + c * s1 - s2;
            s2 = s1;
            s1 = s0;
        }
        var power = s1 * s1 + s2 * s2 - c * s1 * s2;
        return 2 * Math.Sqrt(Math.Max(power, 0)) / n;
    }

    /// <summary>Interleaved stereo sine, <paramref name="seconds"/> long, at 48 kHz.</summary>
    public static float[] Sine(double hz, double seconds, float amplitude = 0.5f, int rate = 48_000)
    {
        var n = (int)(seconds * rate);
        var o = new float[n * 2];
        for (var i = 0; i < n; i++) o[i * 2] = o[i * 2 + 1] = amplitude * (float)Math.Sin(2 * Math.PI * hz * i / rate);
        return o;
    }

    /// <summary>A 16-bit stereo WAV file of <paramref name="stereo"/> (for playing a test tone).</summary>
    public static byte[] Wav(ReadOnlySpan<float> stereo, int rate = 48_000)
    {
        var data = ToInt16(stereo);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + data.Length); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(rate); w.Write(rate * 4);
        w.Write((short)4); w.Write((short)16);
        w.Write("data"u8); w.Write(data.Length); w.Write(data);
        w.Flush();
        return ms.ToArray();
    }
}

public static class AnnexB
{
    public static readonly byte[] Aud = [0, 0, 0, 1, 0x09, 0xF0];

    /// <summary>(start of the NAL's start code, start of its payload, type) for each NAL unit.</summary>
    public static List<(int Start, int Payload, int Type)> Nals(ReadOnlySpan<byte> d)
    {
        var o = new List<(int, int, int)>();
        for (var i = 0; i + 3 < d.Length; i++)
        {
            if (d[i] != 0 || d[i + 1] != 0) continue;
            var sc = d[i + 2] == 1 ? 3 : d[i + 2] == 0 && d[i + 3] == 1 ? 4 : 0;
            if (sc == 0 || i + sc >= d.Length) continue;
            o.Add((i, i + sc, d[i + sc] & 0x1F));
            i += sc;
        }
        return o;
    }
}

/// <summary>
/// Makes an encoder's access units what the muxer and the TV expect, whatever
/// encoder made them: an AUD first, and SPS/PPS in front of every IDR (some
/// Windows encoders send them only once, so a TV joining later couldn't decode).
/// </summary>
public sealed class AccessUnitFixer
{
    private byte[]? _sps, _pps;

    /// <summary>Seeds the parameter sets from the encoder's sequence header (MF_MT_MPEG_SEQUENCE_HEADER), if it gives one.</summary>
    public void Learn(ReadOnlySpan<byte> annexB) => Scan(annexB, out _, out _);

    /// <summary>Returns the fixed access unit and whether it is a keyframe (has an IDR slice).</summary>
    public (byte[] Au, bool Key) Fix(ReadOnlySpan<byte> au)
    {
        Scan(au, out var hasSpsPps, out var key);
        var nals = AnnexB.Nals(au);
        var hasAud = nals.Count > 0 && nals[0].Type == 9;
        var o = new List<byte>(au.Length + 64);
        if (!hasAud) o.AddRange(AnnexB.Aud);
        if (key && !hasSpsPps && _sps is not null && _pps is not null)
        {
            // After the AUD (if the encoder sent one), before everything else.
            if (hasAud)
            {
                var afterAud = nals.Count > 1 ? nals[1].Start : au.Length;
                o.AddRange(au[..afterAud]);
                AddParams(o);
                o.AddRange(au[afterAud..]);
                return ([.. o], key);
            }
            AddParams(o);
        }
        o.AddRange(au);
        return ([.. o], key);
    }

    private void AddParams(List<byte> o)
    {
        o.AddRange([0, 0, 0, 1]);
        o.AddRange(_sps!);
        o.AddRange([0, 0, 0, 1]);
        o.AddRange(_pps!);
    }

    private void Scan(ReadOnlySpan<byte> d, out bool hasSpsPps, out bool key)
    {
        var nals = AnnexB.Nals(d);
        bool sps = false, pps = false;
        key = false;
        for (var k = 0; k < nals.Count; k++)
        {
            var (_, payload, type) = nals[k];
            var end = k + 1 < nals.Count ? nals[k + 1].Start : d.Length;
            // Trailing zero bytes belong to the next start code, not this NAL.
            while (end > payload && d[end - 1] == 0) end--;
            switch (type)
            {
                case 7: _sps = d[payload..end].ToArray(); sps = true; break;
                case 8: _pps = d[payload..end].ToArray(); pps = true; break;
                case 5: key = true; break;
            }
        }
        hasSpsPps = sps && pps;
    }
}

/// <summary>Muxes encoded frames into a TS file with no pacer (for selftest files Claude checks on the Mac).</summary>
public sealed class OfflineMux(TsMuxer.Config? config = null)
{
    private readonly TsMuxer _m = new(config ?? new TsMuxer.Config { MuxRate = 20_000_000 });
    private readonly MemoryStream _out = new();

    public void Video(byte[] au, double seconds, bool key)
    {
        _m.EnqueueVideo(au, TsMuxer.Base90 + (ulong)(seconds * 90_000), key);
        Drain();
    }

    public void Audio(byte[] frame, double seconds)
    {
        _m.EnqueueAudio(frame, TsMuxer.Base90 + (ulong)(seconds * 90_000));
        Drain();
    }

    private void Drain()
    {
        while (_m.QueuedPackets > 0) _out.Write(_m.NextPacket());
    }

    public byte[] ToArray()
    {
        Drain();
        return _out.ToArray();
    }
}

public static class Ac3
{
    private static readonly int[] KbpsByCode = [32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 448, 512, 576, 640];

    /// <summary>Size in bytes of the 48 kHz AC-3 sync frame starting at <paramref name="f"/>, or 0 if it isn't one.</summary>
    public static int FrameSize(ReadOnlySpan<byte> f)
    {
        if (f.Length < 5 || f[0] != 0x0B || f[1] != 0x77) return 0;
        var fscod = f[4] >> 6;
        var code = (f[4] & 0x3F) >> 1;
        if (fscod != 0 || code >= KbpsByCode.Length) return 0; // 48 kHz only (what we encode)
        return KbpsByCode[code] * 4; // 1536 samples at 48 kHz = 32ms: kbit/s × 32ms / 8 bits
    }

    /// <summary>Splits an encoder's output into whole sync frames (some encoders return several at once).</summary>
    public static List<byte[]> Split(ReadOnlySpan<byte> d)
    {
        var o = new List<byte[]>();
        var i = 0;
        while (i < d.Length)
        {
            var n = FrameSize(d[i..]);
            if (n == 0 || i + n > d.Length)
            {
                // Not a frame boundary we understand: pass the rest through whole.
                o.Add(d[i..].ToArray());
                break;
            }
            o.Add(d.Slice(i, n).ToArray());
            i += n;
        }
        return o;
    }
}
