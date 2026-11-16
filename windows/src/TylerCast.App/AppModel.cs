using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Win32;
using TylerCast.Core;
using TylerCast.Windows;

namespace TylerCast.App;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    /// <summary>Every property may have changed.</summary>
    protected void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

/// <summary>What the app remembers between runs, in %LOCALAPPDATA%\TylerCast\settings.json.</summary>
public sealed class Settings
{
    public double Margin { get; set; }
    public bool SoundOnTv { get; set; } = true;
    public bool MuteThisPc { get; set; } = true;
    public bool TestPattern { get; set; }
    /// <summary>The last TV cast to: listed first and opened ready to cast.</summary>
    public string? LastTv { get; set; }
    public bool FirstRunDone { get; set; }
    /// <summary>Until the first cast works, connecting explains Windows' firewall question.</summary>
    public bool FirstCastDone { get; set; }

    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TylerCast", "settings.json");

    public static Settings Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch (Exception e)
        {
            Log.Warn($"Settings unreadable, starting afresh: {e.Message}");
            return new();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { Log.Warn($"Couldn't save settings: {e.Message}"); }
    }
}

/// <summary>"Start with Windows": the per-user Run key, so no admin rights are needed.</summary>
public static class Startup
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "TylerCast";

    public static bool IsOn
    {
        get
        {
            try { return Registry.CurrentUser.OpenSubKey(Key)?.GetValue(Name) is string; }
            catch { return false; }
        }
    }

    public static void Set(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(Key);
        if (on) k.SetValue(Name, $"\"{Environment.ProcessPath}\" --background");
        else k.DeleteValue(Name, throwOnMissingValue: false);
        Log.Info(on ? "Start with Windows: on" : "Start with Windows: off");
    }
}

public enum TvStatus { Idle, Connecting, Casting, Failed }

/// <summary>One TV in the list, like a network in Windows' Wi-Fi list.</summary>
public sealed class TvItem(AppModel model, Renderer tv) : Observable
{
    public AppModel Model => model;
    public Renderer Tv { get; internal set; } = tv;
    public string Location => Tv.Location;
    /// <summary>"Living_Room_TV" reads better as "Living Room TV".</summary>
    public string Name => Tv.Name.Replace('_', ' ');

    /// <summary>"Example Vision", not "Example Example Vision" (the TV repeats its maker in the model).</summary>
    public string Maker
    {
        get
        {
            var words = Tv.Model.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return words.Length > 1 && words[0].Equals(words[1], StringComparison.OrdinalIgnoreCase)
                ? string.Join(' ', words.Skip(1)) : Tv.Model;
        }
    }

    public TvStatus Status { get; private set; }
    public string Subtitle { get; private set; } = "";
    public bool IsExpanded { get; private set; }
    public bool ShowFirewallNote { get; private set; }

    public bool IsActive => Status is TvStatus.Connecting or TvStatus.Casting;
    public bool IsFailed => Status == TvStatus.Failed;
    public bool ShowCastOptions => IsExpanded && !IsActive;
    public bool ShowCastingOptions => IsExpanded && Status == TvStatus.Casting;
    public bool ShowConnectingOptions => IsExpanded && Status == TvStatus.Connecting;
    public string CastLabel => IsFailed ? "Try again" : "Cast";

    internal void Update(TvStatus status, string subtitle, bool expanded, bool firewallNote)
    {
        if (Status == status && Subtitle == subtitle && IsExpanded == expanded && ShowFirewallNote == firewallNote) return;
        (Status, Subtitle, IsExpanded, ShowFirewallNote) = (status, subtitle, expanded, firewallNote);
        RaiseAll();
    }

    internal void Refresh() => RaiseAll();
}

/// <summary>
/// Everything the flyout shows and does: the TVs on this network, the session, the
/// settings. Lives on the UI thread; the session reports from its own threads.
/// </summary>
public sealed class AppModel : Observable
{
    private readonly Dispatcher _ui;
    private readonly Settings _s;
    private readonly MirrorSession? _session; // null in screenshot previews
    private readonly DispatcherTimer _clock;
    private MirrorSession.State _previewState = new MirrorSession.State.Stopped();
    private string? _activeLocation, _errorLocation, _expanded;
    private DateTime? _startedAt;
    private DateTime _lastSearch = DateTime.MinValue;
    private bool _searching;
    private string? _warning;

