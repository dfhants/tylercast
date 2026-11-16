using System.Net.Sockets;

namespace TylerCast.Core;

public sealed record MirrorOptions
{
    public enum AudioKind
    {
        /// <summary>A silent AC-3 track: the TV gets the format it lists, with no sound.</summary>
        SilentAc3,
        /// <summary>The computer's sound, AAC. The test TV doesn't list AAC with H.264 TS, but plays it
        /// (tried 2026-11-15 with AVC_TS_MP_HD_AAC_MULT5_ISO).</summary>
        Aac,
        /// <summary>The computer's sound, AC-3: exactly the profile the test TV lists. Windows
        /// has an AC-3 (Dolby Digital) encoder; macOS doesn't.</summary>
        Ac3,
        None,
    }

    public int Port { get; init; } = 8090;
    public double MarginPercent { get; init; }
    public VideoSettings Video { get; init; } = new();
    /// <summary>The test TV takes it; keyframes go out in half the time.</summary>
    public int MuxRate { get; init; } = 20_000_000;
    /// <summary>Capture-to-DTS slack. Frames whose last packet leaves later than this are "late".</summary>
    public double Delay { get; init; } = 0.15;
    public AudioKind Audio { get; init; } = AudioKind.Aac;
    public bool TestPattern { get; init; }
    /// <summary>Mute the computer while mirroring, so sound comes from the TV only.</summary>
    public bool SoundOnTvOnly { get; init; } = true;
    public string Mime { get; init; } = Dlna.DefaultMime;
    public bool SendProfile { get; init; } = true;
    public bool Tlag { get; init; }
    public bool SendMetadata { get; init; } = true;

    /// <summary>What is actually sent: the test pattern captures no sound, so it keeps
    /// the silent AC-3 track rather than an empty AAC one.</summary>
    public AudioKind EffectiveAudio => TestPattern && Audio is AudioKind.Aac or AudioKind.Ac3 ? AudioKind.SilentAc3 : Audio;
    public AudioCodec Codec => EffectiveAudio == AudioKind.Aac ? AudioCodec.Aac : AudioCodec.Ac3;
    /// <summary>True when the computer's sound is captured and encoded.</summary>
    public bool CapturesSound => EffectiveAudio is AudioKind.Aac or AudioKind.Ac3;
    public string ContentFeatures => Dlna.ContentFeatures(SendProfile ? Dlna.ProfileFor(Codec) : null);

    public static string Name(AudioKind k) => k switch
    {
        AudioKind.SilentAc3 => "silent-ac3", AudioKind.Aac => "aac", AudioKind.Ac3 => "ac3", _ => "none",
    };

    public static AudioKind? ParseAudio(string s) => s switch
    {
        "silent-ac3" => AudioKind.SilentAc3, "aac" => AudioKind.Aac, "ac3" => AudioKind.Ac3, "none" => AudioKind.None, _ => null,
    };
}

public sealed class PipelineException(string message) : Exception(message);

/// <summary>
/// Source → muxer → pacer → broadcaster (→ HTTP server). Runs without any TV,
/// and before Play, so every connection the TV makes joins it.
/// </summary>
public sealed class Pipeline
{
    public MirrorOptions Options { get; }
    public Broadcaster Broadcaster { get; } = new();
    public Pacer Pacer { get; }
    /// <summary>The stream's path while serving: random per pipeline, so it can't be guessed.</summary>
    public string StreamPath { get; } = StreamServer.RandomPath();
    public StreamServer? Server { get; private set; }
    public Action<string>? OnFailure { get; set; }

    private readonly IVideoSource _video;
    private readonly IAudioSource? _audio;
    private readonly SilentAudioSource _silence = new();
    private int _sawFrame;

    public Pipeline(MirrorOptions options, IVideoSource video, IAudioSource? audio = null)
    {
        Options = options;
        _video = video;
        _audio = audio;
        var mc = new TsMuxer.Config
        {
            MuxRate = options.MuxRate,
            IncludeAudio = options.EffectiveAudio != MirrorOptions.AudioKind.None,
            AudioCodec = options.Codec,
        };
        Pacer = new Pacer(new TsMuxer(mc), Broadcaster, options.Delay);
    }

