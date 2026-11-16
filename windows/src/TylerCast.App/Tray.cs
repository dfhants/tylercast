using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using TylerCast.Core;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace TylerCast.App;

internal static partial class Native
{
    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(IntPtr icon);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2;

    /// <summary>Windows 11's rounded corners (and dark frame in dark mode); ignored on Windows 10.</summary>
    internal static void Round(Window w, bool dark)
    {
        var h = new WindowInteropHelper(w).Handle;
        var round = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(h, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        var d = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(h, DWMWA_USE_IMMERSIVE_DARK_MODE, ref d, sizeof(int));
    }
}

/// <summary>The user's Windows colours: light or dark apps and taskbar, and the accent.</summary>
internal static class WinTheme
{
    private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool AppsLight => Read(Personalize, "AppsUseLightTheme", 1) != 0;
    public static bool TaskbarLight => Read(Personalize, "SystemUsesLightTheme", 0) != 0;

    /// <summary>The accent as Windows uses it on the taskbar's theme: lighter on dark, darker on light.</summary>
    public static Drawing.Color Accent(bool onLight)
    {
        try
        {
            if (Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent")
                    ?.GetValue("AccentPalette") is byte[] { Length: >= 32 } p)
            {
                var i = (onLight ? 4 : 1) * 4; // Dark1 on light, Light2 on dark
                return Drawing.Color.FromArgb(p[i], p[i + 1], p[i + 2]);
            }
        }
        catch { }
        return onLight ? Drawing.Color.FromArgb(0x00, 0x5A, 0x9E) : Drawing.Color.FromArgb(0x60, 0xCD, 0xFF);
    }

    private static int Read(string key, string name, int fallback)
    {
        try { return Registry.CurrentUser.OpenSubKey(key)?.GetValue(name) is int v ? v : fallback; }
        catch { return fallback; }
    }
}

/// <summary>The flyout: borderless, above the taskbar's tray corner, closes when it loses focus.</summary>
internal sealed class FlyoutWindow : Window
{
    public PanelView Panel { get; }
    public DateTime HiddenAt { get; private set; }

    public FlyoutWindow(AppModel model)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Left = -10_000;
        Top = -10_000;
        Title = "TylerCast";
        Panel = new PanelView { DataContext = model };
        Content = Panel;
        SourceInitialized += (_, _) => Native.Round(this, !WinTheme.AppsLight);
        Deactivated += (_, _) => HideFlyout();
        SizeChanged += (_, _) => { if (IsVisible) Place(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) HideFlyout(); };
    }

    public void ShowFlyout()
    {
        Show();
        UpdateLayout();
        Place();
        Activate();
        Native.SetForegroundWindow(new WindowInteropHelper(this).Handle);
        Panel.Focus();
    }

    public void HideFlyout()
    {
        if (!IsVisible) return;
        HiddenAt = DateTime.Now;
        Hide();
    }

    /// <summary>
    /// 12px in from the corner where the tray is: bottom right usually, but the taskbar can be at
    /// the top (the test laptop's is) or a side. Placed in physical pixels on the monitor
    /// under the pointer, since the tray icon was just clicked.
    /// </summary>
    private void Place()
    {
        var screen = Forms.Screen.FromPoint(Forms.Cursor.Position);
        var (wa, b) = (screen.WorkingArea, screen.Bounds);
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var w = (int)Math.Ceiling(ActualWidth * scale);
        var h = (int)Math.Ceiling(ActualHeight * scale);
        var m = (int)(12 * scale);
        var top = wa.Top > b.Top;
        var left = !top && wa.Left > b.Left;
        var x = left ? wa.Left + m : wa.Right - m - w;
        var y = top ? wa.Top + m : wa.Bottom - m - h;
        const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
        Native.SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }
}

/// <summary>
/// The tray icon and everything hanging off it: left-click toggles the flyout, right-click
/// gives Stop casting / Settings / Open log / Exit, and a notification says when casting
/// stopped by itself while the flyout was closed.
/// </summary>
internal sealed class TrayHost
{
    private readonly AppModel _model;
    private readonly FlyoutWindow _flyout;
    private readonly Forms.NotifyIcon _icon;
    private readonly Window _menuOwner;
    private SettingsWindow? _settings;
    private IntPtr _hicon;
    /// <summary>Set once Exit or sign-out starts: the icon is going away, so nothing redraws it.</summary>
    private bool _exiting;