    public ObservableCollection<TvItem> Tvs { get; } = [];
    public Settings Settings => _s;

    /// <summary>A failure while the flyout may be closed (the tray shows a notification).</summary>
    public event Action<string>? Failed;
    /// <summary>The tray icon and its tooltip should be redrawn.</summary>
    public event Action? StatusChanged;

    public AppModel(Dispatcher ui, bool preview = false)
    {
        _ui = ui;
        _s = preview ? new Settings() : Settings.Load();
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) => { if (IsCasting) Refresh(); }, ui);
        _clock.Stop();
        Tvs.CollectionChanged += (_, _) => Raise(nameof(NoTvs));
        if (preview) return;
        _session = new MirrorSession(new MirrorOptions(), VideoFor, AudioFor) { KeepAwake = Platform.KeepAwake };
        _session.StateChanged += s => _ui.BeginInvoke(() => OnState(s));
        // Sleep ends casting cleanly (the TV gets Stop); nothing restarts on wake.
        SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == PowerModes.Suspend && IsActive)
            {
                Log.Info("The PC is going to sleep: stopping casting");
                Disconnect();
            }
        };
    }

    private static IVideoSource VideoFor(MirrorOptions o) => o.TestPattern ? new ClipVideoSource() : new WindowsVideoSource(o);
    private static IAudioSource? AudioFor(MirrorOptions o) => o.CapturesSound ? new WindowsAudioSource(o.Codec, o.SoundOnTvOnly) : null;

    private MirrorSession.State State => _session?.Current ?? _previewState;
    public bool IsActive => State is MirrorSession.State.Connecting or MirrorSession.State.Playing;
    public bool IsCasting => State is MirrorSession.State.Playing;
    public bool HasError => _errorLocation is not null;

    // ---- list

    public bool Searching
    {
        get => _searching;
        private set { _searching = value; Raise(); Raise(nameof(EmptyTitle)); }
    }

    public bool NoTvs => Tvs.Count == 0;
    public string EmptyTitle => Searching ? "Looking for TVs" : "No TVs found";
    public string EmptyHint => "Switch the TV on, and check it's on the same Wi-Fi as this PC.";

    public string? Warning
    {
        get => _warning;
        private set { _warning = value; Raise(); Raise(nameof(HasWarning)); }
    }

    public bool HasWarning => Warning is not null;

    // ---- settings (sound applies at the next cast; margin applies live)

    public double Margin
    {
        get => _s.Margin;
        set
        {
            var v = Math.Clamp(Math.Round(value), 0, 10);
            if (v == _s.Margin) return;
            _s.Margin = v;
            _s.Save();
            _session?.SetMargin(v);
            Raise();
            Raise(nameof(MarginText));
        }
    }

    public string MarginText => $"{Margin:0}%";

    public bool SoundOnTv
    {
        get => _s.SoundOnTv;
        set { _s.SoundOnTv = value; _s.Save(); Raise(); Raise(nameof(SoundSummary)); }
    }

    public bool MuteThisPc
    {
        get => _s.MuteThisPc;
        set { _s.MuteThisPc = value; _s.Save(); Raise(); Raise(nameof(SoundSummary)); }
    }

    public bool TestPattern
    {
        get => _s.TestPattern;
        set { _s.TestPattern = value; _s.Save(); Raise(); }
    }

    public bool StartWithWindows
    {
        get => Startup.IsOn;
        set
        {
            try { Startup.Set(value); }
            catch (Exception e) { Log.Error($"Couldn't change Start with Windows: {e.Message}"); }
            Raise();
        }
    }

    public bool CanChangeTestPattern => !IsActive;

    /// <summary>What the sound is doing while casting (the choice is made before casting starts).</summary>
    public string SoundSummary => !SoundOnTv ? "No sound on the TV" : MuteThisPc ? "Sound on the TV only" : "Sound on the TV and this PC";

    public string TrayText => State switch
    {
        MirrorSession.State.Playing => $"TylerCast: casting to {ActiveName} · {Elapsed}",
        MirrorSession.State.Connecting => $"TylerCast: connecting to {ActiveName}",
        _ when HasError => "TylerCast: casting stopped",
        _ => "TylerCast",
    };

    private string ActiveName => Tvs.FirstOrDefault(t => t.Location == _activeLocation)?.Name ?? "the TV";

    private string Elapsed
    {
        get
        {
            var s = _startedAt is { } t ? (int)Math.Max(0, (DateTime.Now - t).TotalSeconds) : 0;
            return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
        }
    }

    // ---- actions

    /// <summary>A click on a TV's row opens it (one at a time, as in the Wi-Fi list).</summary>
    public void Toggle(TvItem tv)
    {
        _expanded = _expanded == tv.Location && !tv.IsActive ? null : tv.Location;
        Refresh();
    }

    public void Cast(TvItem tv)
    {
        if (_session is null) return;
        _s.LastTv = tv.Location;
        _s.Save();
        _errorLocation = null;
        var wasActive = _session.IsActive;
        _activeLocation = tv.Location;
        _expanded = tv.Location;
        Refresh();
        var renderer = tv.Tv;
        _ = Task.Run(async () =>
        {
            try
            {
                if (wasActive) await _session.StopAsync();
                _ui.Invoke(() => _activeLocation = renderer.Location);
                _session.Options = Options();
                await _session.StartAsync(renderer);
            }
            catch (Exception e)
            {
                Log.Error($"Cast failed: {e}");
                _ = _ui.BeginInvoke(() => OnState(new MirrorSession.State.Failed(MirrorSession.Explain(e, renderer.Name))));
            }
        });
    }

    private MirrorOptions Options()
    {
        var audio = _s.TestPattern ? MirrorOptions.AudioKind.SilentAc3
            : _s.SoundOnTv ? Platform.Resolve(MirrorOptions.AudioKind.Aac) : MirrorOptions.AudioKind.SilentAc3;
        return new MirrorOptions
        {
            MarginPercent = _s.Margin,
            Audio = audio,
            Delay = Platform.DelayFor(audio),
            SoundOnTvOnly = _s.MuteThisPc,
            TestPattern = _s.TestPattern,
        };
    }

    public void Disconnect()
    {
        if (_session is null) return;
        _ = Task.Run(_session.StopAsync);
    }

    public Task ShutdownAsync() => _session?.StopAsync() ?? Task.CompletedTask;

    private void OnState(MirrorSession.State s)
    {
        switch (s)
        {
            case MirrorSession.State.Playing:
                _startedAt ??= DateTime.Now;
                if (!_s.FirstCastDone)
                {
                    _s.FirstCastDone = true;
                    _s.Save();
                }
                break;
            case MirrorSession.State.Failed f:
                _errorLocation = _activeLocation;
                _activeLocation = null;
                _startedAt = null;
                Failed?.Invoke(f.Why);
                break;
            case MirrorSession.State.Stopped:
                _activeLocation = null;
                _startedAt = null;
                break;
        }
        if (IsCasting) _clock.Start();
        else _clock.Stop();
        Refresh();
    }

    /// <summary>The flyout opened: look again if the list is old, and check the network.</summary>
    public void PanelOpened()
    {
        if (DateTime.Now - _lastSearch > TimeSpan.FromSeconds(30)) Search();
        _ = CheckNetworkAsync();
        if (_expanded is null && !IsActive) _expanded = Tvs.FirstOrDefault()?.Location;
        Refresh();
    }

    /// <summary>The flyout closed: an error has been seen, so its row goes back to normal.</summary>
    public void PanelClosed()
    {
        if (_errorLocation is null) return;
        _errorLocation = null;
        Refresh();
    }

    public async void Search()
    {
        if (Searching || _session is null) return;
        Searching = true;
        _lastSearch = DateTime.Now;
        try
        {
            // Each TV appears as soon as it answers; the list settles when the search ends.
            var found = await Discovery.RenderersAsync(onFound: r =>
            {
                _ = _ui.BeginInvoke(() => Add(r));
                return Task.CompletedTask;
            });
            foreach (var gone in Tvs.Where(t => found.All(f => f.Location != t.Location) && t.Location != _activeLocation).ToList())
                Tvs.Remove(gone);
            foreach (var r in found) Add(r);
            Log.Info($"Found {found.Count} TV(s)");
        }
        catch (Exception e) { Log.Warn($"Searching for TVs failed: {e.Message}"); }
        finally
        {
            Searching = false;
            Refresh();
        }
    }

    private void Add(Renderer r)
    {
        if (Tvs.FirstOrDefault(t => t.Location == r.Location) is { } same)
        {
            same.Tv = r;
            same.Refresh();
            return;
        }
        var item = new TvItem(this, r);
        var i = 0;
        while (i < Tvs.Count && Before(Tvs[i], item)) i++;
        Tvs.Insert(i, item);
        if (_expanded is null && !IsActive && Tvs.Count == 1) _expanded = item.Location;
        Refresh();
    }

    /// <summary>The last TV cast to comes first, then by name.</summary>
    private bool Before(TvItem a, TvItem b)
    {
        if ((a.Location == _s.LastTv) != (b.Location == _s.LastTv)) return a.Location == _s.LastTv;
        return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase) <= 0;
    }

    /// <summary>
    /// A network set to Public blocks the TV from fetching the stream (Windows' firewall
    /// refuses incoming connections there): round 1 met it on the test laptop.
    /// </summary>
    private async Task CheckNetworkAsync()
    {
        var names = await Task.Run(PublicNetworks);
        Warning = names.Count == 0 ? null
            : $"{names[0]} is set to a public network, so TVs can't connect. Change it to Private.";
    }

    private static List<string> PublicNetworks()
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -Command \"Get-NetConnectionProfile | Where-Object NetworkCategory -eq 'Public' | ForEach-Object { $_.Name }\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10_000);
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }
        catch (Exception e)
        {
            Log.Warn($"Couldn't check the network profile: {e.Message}");
            return [];
        }
    }

    public static void FixWarning() => Open("ms-settings:network-status");

    public static void OpenLog() => Open(Log.FilePath);

    private static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception e) { Log.Warn($"Couldn't open {target}: {e.Message}"); }
    }

    /// <summary>Recomputes every row from the session's state.</summary>
    private void Refresh()
    {
        var state = State;
        foreach (var tv in Tvs)
        {
            var active = tv.Location == _activeLocation;
            TvStatus status;
            string sub;
            if (active && state is MirrorSession.State.Playing)
            {
                status = TvStatus.Casting;
                sub = (_s.TestPattern ? "Casting the test pattern · " : "Casting · ") + Elapsed;
            }
            else if (active && state is MirrorSession.State.Connecting)
            {
                status = TvStatus.Connecting;
                sub = "Connecting…";
            }
            else if (tv.Location == _errorLocation && state is MirrorSession.State.Failed f)
            {
                status = TvStatus.Failed;
                sub = f.Why;
            }
            else
            {
                status = TvStatus.Idle;
                sub = tv.Maker;
            }
            var expanded = active || tv.Location == _expanded;
            tv.Update(status, sub, expanded, firewallNote: status == TvStatus.Connecting && !_s.FirstCastDone);
        }
        Raise(nameof(IsActive));
        Raise(nameof(CanChangeTestPattern));
        Raise(nameof(TrayText));
        StatusChanged?.Invoke();
    }

    // ---- previews for --screenshot

    internal static AppModel Preview(Dispatcher ui, MirrorSession.State state, int tvs, bool warning = false,
        bool searching = false, bool firstCast = false, double margin = 3)
    {
        var m = new AppModel(ui, preview: true) { _previewState = state };
        m._s.Margin = margin;
        m._s.FirstCastDone = !firstCast;
        var all = new[]
        {
            new Renderer("http://192.0.2.175:55000/dmr/ddd.xml", "192.0.2.175", "Living_Room_TV", "Example Example Vision", new Dictionary<string, Uri>()),
            new Renderer("http://192.0.2.80:9197/dmr", "192.0.2.80", "Bedroom TV", "Samsung Electronics UE43AU7100", new Dictionary<string, Uri>()),
        };
        foreach (var r in all.Take(tvs)) m.Tvs.Add(new TvItem(m, r));
        if (tvs > 0)
        {
            var first = all[0].Location;
            if (state is MirrorSession.State.Failed) m._errorLocation = first;
            else if (state is not MirrorSession.State.Stopped) m._activeLocation = first;
            m._expanded = first;
        }
        if (state is MirrorSession.State.Playing) m._startedAt = DateTime.Now - TimeSpan.FromSeconds(727);
        m._searching = searching;
        if (warning) m._warning = "HomeWiFi is set to a public network, so TVs can't connect. Change it to Private.";
        m.Refresh();
        return m;
    }
}