    /// <summary>
    /// <paramref name="allowedPeer"/>: only that address may fetch the stream (the TV).
    /// <paramref name="bindHost"/>: listen on that local address only.
    /// </summary>
    public async Task StartAsync(bool serve, string? allowedPeer = null, string? bindHost = null, CancellationToken ct = default)
    {
        if (Options.CapturesSound && _audio is null)
            throw new PipelineException("capturing the computer's sound isn't available here: use --audio silent-ac3 or --test-pattern");
        if (Options.CapturesSound && _audio!.Codec != Options.Codec)
            throw new PipelineException($"the sound source makes {_audio.Codec}, not {Options.Codec}");
        if (serve)
        {
            var h = new StreamHeaders { ContentType = Options.Mime, ContentFeatures = Options.ContentFeatures, Tlag = Options.Tlag };
            var s = new StreamServer(Broadcaster, h, Options.Port, StreamPath, allowedPeer: allowedPeer, bindHost: bindHost)
            {
                OnJoin = _video.RequestKeyframe,
            };
            try { s.Start(); }
            catch (SocketException e)
            {
                throw new PipelineException($"cannot listen on port {Options.Port}: {e.SocketErrorCode} (another copy running?)");
            }
            Server = s;
        }

        if (Options.EffectiveAudio == MirrorOptions.AudioKind.SilentAc3)
        {
            Pacer.OnTick = now => _silence.Tick(now, Pacer);
            Log.Info("Audio: silent AC-3 track" + (Options.TestPattern && Options.Audio == MirrorOptions.AudioKind.Aac
                ? " (test pattern: no sound to capture)" : ""));
        }
        else if (Options.CapturesSound)
        {
            Log.Info($"Audio: {_audio!.Name} (profile {Dlna.ProfileFor(Options.Codec)})");
        }
        else
        {
            Log.Info("Audio: none (video-only TS)");
        }

        Log.Info($"Source: {_video.Name}");
        // The pacer's clock first: PTS are stream times measured from its start.
        Pacer.Start();
        _video.Start((au, host, key) =>
        {
            Pacer.WithMuxer(m => m.EnqueueVideo(au, Pacer.Pts90ForHost(host), key));
            Interlocked.Exchange(ref _sawFrame, 1);
        }, why => OnFailure?.Invoke(why));
        _audio?.Start((frame, host) => Pacer.WithMuxer(m => m.EnqueueAudio(frame, Pacer.Pts90ForHost(host))),
            why => OnFailure?.Invoke($"sound: {why}"));
        for (var i = 0; i < 40 && Volatile.Read(ref _sawFrame) == 0; i++) await Task.Delay(100, ct).ConfigureAwait(false);
        if (Volatile.Read(ref _sawFrame) == 0)
        {
            Stop();
            throw new PipelineException(Options.TestPattern ? "the encoder produced nothing in 4s" : "no picture from the screen in 4s");
        }
        Log.Info("Encoder running; stream is live");
    }

    public void Stop()
    {
        _audio?.Stop();
        _video.Stop();
        Pacer.Stop();
        Server?.Stop();
        Server = null;
    }
}

