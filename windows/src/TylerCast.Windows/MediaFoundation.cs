using System.Runtime.InteropServices;
using TylerCast.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;

namespace TylerCast.Windows;

/// <summary>An encoder Windows offers (an MFT), as found by MFTEnumEx.</summary>
public sealed record EncoderInfo(string Name, bool Hardware, bool Async, string Vendor, int Index);

internal static unsafe class Mf
{
    private static int _started;

    public static void Startup()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        PInvoke.MFStartup((PInvoke.MF_SDK_VERSION << 16) | PInvoke.MF_API_VERSION, 0).ThrowOnFailure();
    }

    /// <summary>All encoders for <paramref name="input"/> → <paramref name="output"/>, best first (hardware, then software).</summary>
    public static List<(EncoderInfo Info, IMFActivate Activate)> Enumerate(Guid category, Guid major, Guid input, Guid output)
    {
        Startup();
        var inType = new MFT_REGISTER_TYPE_INFO { guidMajorType = major, guidSubtype = input };
        var outType = new MFT_REGISTER_TYPE_INFO { guidMajorType = major, guidSubtype = output };
        var flags = MFT_ENUM_FLAG.MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG.MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG.MFT_ENUM_FLAG_ASYNCMFT
                    | MFT_ENUM_FLAG.MFT_ENUM_FLAG_LOCALMFT | MFT_ENUM_FLAG.MFT_ENUM_FLAG_SORTANDFILTER;
        IMFActivate_unmanaged** list;
        uint count;
        PInvoke.MFTEnumEx(category, flags, &inType, &outType, out list, &count).ThrowOnFailure();
        var result = new List<(EncoderInfo, IMFActivate)>();
        try
        {
            for (var i = 0; i < count; i++)
            {
                var a = (IMFActivate)Marshal.GetObjectForIUnknown((nint)list[i]);
                Marshal.Release((nint)list[i]);
                var name = GetString(a, PInvoke.MFT_FRIENDLY_NAME_Attribute) ?? $"encoder {i}";
                var hw = GetString(a, PInvoke.MFT_ENUM_HARDWARE_URL_Attribute) is not null;
                var vendor = GetString(a, PInvoke.MFT_ENUM_HARDWARE_VENDOR_ID_Attribute) ?? "";
                var async = GetUInt32(a, PInvoke.MF_TRANSFORM_ASYNC) == 1;
                result.Add((new EncoderInfo(name, hw, async, vendor, i), a));
            }
        }
        finally { PInvoke.CoTaskMemFree(list); }
        return result;
    }

    public static string? GetString(IMFAttributes a, Guid key)
    {
        try
        {
            PWSTR s;
            a.GetAllocatedString(&key, &s, out _);
            try { return s.ToString(); }
            finally { PInvoke.CoTaskMemFree(s.Value); }
        }
        catch (COMException) { return null; }
    }

    public static uint? GetUInt32(IMFAttributes a, Guid key)
    {
        try
        {
            a.GetUINT32(&key, out var v);
            return v;
        }
        catch (COMException) { return null; }
    }

    public static Guid? GetGuid(IMFAttributes a, Guid key)
    {
        try
        {
            Guid g;
            a.GetGUID(&key, &g);
            return g;
        }
        catch (COMException) { return null; }
    }

    public static byte[]? GetBlob(IMFAttributes a, Guid key)
    {
        try
        {
            byte* p;
            a.GetAllocatedBlob(&key, &p, out var n);
            try { return new ReadOnlySpan<byte>(p, (int)n).ToArray(); }
            finally { PInvoke.CoTaskMemFree(p); }
        }
        catch (COMException) { return null; }
    }

    public static void Set(IMFAttributes a, Guid key, uint v) => a.SetUINT32(&key, v);
    public static void Set(IMFAttributes a, Guid key, ulong v) => a.SetUINT64(&key, v);
    public static void Set(IMFAttributes a, Guid key, Guid v) => a.SetGUID(&key, &v);
    public static ulong Pack(uint hi, uint lo) => ((ulong)hi << 32) | lo;

    public static IMFTransform Activate(IMFActivate a)
    {
        var iid = typeof(IMFTransform).GUID;
        a.ActivateObject(&iid, out var p);
        var t = (IMFTransform)Marshal.GetObjectForIUnknown((nint)p);
        Marshal.Release((nint)p);
        return t;
    }

    public static IMFSample Sample(ReadOnlySpan<byte> data, long time, long duration)
    {
        PInvoke.MFCreateMemoryBuffer((uint)data.Length, out var buf).ThrowOnFailure();
        byte* p;
        buf.Lock(&p, null, null);
        data.CopyTo(new Span<byte>(p, data.Length));
        buf.Unlock();
        buf.SetCurrentLength((uint)data.Length);
        PInvoke.MFCreateSample(out var s).ThrowOnFailure();
        s.AddBuffer(buf);
        s.SetSampleTime(time);
        s.SetSampleDuration(duration);
        Marshal.ReleaseComObject(buf);
        return s;
    }

    public static byte[] Bytes(IMFSample s)
    {
        s.ConvertToContiguousBuffer(out var buf);
        try
        {
            byte* p;
            uint len;
            buf.Lock(&p, null, &len);
            try { return new ReadOnlySpan<byte>(p, (int)len).ToArray(); }
            finally { buf.Unlock(); }
        }
        finally { Marshal.ReleaseComObject(buf); }
    }

    /// <summary>The first available type of a stream that has the given subtype, or null.</summary>
    public static IMFMediaType? FindType(Func<uint, (HRESULT, IMFMediaType?)> get, Guid subtype, Func<IMFMediaType, bool>? also = null)
    {
        for (uint i = 0; ; i++)
        {
            var (hr, t) = get(i);
            if (hr.Failed || t is null) return null;
            if (GetGuid(t, PInvoke.MF_MT_SUBTYPE) == subtype && (also is null || also(t))) return t;
            Marshal.ReleaseComObject(t);
        }
    }
}