    public TrayHost(bool background)
    {
        _model = new AppModel(Dispatcher.CurrentDispatcher);
        _flyout = new FlyoutWindow(_model);
        _flyout.IsVisibleChanged += (_, _) =>
        {
            if (_flyout.IsVisible) _model.PanelOpened();
            else _model.PanelClosed();
        };
        _flyout.Panel.SettingsRequested += ShowSettings;

        // A context menu needs a window of ours in front, or it won't close on a click elsewhere.
        _menuOwner = new Window
        {
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, Width = 1, Height = 1,
            Left = -10_000, Top = -10_000, ResizeMode = ResizeMode.NoResize,
        };

        _icon = new Forms.NotifyIcon { Visible = true };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) ToggleFlyout();
            else if (e.Button == Forms.MouseButtons.Right) ShowMenu();
        };
        _icon.BalloonTipClicked += (_, _) => _flyout.ShowFlyout();
        _model.StatusChanged += UpdateIcon;
        _model.Failed += why =>
        {
            if (!_flyout.IsVisible) _icon.ShowBalloonTip(8000, "Casting stopped", why, Forms.ToolTipIcon.Warning);
        };
        SystemEvents.UserPreferenceChanged += (_, _) => Application.Current.Dispatcher.BeginInvoke(UpdateIcon);
        UpdateIcon();

        _model.Search();
        if (!_model.Settings.FirstRunDone)
        {
            // Windows 11 hides new tray icons behind ^, so say where TylerCast lives.
            _model.Settings.FirstRunDone = true;
            _model.Settings.Save();
            _flyout.ShowFlyout();
            _icon.ShowBalloonTip(15000, "TylerCast is in the tray",
                "Find it under ^ next to the clock. To keep it in view, drag it onto the taskbar.", Forms.ToolTipIcon.Info);
        }
        else if (!background)
        {
            _flyout.ShowFlyout();
        }
    }

    /// <summary>Opening TylerCast again (Start menu) shows the flyout of the copy already running.</summary>
    public void ShowFlyout() => _flyout.ShowFlyout();

    private void ToggleFlyout()
    {
        // Clicking the icon while the flyout is open first takes its focus (which hides it):
        // that click means close, not open again.
        if (_flyout.IsVisible || DateTime.Now - _flyout.HiddenAt < TimeSpan.FromMilliseconds(300)) _flyout.HideFlyout();
        else _flyout.ShowFlyout();
    }

    private void ShowMenu()
    {
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        void Add(string text, string glyph, Action act, bool enabled = true)
        {
            var item = new MenuItem
            {
                Header = text, IsEnabled = enabled,
                Icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets") },
            };
            item.Click += (_, _) => act();
            menu.Items.Add(item);
        }
        Add("Stop casting", "", _model.Disconnect, _model.IsActive);
        Add("Settings", "", ShowSettings);
        Add("Open log", "", AppModel.OpenLog);
        menu.Items.Add(new Separator());
        Add("Exit", "", Exit);
        _menuOwner.Show();
        _menuOwner.Activate();
        Native.SetForegroundWindow(new WindowInteropHelper(_menuOwner).Handle);
        menu.Closed += (_, _) => _menuOwner.Hide();
        menu.IsOpen = true;
    }

    private void ShowSettings()
    {
        _flyout.HideFlyout();
        if (_settings is { IsLoaded: true })
        {
            _settings.Activate();
            return;
        }
        _settings = new SettingsWindow(_model);
        _settings.SourceInitialized += (_, _) => Native.Round(_settings, !WinTheme.AppsLight);
        _settings.Show();
        _settings.Activate();
    }

    private async void Exit()
    {
        Log.Info("Exit");
        _exiting = true;
        _icon.Visible = false;
        try { await _model.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(4)); }
        catch (Exception e) { Log.Warn($"Stopping on exit: {e.Message}"); }
        _icon.Dispose();
        Application.Current.Shutdown();
    }

    /// <summary>Stops casting when Windows signs out or shuts down, so the TV gets Stop.</summary>
    public void SessionEnding()
    {
        _exiting = true;
        try { _model.ShutdownAsync().Wait(TimeSpan.FromSeconds(3)); }
        catch { }
        _icon.Visible = false;
    }

    /// <summary>
    /// The Mac menu bar's glyph (a screen on a stand with a T) in the taskbar's text colour,
    /// filled in the accent colour while casting, with "!" after a failure. Drawn at the tray's
    /// icon size, so it stays sharp at any scaling.
    /// </summary>
    private void UpdateIcon()
    {
        // The session's "stopped" arrives after Exit has removed the icon (round 4).
        if (_exiting) return;
        var light = WinTheme.TaskbarLight;
        var glyph = _model.HasError ? TrayGlyph.Error : _model.IsCasting ? TrayGlyph.Casting
            : _model.IsActive ? TrayGlyph.Connecting : TrayGlyph.Idle;
        var colour = glyph == TrayGlyph.Casting ? WinTheme.Accent(light) : light ? Drawing.Color.Black : Drawing.Color.White;
        using var bmp = TrayGlyphs.Draw(glyph, colour, Forms.SystemInformation.SmallIconSize.Width);
        var old = _hicon;
        _hicon = bmp.GetHicon();
        _icon.Icon = Drawing.Icon.FromHandle(_hicon);
        if (old != IntPtr.Zero) Native.DestroyIcon(old);
        var text = _model.TrayText;
        _icon.Text = text.Length > 120 ? text[..120] : text;
    }
}

