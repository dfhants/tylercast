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

/// <summary>The tray glyph, drawn as Sources/TylerCast/Icon.swift draws the Mac's (18-unit grid, y down here).</summary>
internal static class TrayGlyphs
{
    public static Drawing.Bitmap Draw(TrayGlyph g, Drawing.Color colour, int size)
    {
        var bmp = new Drawing.Bitmap(size, size, Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var gr = Drawing.Graphics.FromImage(bmp);
        gr.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
        gr.PixelOffsetMode = Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        var k = size / 18f;
        gr.ScaleTransform(k, k);
        var c = g == TrayGlyph.Connecting ? Drawing.Color.FromArgb(128, colour) : colour;
        using var brush = new Drawing.SolidBrush(c);
        // Stand: neck and foot.
        gr.FillRectangle(brush, 8.25f, 13.0f, 1.5f, 2.4f);
        using (var foot = Rounded(5.5f, 15.0f, 7f, 1.4f, 0.7f)) gr.FillPath(brush, foot);
        using var screen = Rounded(1.25f, 2.25f, 15.5f, 11f, 2.2f);
        using var mark = g == TrayGlyph.Error ? Bang() : LetterT();
        if (g == TrayGlyph.Casting)
        {
            // Filled screen with the T cut out of it.
            using var region = new Drawing.Region(screen);
            region.Exclude(mark);
            gr.FillRegion(brush, region);
        }
        else
        {
            using var pen = new Drawing.Pen(c, 1.5f);
            gr.DrawPath(pen, screen);
            gr.FillPath(brush, mark);
        }
        return bmp;
    }

    private static Drawing.Drawing2D.GraphicsPath LetterT()
    {
        var p = Rounded(5.0f, 4.5f, 8f, 1.9f, 0.5f);
        using var stem = Rounded(8.05f, 5.5f, 1.9f, 5.6f, 0.5f);
        p.AddPath(stem, false);
        return p;
    }

    private static Drawing.Drawing2D.GraphicsPath Bang()
    {
        var p = Rounded(8.1f, 4.0f, 1.8f, 4.6f, 0.6f);
        p.AddEllipse(8.05f, 9.4f, 1.9f, 1.9f);
        return p;
    }

    private static Drawing.Drawing2D.GraphicsPath Rounded(float x, float y, float w, float h, float r)
    {
        var p = new Drawing.Drawing2D.GraphicsPath();
        var d = 2 * r;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