/// <summary>
/// One mirroring session to one TV: pipeline up first, then SOAP Stop,
/// SetAVTransportURI, Play. Stop on the way out.
/// </summary>
public sealed class MirrorSession(MirrorOptions options, Func<MirrorOptions, IVideoSource> videoFactory,
    Func<MirrorOptions, IAudioSource?>? audioFactory = null)
{
    public abstract record State
    {
        public sealed record Stopped : State;
        public sealed record Connecting(string What) : State;
        public sealed record Playing(string Tv) : State;
        public sealed record Failed(string Why) : State;

        public string Text => this switch
        {
            Connecting c => $"Connecting: {c.What}",
            Playing p => $"Playing on {p.Tv}",
            Failed f => $"Error: {f.Why}",
            _ => "Stopped",
        };
    }

    /// <summary>State checks in a row the TV may miss before the session ends.</summary>
    public const int MaxMissedPolls = 3;
    public static TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    public MirrorOptions Options { get; set; } = options;
    public event Action<State>? StateChanged;
    /// <summary>Called with true while mirroring (keep the display awake), false after.</summary>
    public Action<bool>? KeepAwake { get; set; }
    public Pipeline? Pipeline => _pipeline;

    private State _state = new State.Stopped();
    private Pipeline? _pipeline;
    private AVTransport? _transport;
    private CancellationTokenSource? _poll;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public State Current => _state;
    public bool IsActive => _state is State.Connecting or State.Playing;

    private void Set(State s)
    {
        var was = IsActive;
        _state = s;
        if (IsActive != was) KeepAwake?.Invoke(IsActive);
        StateChanged?.Invoke(s);
    }

    public async Task StartAsync(Renderer tv, CancellationToken ct = default)
    {
        if (IsActive) return;
        if (tv.AVTransport is not { } avt)
        {
            Set(new State.Failed($"{tv.Name} can't play streams (no AVTransport)"));
            return;
        }
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Set(new State.Connecting($"finding a route to {tv.Name}"));
            // Right after wake the network may still be rejoining: give it a few seconds.
            var host = Ssdp.LocalAddressTowards(tv.Ip);
            for (var i = 0; i < 10 && host is null; i++)
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
                host = Ssdp.LocalAddressTowards(tv.Ip);
            }
            if (host is null)
            {
                Log.Error($"No route to {tv.Ip}");
                throw new PipelineException("Can't reach the TV");
            }

            Set(new State.Connecting("starting capture and encoder"));
            var p = new Pipeline(Options, videoFactory(Options), Options.CapturesSound ? audioFactory?.Invoke(Options) : null)
            {
                OnFailure = why => _ = FailAsync(why),
            };
            _pipeline = p;
            await p.StartAsync(serve: true, allowedPeer: tv.Ip, bindHost: host, ct).ConfigureAwait(false);

            var url = $"http://{host}:{Options.Port}{p.StreamPath}";
            var protocolInfo = Dlna.ProtocolInfo(Options.ContentFeatures, Options.Mime);
            Log.Info($"Serving {url}  (Content-Type {Options.Mime})");
            Log.Info($"protocolInfo {protocolInfo}");

            Set(new State.Connecting($"asking {tv.Name} to play"));
            var t = new AVTransport(avt);
            _transport = t;
            try { await t.StopAsync(ct).ConfigureAwait(false); }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Log.Info($"Stop before play failed (nothing playing is fine): {e.Message}");
            }
            var meta = Options.SendMetadata ? Soap.Didl(url, protocolInfo) : "";
            await t.SetUriAsync(url, meta, ct).ConfigureAwait(false);
            await t.PlayAsync(ct).ConfigureAwait(false);
            Set(new State.Playing(tv.Name));
            Log.Info($"Playing on {tv.Name}");
            Poll(t);
        }
        catch (Exception e)
        {
            Log.Error($"Start failed: {e.Message}");
            await TeardownAsync(sendStop: _transport is not null).ConfigureAwait(false);
            Set(new State.Failed(Explain(e, tv.Name)));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>One plain sentence for the tray; the log keeps the raw error.</summary>
    public static string Explain(Exception e, string tv) => e switch
    {
        // The TV couldn't open our stream URL: usually a guest network or client
        // isolation between this computer and the TV, or the firewall.
        SoapException { UpnpCode: "716" } => "The TV can't reach this computer",
        HttpRequestException or TaskCanceledException or TimeoutException => "The TV didn't answer",
        _ when e.Message.Length > 0 => char.ToUpperInvariant(e.Message[0]) + e.Message[1..],
        _ => e.GetType().Name,
    };

    public async Task StopAsync()
    {
        if (_pipeline is null && _transport is null)
        {
            Set(new State.Stopped());
            return;
        }
        Log.Info("Stopping");
        await TeardownAsync(sendStop: true).ConfigureAwait(false);
        Set(new State.Stopped());
    }

    private async Task FailAsync(string why)
    {
        if (!IsActive) return;
        Log.Error($"Stopping: {why}");
        await TeardownAsync(sendStop: true).ConfigureAwait(false);
        Set(new State.Failed(why));
    }

    private async Task TeardownAsync(bool sendStop)
    {
        _poll?.Cancel();
        _poll = null;
        if (sendStop && _transport is { } t)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await t.StopAsync(cts.Token).ConfigureAwait(false);
            }
            catch (Exception e) { Log.Info($"Stop on the way out failed: {e.Message}"); }
        }
        _transport = null;
        _pipeline?.Stop();
        _pipeline = null;
    }

    /// <summary>
    /// Logs the TV's transport state every 5s. If it stops after playing (remote
    /// control, input change) the session ends and says so. A TV in standby or off
    /// the network never answers again: after <see cref="MaxMissedPolls"/> missed checks
    /// the session stops rather than keep serving the screen to no one.
    /// </summary>
    private void Poll(AVTransport t)
    {
        var cts = new CancellationTokenSource();
        _poll = cts;
        _ = Task.Run(async () =>
        {
            string? last = null;
            var played = false;
            var misses = 0;
            while (!cts.IsCancellationRequested)
            {
                try { await Task.Delay(PollInterval, cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                string? s;
                try
                {
                    s = await t.TransportStateAsync(cts.Token).ConfigureAwait(false);
                    misses = 0;
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { return; }
                catch (Exception e)
                {
                    misses++;
                    Log.Info($"TV didn't answer a state check ({misses} of {MaxMissedPolls}): {e.Message}");
                    if (misses >= MaxMissedPolls)
                    {
                        await FailAsync("The TV stopped answering").ConfigureAwait(false);
                        return;
                    }
                    continue;
                }
                if (s is null) continue;
                if (s != last)
                {
                    Log.Info($"TV transport state: {s}");
                    last = s;
                }
                if (s == "PLAYING") played = true;
                if (played && s is "STOPPED" or "NO_MEDIA_PRESENT")
                {
                    Log.Info($"TV stopped playing (state {s})");
                    await FailAsync("The TV stopped the stream").ConfigureAwait(false);
                    return;
                }
            }
        });
    }
}
