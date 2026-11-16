using System.Diagnostics;
using System.Runtime.InteropServices;
using TylerCast.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.Media.Audio.Endpoints;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.Com;

namespace TylerCast.Windows;

/// <summary>
/// What the computer is playing, from WASAPI loopback on the default output device
/// (whatever the laptop plays through: speakers, headphones, HDMI). No virtual
/// cable needed. Delivers interleaved stereo 48 kHz float with the host time
/// (QPC, the same clock as <see cref="Pacer.HostSeconds"/>) of each chunk's first sample.
/// </summary>
public sealed unsafe class Loopback : IDisposable
{
    public string DeviceName { get; }
    public string MixFormat { get; }
    public int Rate { get; }
    public int Channels { get; }

    private readonly IMMDevice _device;
    private readonly IAudioClient _client;
    private readonly IAudioCaptureClient _capture;
    private readonly bool _float;
    private readonly int _bits;
    private Thread? _thread;
    private volatile bool _running;
    private double _phase;
    private (float, float) _last;

    public Loopback()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, out _device);
        DeviceName = DeviceFriendlyName(_device);
        var iid = typeof(IAudioClient).GUID;
        _device.Activate(&iid, CLSCTX.CLSCTX_ALL, null, out var c);
        _client = (IAudioClient)c;
        WAVEFORMATEX* fmt;
        _client.GetMixFormat(&fmt);
        try
        {
            Rate = (int)fmt->nSamplesPerSec;
            Channels = fmt->nChannels;
            _bits = fmt->wBitsPerSample;
            _float = fmt->wFormatTag == 3 /* WAVE_FORMAT_IEEE_FLOAT */
                     || (fmt->wFormatTag == PInvoke.WAVE_FORMAT_EXTENSIBLE
                         && ((WAVEFORMATEXTENSIBLE*)fmt)->SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"));
            MixFormat = $"{Rate} Hz, {Channels} ch, {_bits}-bit {(_float ? "float" : "integer")}";
            // 100ms buffer, shared mode, loopback: we read what the device is playing.
            _client.Initialize(AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED, PInvoke.AUDCLNT_STREAMFLAGS_LOOPBACK, 1_000_000, 0, fmt, null);
        }
        finally { PInvoke.CoTaskMemFree(fmt); }
        var ciid = typeof(IAudioCaptureClient).GUID;
        _client.GetService(&ciid, out var p);
        _capture = (IAudioCaptureClient)Marshal.GetObjectForIUnknown((nint)p);
        Marshal.Release((nint)p);
        if (!_float && _bits is not (16 or 24 or 32)) throw new NotSupportedException($"mix format {MixFormat}");
    }

    private static string DeviceFriendlyName(IMMDevice d)
    {
        try
        {
            PWSTR id;
            d.GetId(&id);
            try { return id.ToString(); }
            finally { PInvoke.CoTaskMemFree(id.Value); }
        }
        catch (COMException) { return "default output"; }
    }

    /// <summary>Starts calling <paramref name="onAudio"/> (stereo 48 kHz float, host seconds of the first sample) on its own thread.</summary>
    public void Start(Action<float[], double> onAudio, Action<string>? onError = null)
    {
        _client.Start();
        _running = true;
        _thread = new Thread(() => Loop(onAudio, onError)) { Name = "loopback", IsBackground = true, Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    private void Loop(Action<float[], double> onAudio, Action<string>? onError)
    {
        var bytesPerFrame = Channels * _bits / 8;
        try
        {
            while (_running)
            {
                Thread.Sleep(10);
                while (true)
                {
                    _capture.GetNextPacketSize(out var next);
                    if (next == 0) break;
                    byte* data;
                    ulong qpc;
                    var hr = _capture.GetBuffer(&data, out var frames, out var flags, null, &qpc);
                    if (hr.Failed) throw new COMException("GetBuffer", hr.Value);
                    var host = qpc / 1e7; // QPC position in 100ns units: the same clock as Stopwatch
                    var src = new float[frames * Channels];
                    if ((flags & 0x2 /* AUDCLNT_BUFFERFLAGS_SILENT */) == 0)
                    {
                        var bytes = new ReadOnlySpan<byte>(data, (int)(frames * bytesPerFrame));
                        if (_float) MemoryMarshal.Cast<byte, float>(bytes).CopyTo(src);
                        else
                        {
                            for (var i = 0; i < src.Length; i++)
                            {
                                src[i] = _bits switch
                                {
                                    16 => BitConverter.ToInt16(bytes.Slice(i * 2, 2)) / 32768f,
                                    24 => ((bytes[i * 3] << 8 | bytes[i * 3 + 1] << 16 | bytes[i * 3 + 2] << 24) >> 8) / 8388608f,
                                    _ => BitConverter.ToInt32(bytes.Slice(i * 4, 4)) / 2147483648f,
                                };
                            }
                        }
                    }
                    _capture.ReleaseBuffer(frames);
                    var stereo = Pcm.ToStereo48k(src, Channels, Rate, ref _phase, ref _last);
                    if (stereo.Length > 0) onAudio(stereo, host);
                }
            }
        }
        catch (Exception e) when (_running)
        {
            Log.Error($"Loopback capture failed: {Captures.Describe(e)}");
            onError?.Invoke($"loopback capture failed: {e.Message}");
        }
    }

    private T WithVolume<T>(Func<IAudioEndpointVolume, T> f)
    {
        var iid = typeof(IAudioEndpointVolume).GUID;
        _device.Activate(&iid, CLSCTX.CLSCTX_ALL, null, out var v);
        var vol = (IAudioEndpointVolume)v;
        try { return f(vol); }
        finally { Marshal.ReleaseComObject(vol); }
    }

    /// <summary>Mutes or unmutes the device itself (the laptop's speakers). Returns the previous state.</summary>
    public bool SetMute(bool mute) => WithVolume(vol =>
    {
        BOOL was;
        vol.GetMute(&was);
        vol.SetMute(mute, null);
        return (bool)was;
    });

    public bool Muted => WithVolume(vol =>
    {
        BOOL m;
        vol.GetMute(&m);
        return (bool)m;
    });

    /// <summary>The device's master volume, 0 to 1.</summary>
    public float Volume => WithVolume(vol =>
    {
        vol.GetMasterVolumeLevelScalar(out var v);
        return v;
    });

    public void Stop()
    {
        _running = false;
        _thread?.Join(500);
        try { _client.Stop(); } catch (COMException) { }
    }

    public void Dispose()
    {
        Stop();
        Marshal.ReleaseComObject(_capture);
        Marshal.ReleaseComObject(_client);
        Marshal.ReleaseComObject(_device);
    }
}

/// <summary>
/// A Media Foundation audio encoder: AAC-LC (ADTS added here) or AC-3 (Windows'
/// Dolby Digital encoder), 48 kHz stereo, from 16-bit PCM.
/// </summary>
public sealed unsafe class MfAudioEncoder : IDisposable
{
    public AudioCodec Codec { get; }
    public string Name { get; }
    public int Bitrate { get; }
    /// <summary>(complete frame, sample time in 100ns of its first sample).</summary>
    public event Action<byte[], long>? Output;

    private readonly IMFTransform _t;
    private readonly bool _providesSamples;
    private readonly uint _outputSize;

    /// <summary>Encoders Windows has for <paramref name="codec"/>, best first (empty if none).</summary>
    public static List<string> Available(AudioCodec codec)
    {
        try
        {
            var all = Enumerate(codec);
            foreach (var (_, a) in all) Marshal.ReleaseComObject(a);
            return all.Select(e => e.Info.Name).ToList();
        }
        catch (Exception e)
        {
            Log.Warn($"Listing {codec} encoders failed: {Captures.Describe(e)}");
            return [];
        }
    }

    private static List<(EncoderInfo Info, IMFActivate Activate)> Enumerate(AudioCodec codec) =>
        Mf.Enumerate(PInvoke.MFT_CATEGORY_AUDIO_ENCODER, PInvoke.MFMediaType_Audio, PInvoke.MFAudioFormat_PCM,
            codec == AudioCodec.Aac ? PInvoke.MFAudioFormat_AAC : PInvoke.MFAudioFormat_Dolby_AC3);

    public MfAudioEncoder(AudioCodec codec)
    {
        Codec = codec;
        var all = Enumerate(codec);
        if (all.Count == 0) throw new PipelineException($"Windows has no {codec} encoder here");
        try
        {
            Name = all[0].Info.Name;
            _t = Mf.Activate(all[0].Activate);
        }
        finally { foreach (var (_, a) in all) Marshal.ReleaseComObject(a); }

        // Encoders disagree on the order: some want the output type first, others the input
        // type first (and only list output types once it is set). Try one order, then the other.
        var wantBytes = codec == AudioCodec.Aac ? 20_000u : 24_000u; // 160 / 192 kbit/s
        Exception? first = null;
        foreach (var outputFirst in new[] { true, false })
        {
            try
            {
                if (outputFirst)
                {
                    Bitrate = SetOutput(codec, wantBytes);
                    _t.SetInputType(0, PcmInput(), 0);
                }
                else
                {
                    _t.SetInputType(0, PcmInput(), 0);
                    Bitrate = SetOutput(codec, wantBytes);
                }
                first = null;
                break;
            }
            catch (COMException e)
            {
                Log.Info($"{Name}: setting types {(outputFirst ? "output first" : "input first")} failed: {Captures.Describe(e)}");
                first ??= e;
            }
        }
        if (first is not null) throw first;

        MFT_OUTPUT_STREAM_INFO si;
        _t.GetOutputStreamInfo(0, &si);
        _providesSamples = (si.dwFlags & 0x100) != 0;
        _outputSize = Math.Max(si.cbSize, 8192u);
        _t.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
        _t.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
        Log.Info($"Audio encoder: {Name}, {codec} {Bitrate / 1000} kbit/s");
    }

    private static IMFMediaType PcmInput()
    {
        PInvoke.MFCreateMediaType(out var input).ThrowOnFailure();
        Mf.Set(input, PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Audio);
        Mf.Set(input, PInvoke.MF_MT_SUBTYPE, PInvoke.MFAudioFormat_PCM);
        Mf.Set(input, PInvoke.MF_MT_AUDIO_SAMPLES_PER_SECOND, 48_000u);
        Mf.Set(input, PInvoke.MF_MT_AUDIO_NUM_CHANNELS, 2u);
        Mf.Set(input, PInvoke.MF_MT_AUDIO_BITS_PER_SAMPLE, 16u);
        Mf.Set(input, PInvoke.MF_MT_AUDIO_BLOCK_ALIGNMENT, 4u);
        Mf.Set(input, PInvoke.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 192_000u);
        return input;
    }

    /// <summary>Sets the offered output type closest to <paramref name="wantBytes"/>/s at 48 kHz stereo
    /// (or a built one if none is offered). Returns its bit rate.</summary>
    private int SetOutput(AudioCodec codec, uint wantBytes)
    {
        var sub = codec == AudioCodec.Aac ? PInvoke.MFAudioFormat_AAC : PInvoke.MFAudioFormat_Dolby_AC3;
        IMFMediaType? best = null;
        var bestDiff = long.MaxValue;
        for (uint i = 0; ; i++)
        {
            var hr = _t.GetOutputAvailableType(0, i, out var t);
            if (hr.Failed || t is null) break;
            var ok = Mf.GetGuid(t, PInvoke.MF_MT_SUBTYPE) == sub && Mf.GetUInt32(t, PInvoke.MF_MT_AUDIO_SAMPLES_PER_SECOND) == 48_000
                     && Mf.GetUInt32(t, PInvoke.MF_MT_AUDIO_NUM_CHANNELS) == 2
                     && (codec != AudioCodec.Aac || (Mf.GetUInt32(t, PInvoke.MF_MT_AAC_PAYLOAD_TYPE) ?? 0) == 0);
            var diff = Math.Abs((long)(Mf.GetUInt32(t, PInvoke.MF_MT_AUDIO_AVG_BYTES_PER_SECOND) ?? 0) - wantBytes);
            if (ok && diff < bestDiff)
            {
                if (best is not null) Marshal.ReleaseComObject(best);
                best = t;
                bestDiff = diff;
            }
            else Marshal.ReleaseComObject(t);
        }
        if (best is null)
        {
            PInvoke.MFCreateMediaType(out best).ThrowOnFailure();
            Mf.Set(best, PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Audio);
            Mf.Set(best, PInvoke.MF_MT_SUBTYPE, sub);
            Mf.Set(best, PInvoke.MF_MT_AUDIO_SAMPLES_PER_SECOND, 48_000u);
            Mf.Set(best, PInvoke.MF_MT_AUDIO_NUM_CHANNELS, 2u);
            Mf.Set(best, PInvoke.MF_MT_AUDIO_BITS_PER_SAMPLE, 16u);
            Mf.Set(best, PInvoke.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, wantBytes);
            if (codec == AudioCodec.Aac) Mf.Set(best, PInvoke.MF_MT_AAC_PAYLOAD_TYPE, 0u);
        }
        _t.SetOutputType(0, best, 0);
        return (int)(Mf.GetUInt32(best, PInvoke.MF_MT_AUDIO_AVG_BYTES_PER_SECOND) ?? wantBytes) * 8;
    }

    /// <summary>Encodes 16-bit stereo 48 kHz PCM whose first sample is at <paramref name="time"/> (100ns).</summary>
    public void Encode(byte[] pcm16, long time)
    {
        var sample = Mf.Sample(pcm16, time, pcm16.Length / 4 * 10_000_000L / 48_000);
        var hr = _t.ProcessInput(0, sample, 0);
        if (hr == HRESULT.MF_E_NOTACCEPTING)
        {
            Drain();
            hr = _t.ProcessInput(0, sample, 0);
        }
        Marshal.ReleaseComObject(sample);
        hr.ThrowOnFailure();
        Drain();
    }

    private void Drain()
    {
        while (true)
        {
            var buf = new MFT_OUTPUT_DATA_BUFFER[1];
            if (!_providesSamples)
            {
                PInvoke.MFCreateMemoryBuffer(_outputSize, out var mb).ThrowOnFailure();
                PInvoke.MFCreateSample(out var s).ThrowOnFailure();
                s.AddBuffer(mb);
                Marshal.ReleaseComObject(mb);
                buf[0].pSample = s;
            }
            var hr = _t.ProcessOutput(0, 1, buf, out _);
            var sample = buf[0].pSample;
            try
            {
                if (hr == HRESULT.MF_E_TRANSFORM_NEED_MORE_INPUT) return;
                hr.ThrowOnFailure();
                if (sample is null) continue;
                sample.GetSampleTime(out var time);
                var data = Mf.Bytes(sample);
                if (data.Length == 0) continue;
                if (Codec == AudioCodec.Aac) Output?.Invoke([.. Adts.Header(data.Length), .. data], time);
                else
                {
                    var t = time;
                    foreach (var f in Ac3.Split(data))
                    {
                        Output?.Invoke(f, t);
                        t += 320_000; // 1536 samples at 48 kHz = 32ms
                    }
                }
            }
            finally
            {
                if (sample is not null) Marshal.ReleaseComObject(sample);
                if (buf[0].pEvents is not null) Marshal.ReleaseComObject(buf[0].pEvents);
            }
        }
    }

    public void Dispose()
    {
        try { _t.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0); } catch (COMException) { }
        Marshal.ReleaseComObject(_t);
    }
}