internal enum TrayGlyph { Idle, Connecting, Casting, Error }

/// <summary>
/// The tray glyph: the Mac menu bar's screen-on-a-stand with a T (Sources/TylerCast/Icon.swift),
/// redrawn for each icon size with its straight edges on whole pixels, so the T stays solid and
/// sharp at 16-32 px (drawing the Mac's shapes scaled gave a notched, jagged T: round 5).
/// Rasterised here with 4×4 supersampling, so only the rounded corners are soft.
/// </summary>
internal static class TrayGlyphs
{
    private const int SS = 4;

    private delegate bool Shape(double x, double y);

    public static Drawing.Bitmap Draw(TrayGlyph g, Drawing.Color colour, int size)
    {
        var k = size / 18.0;
        int P(double v) => (int)Math.Round(v * k, MidpointRounding.AwayFromZero);
        // A width that centres exactly in the icon (same parity as the size).
        int Even(int w) { w = Math.Max(1, w); return (size - w) % 2 != 0 ? w + 1 : w; }
        int R(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);

        var t = Math.Max(1, R(1.5 * k));
        int ox0 = P(0.5), oy0 = P(1.5), ox1 = size - P(0.5), oy1 = P(14.0);
        var rad = 2.6 * k;
        var outer = Box(ox0, oy0, ox1, oy1, rad);
        var inner = Box(ox0 + t, oy0 + t, ox1 - t, oy1 - t, Math.Max(0, rad - t));
        var wn = Even(R(1.5 * k));
        var neck = Box((size - wn) / 2, oy1, (size + wn) / 2, P(15.2), 0);
        var wf = Even(R(7 * k));
        int fy0 = P(15.2), fy1 = Math.Max(fy0 + 1, P(16.6));
        var foot = Box((size - wf) / 2, fy0, (size + wf) / 2, fy1, (fy1 - fy0) / 2.0);

        // The mark sits inside the screen with at least a pixel clear all round.
        var gap = Math.Max(1, R(1.2 * k));
        int top = oy0 + t + gap, bottom = oy1 - t - gap;
        Shape mark;
        if (g == TrayGlyph.Error)
        {
            var ws = Even(Math.Max(2, R(1.8 * k)));
            var dot0 = bottom - ws;
            var bar = Box((size - ws) / 2, top, (size + ws) / 2, dot0 - Math.Max(1, R(k)), ws / 4.0);
            var dot = Box((size - ws) / 2, dot0, (size + ws) / 2, bottom, ws / 2.0);
            mark = (x, y) => bar(x, y) || dot(x, y);
        }
        else
        {
            var ws = Even(Math.Max(2, R(2.2 * k)));
            var wb = Even(R(8 * k));
            var bh = Math.Max(2, R(2.1 * k));
            var bar = Box((size - wb) / 2, top, (size + wb) / 2, top + bh, 0);
            var stem = Box((size - ws) / 2, top, (size + ws) / 2, bottom, 0);
            mark = (x, y) => bar(x, y) || stem(x, y);
        }

        var bmp = new Drawing.Bitmap(size, size, Drawing.Imaging.PixelFormat.Format32bppArgb);
        var scale = g == TrayGlyph.Connecting ? 0.5 : 1.0;
        for (var py = 0; py < size; py++)
        for (var px = 0; px < size; px++)
        {
            var hits = 0;
            for (var sy = 0; sy < SS; sy++)
            for (var sx = 0; sx < SS; sx++)
            {
                double x = px + (sx + 0.5) / SS, y = py + (sy + 0.5) / SS;
                var stand = neck(x, y) || foot(x, y);
                var on = g == TrayGlyph.Casting
                    ? (outer(x, y) && !mark(x, y)) || stand
                    : (outer(x, y) && !inner(x, y)) || stand || mark(x, y);
                if (on) hits++;
            }
            var a = (int)(hits * 255 / (SS * SS) * scale);
            bmp.SetPixel(px, py, Drawing.Color.FromArgb(a, colour));
        }
        return bmp;
    }

    /// <summary>A box from (x0,y0) to (x1,y1) in pixels, corners rounded to radius r.</summary>
    private static Shape Box(double x0, double y0, double x1, double y1, double r) => (x, y) =>
    {
        if (x < x0 || x >= x1 || y < y0 || y >= y1) return false;
        var cx = Math.Clamp(x, x0 + r, x1 - r);
        var cy = Math.Clamp(y, y0 + r, y1 - r);
        return (x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r;
    };
}