/// <summary>
/// One Media Foundation H.264 encoder, configured as the TV wants it: Main 4.1,
/// 1920x1080 25fps, no B-frames, IDR every 2s, constant bit rate, low latency.
/// Handles both synchronous MFTs (Microsoft's software encoder) and the
/// asynchronous event-driven ones hardware vendors ship (Intel, AMD, NVIDIA, Qualcomm).
/// Input is NV12 in system memory, which every encoder accepts.
/// </summary>
public sealed unsafe class MfH264Encoder : IDisposable
{
    public EncoderInfo Info { get; }
    /// <summary>(Annex B access unit as the encoder wrote it, its sample time in 100ns, keyframe flag from the encoder).</summary>
    public event Action<byte[], long, bool>? Output;
    public event Action<string>? Failed;
    /// <summary>Settings the encoder refused (logged; the stream may still be fine).</summary>
    public List<string> Refused { get; } = [];

    private readonly IMFTransform _t;
    private readonly IMFMediaEventGenerator? _events;
    private readonly bool _providesSamples;
    private readonly uint _outputSize;
    private readonly ICodecAPI? _codec;
    private readonly long _frameDuration;
    /// <summary>Frames waiting for the async encoder to ask (guarded by _gate, as is _needInput).</summary>
    private readonly Queue<IMFSample> _input = new();
    private const int MaxQueued = 8;
    private readonly Thread? _eventThread;
    private volatile bool _stopping;
    private int _needInput;
    private readonly Lock _gate = new();

    public byte[]? SequenceHeader { get; }
    /// <summary>The encoder settled a new sequence header (SPS/PPS) after starting.</summary>
    public event Action<byte[]>? SequenceHeaderChanged;
    private uint _streamFlags;
    private int _outputs;

    internal static List<(EncoderInfo Info, IMFActivate Activate)> Enumerate() =>
        Mf.Enumerate(PInvoke.MFT_CATEGORY_VIDEO_ENCODER, PInvoke.MFMediaType_Video, PInvoke.MFVideoFormat_NV12, PInvoke.MFVideoFormat_H264);