/// <summary>
/// The computer's sound for the TV: loopback → continuous 48 kHz timeline (silence
/// padded while nothing plays) → AAC or AC-3. Optionally mutes the laptop's own
/// speakers while running and puts them back after.
/// </summary>
public sealed class WindowsAudioSource(AudioCodec codec, bool muteComputer) : IAudioSource
{
    public string Name { get; private set; } = $"computer's sound, {codec}";
    public AudioCodec Codec { get; } = codec;

    private Loopback? _loop;
    private MfAudioEncoder? _enc;
    private PcmTimeline? _timeline;
    private System.Threading.Timer? _padder;
    /// <summary>Host time of the timeline's start, as the encoder's sample times count from it.</summary>
    private double _anchor;
    private bool? _wasMuted;
    private readonly Lock _gate = new();

    public void Start(Action<byte[], double> onFrame, Action<string>? onError = null)
    {
        _enc = new MfAudioEncoder(Codec);
        _loop = new Loopback();
        Name = $"computer's sound ({_loop.MixFormat}) → {_enc.Name}, {Codec} {_enc.Bitrate / 1000}k";
        // 10ms blocks; the encoder buffers into its own frame size.
        _timeline = new PcmTimeline(480, (block, host) =>
        {
            lock (_gate)
            {
                // Read the anchor here, inside the timeline's callback, not from the encoder's
                // output callback (which runs inside this one).
                _anchor = _timeline!.Anchor ?? host;
                try { _enc.Encode(Pcm.ToInt16(block), (long)Math.Round((host - _anchor) * 1e7)); }
                catch (Exception e) { onError?.Invoke($"{Codec} encoding failed: {Captures.Describe(e)}"); }
            }
        });
        _enc.Output += (frame, time) => onFrame(frame, _anchor + time / 1e7);
        _loop.Start((pcm, host) => _timeline.Push(pcm, host), onError);
        _padder = new System.Threading.Timer(_ => _timeline.PadSilence(Pacer.HostSeconds()), null, 0, 20);
        if (muteComputer)
        {
            _wasMuted = _loop.SetMute(true);
            Log.Info("Muted this computer's speakers while mirroring (sound on the TV only)");
        }
        Log.Info($"Audio: {Name}");
    }

    public void Stop()
    {
        _padder?.Dispose();
        _padder = null;
        if (_loop is not null && _wasMuted is { } was)
        {
            try { _loop.SetMute(was); Log.Info("Speakers restored"); }
            catch (Exception e) { Log.Warn($"Could not restore the speakers' mute state: {e.Message}"); }
            _wasMuted = null;
        }
        _loop?.Dispose();
        _loop = null;
        lock (_gate)
        {
            _enc?.Dispose();
            _enc = null;
        }
    }

    public void Dispose() => Stop();
}