    internal MfH264Encoder(EncoderInfo info, IMFActivate activate, VideoSettings v)
    {
        Info = info;
        _frameDuration = 10_000_000L / v.Fps;
        _t = Mf.Activate(activate);

        _t.GetAttributes(out var attrs);
        var isAsync = Mf.GetUInt32(attrs, PInvoke.MF_TRANSFORM_ASYNC) == 1;
        if (isAsync)
        {
            Mf.Set(attrs, PInvoke.MF_TRANSFORM_ASYNC_UNLOCK, 1u);
            _events = (IMFMediaEventGenerator)_t;
        }
        try { Mf.Set(attrs, PInvoke.MF_LOW_LATENCY, 1u); } catch (COMException) { Refused.Add("MF_LOW_LATENCY"); }

        _codec = _t as ICodecAPI;
        ConfigureCodec(v);

        // Output first (encoders need it before they list input types), then input.
        PInvoke.MFCreateMediaType(out var outType).ThrowOnFailure();
        Mf.Set(outType, PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
        Mf.Set(outType, PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_H264);
        Mf.Set(outType, PInvoke.MF_MT_AVG_BITRATE, (uint)v.Bitrate);
        Mf.Set(outType, PInvoke.MF_MT_FRAME_SIZE, Mf.Pack((uint)v.Width, (uint)v.Height));
        Mf.Set(outType, PInvoke.MF_MT_FRAME_RATE, Mf.Pack((uint)v.Fps, 1));
        Mf.Set(outType, PInvoke.MF_MT_PIXEL_ASPECT_RATIO, Mf.Pack(1, 1));
        Mf.Set(outType, PInvoke.MF_MT_INTERLACE_MODE, (uint)MFVideoInterlaceMode.MFVideoInterlace_Progressive);
        Mf.Set(outType, PInvoke.MF_MT_MPEG2_PROFILE, (uint)eAVEncH264VProfile.eAVEncH264VProfile_Main);
        Mf.Set(outType, PInvoke.MF_MT_MPEG2_LEVEL, (uint)eAVEncH264VLevel.eAVEncH264VLevel4_1);
        SetColour(outType);
        _t.SetOutputType(0, outType, 0);

        var inType = Mf.FindType(i => (_t.GetInputAvailableType(0, i, out var t), t), PInvoke.MFVideoFormat_NV12);
        if (inType is null)
        {
            PInvoke.MFCreateMediaType(out inType).ThrowOnFailure();
            Mf.Set(inType, PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            Mf.Set(inType, PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_NV12);
        }
        Mf.Set(inType, PInvoke.MF_MT_FRAME_SIZE, Mf.Pack((uint)v.Width, (uint)v.Height));
        Mf.Set(inType, PInvoke.MF_MT_FRAME_RATE, Mf.Pack((uint)v.Fps, 1));
        Mf.Set(inType, PInvoke.MF_MT_PIXEL_ASPECT_RATIO, Mf.Pack(1, 1));
        Mf.Set(inType, PInvoke.MF_MT_INTERLACE_MODE, (uint)MFVideoInterlaceMode.MFVideoInterlace_Progressive);
        SetColour(inType);
        _t.SetInputType(0, inType, 0);

        // Some encoders only take rate control once the types are set: try again.
        ConfigureCodec(v, quiet: true);

        _t.GetOutputCurrentType(0, out var current);
        SequenceHeader = Mf.GetBlob(current, PInvoke.MF_MT_MPEG_SEQUENCE_HEADER);

        MFT_OUTPUT_STREAM_INFO si;
        _t.GetOutputStreamInfo(0, &si);
        _streamFlags = si.dwFlags;
        _providesSamples = (si.dwFlags & 0x100) != 0 || (si.dwFlags & 0x200) != 0; // PROVIDES_SAMPLES / CAN_PROVIDE_SAMPLES
        _outputSize = Math.Max(si.cbSize, (uint)(v.Width * v.Height * 3 / 2));

        _t.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
        _t.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);

        if (_events is not null)
        {
            _eventThread = new Thread(EventLoop) { Name = "mft-events", IsBackground = true };
            _eventThread.Start();
        }
        Log.Info($"Encoder: {info.Name} ({(info.Hardware ? "hardware" : "software")}, {(isAsync ? "async" : "sync")}, "
            + $"output stream flags 0x{si.dwFlags:X}, size {si.cbSize})"
            + (Refused.Count > 0 ? $"; refused {string.Join(", ", Refused)}" : ""));
    }

    private static void SetColour(IMFMediaType t)
    {
        Mf.Set(t, PInvoke.MF_MT_VIDEO_PRIMARIES, (uint)MFVideoPrimaries.MFVideoPrimaries_BT709);
        Mf.Set(t, PInvoke.MF_MT_TRANSFER_FUNCTION, (uint)MFVideoTransferFunction.MFVideoTransFunc_709);
        Mf.Set(t, PInvoke.MF_MT_YUV_MATRIX, (uint)MFVideoTransferMatrix.MFVideoTransferMatrix_BT709);
        Mf.Set(t, PInvoke.MF_MT_VIDEO_NOMINAL_RANGE, (uint)MFNominalRange.MFNominalRange_16_235);
    }

    private void ConfigureCodec(VideoSettings v, bool quiet = false)
    {
        if (_codec is null)
        {
            if (!quiet) Refused.Add("ICodecAPI (none)");
            return;
        }
        var gop = (uint)(v.Fps * v.KeyframeSeconds);
        (Guid Key, object Value, string Name)[] settings =
        [
            (typeof(CODECAPI_AVLowLatencyMode).GUID, true, "low latency"),
            (typeof(CODECAPI_AVEncCommonRateControlMode).GUID, (uint)eAVEncCommonRateControlMode.eAVEncCommonRateControlMode_CBR, "CBR"),
            (typeof(CODECAPI_AVEncCommonMeanBitRate).GUID, (uint)v.Bitrate, "mean bit rate"),
            // A quarter second of bits: keyframes stay small enough to send in time. With the
            // half-second buffer Microsoft's encoder made 371 kB keyframes (148ms at 20 Mbit/s).
            (typeof(CODECAPI_AVEncCommonBufferSize).GUID, (uint)(v.Bitrate * v.RateWindow / 2), "buffer size"),
            (typeof(CODECAPI_AVEncMPVGOPSize).GUID, gop, "GOP size"),
            (typeof(CODECAPI_AVEncMPVDefaultBPictureCount).GUID, 0u, "no B-frames"),
            (typeof(CODECAPI_AVEncCommonQualityVsSpeed).GUID, 50u, "quality/speed"),
        ];
        foreach (var (key, value, name) in settings)
        {
            var k = key;
            var hr = _codec.SetValue(&k, value);
            if (hr.Failed && !quiet) Refused.Add($"{name} (0x{(uint)hr.Value:X8})");
        }
    }

    /// <summary>Encodes one NV12 frame. <paramref name="index"/> sets its sample time (index × frame duration).</summary>
    public void Encode(ReadOnlySpan<byte> nv12, long index, bool forceKeyframe)
    {
        if (forceKeyframe && _codec is not null)
        {
            var k = typeof(CODECAPI_AVEncVideoForceKeyFrame).GUID;
            _codec.SetValue(&k, 1u);
        }
        var sample = Mf.Sample(nv12, index * _frameDuration, _frameDuration);
        if (_events is null)
        {
            lock (_gate)
            {
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
            return;
        }
        // Async: hand it to the encoder now if it has asked for input, else queue it.
        // Both this and the event thread decide under _gate, so frames stay in order.
        lock (_gate)
        {
            if (_needInput > 0)
            {
                _needInput--;
                Feed(sample);
            }
            else if (_input.Count < MaxQueued)
            {
                _input.Enqueue(sample);
            }
            else
            {
                Marshal.ReleaseComObject(sample); // encoder is behind: drop rather than build delay
                Log.Debug($"{Info.Name}: input queue full, frame {index} dropped");
            }
        }
    }

    private void Feed(IMFSample s)
    {
        var hr = _t.ProcessInput(0, s, 0);
        Marshal.ReleaseComObject(s);
        if (hr.Failed) Failed?.Invoke($"{Info.Name}: ProcessInput 0x{(uint)hr.Value:X8}");
    }

    /// <summary>Collects every output the encoder has ready.</summary>
    private void Drain()
    {
        while (true)
        {
            var buf = new MFT_OUTPUT_DATA_BUFFER[1];
            buf[0].dwStreamID = 0;
            if (!_providesSamples)
            {
                PInvoke.MFCreateMemoryBuffer(_outputSize, out var mb).ThrowOnFailure();
                PInvoke.MFCreateSample(out var s).ThrowOnFailure();
                s.AddBuffer(mb);
                Marshal.ReleaseComObject(mb);
                buf[0].pSample = s;
            }
            var hr = _t.ProcessOutput(0, 1, buf, out _);
            if (hr == HRESULT.MF_E_TRANSFORM_NEED_MORE_INPUT)
            {
                Release(buf[0]);
                return;
            }
            if (hr == HRESULT.MF_E_TRANSFORM_STREAM_CHANGE)
            {
                // The encoder has settled its output format (Intel's does this on its first
                // output, with the sequence header): confirm it. An async encoder then sends a
                // fresh HaveOutput; asking again now gets E_UNEXPECTED (round 1, test laptop).
                Release(buf[0]);
                var got = Mf.FindType(i => (_t.GetOutputAvailableType(0, i, out var t), t), PInvoke.MFVideoFormat_H264);
                if (got is not null)
                {
                    _t.SetOutputType(0, got, 0);
                    if (Mf.GetBlob(got, PInvoke.MF_MT_MPEG_SEQUENCE_HEADER) is { } sh) SequenceHeaderChanged?.Invoke(sh);
                }
                Log.Debug($"{Info.Name}: output format changed; confirmed");
                if (_events is not null) return;
                continue;
            }
            if (hr.Failed)
            {
                Release(buf[0]);
                Failed?.Invoke($"{Info.Name}: ProcessOutput 0x{(uint)hr.Value:X8} (output #{_outputs + 1}, "
                    + $"stream flags 0x{_streamFlags:X}, {(_providesSamples ? "encoder's samples" : "our samples")})");
                return;
            }
            var sample = buf[0].pSample;
            if (sample is not null)
            {
                sample.GetSampleTime(out var time);
                var key = Mf.GetUInt32(sample, PInvoke.MFSampleExtension_CleanPoint) == 1;
                var data = Mf.Bytes(sample);
                Release(buf[0]);
                _outputs++;
                if (data.Length > 0) Output?.Invoke(data, time, key);
            }
            if (_events is not null) return; // async: one output per HaveOutput event
        }
    }

    private static void Release(MFT_OUTPUT_DATA_BUFFER b)
    {
        if (b.pSample is not null) Marshal.ReleaseComObject(b.pSample);
        if (b.pEvents is not null) Marshal.ReleaseComObject(b.pEvents);
    }

    private const uint METransformNeedInput = 601, METransformHaveOutput = 602, METransformDrainComplete = 603;

    private void EventLoop()
    {
        try
        {
            while (!_stopping)
            {
                var hr = _events!.GetEvent(MEDIA_EVENT_GENERATOR_GET_EVENT_FLAGS.MF_EVENT_FLAG_NONE, out var e);
                if (hr.Failed)
                {
                    if (!_stopping) Failed?.Invoke($"{Info.Name}: GetEvent 0x{(uint)hr.Value:X8}");
                    return;
                }
                e.GetType(out var type);
                Marshal.ReleaseComObject(e);
                switch (type)
                {
                    case METransformNeedInput:
                        lock (_gate)
                        {
                            if (_input.TryDequeue(out var s)) Feed(s);
                            else _needInput++;
                        }
                        break;
                    case METransformHaveOutput:
                        lock (_gate) Drain();
                        break;
                    case METransformDrainComplete:
                        return;
                }
            }
        }
        catch (Exception ex) when (!_stopping)
        {
            Failed?.Invoke($"{Info.Name}: {Captures.Describe(ex)}");
        }
    }

    public void Dispose()
    {
        _stopping = true;
        try
        {
            _t.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
            _t.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_COMMAND_FLUSH, 0);
        }
        catch (COMException) { }
        if (_events is not null)
        {
            try { ((IMFShutdown)_t).Shutdown(); } catch (Exception) { }
        }
        _eventThread?.Join(1000);
        lock (_gate) while (_input.TryDequeue(out var s)) Marshal.ReleaseComObject(s);
        Marshal.ReleaseComObject(_t);
    }
}
