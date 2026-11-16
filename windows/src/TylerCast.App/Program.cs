using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TylerCast.Core;
using TylerCast.Windows;

namespace TylerCast.App;

public static class Program
{
    private const string MutexName = @"Local\TylerCast.App";
    private const string ShowEventName = @"Local\TylerCast.App.Show";

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--screenshot") return Screenshots.Run(args[1]);

        // One copy at a time: starting it again (Start menu, desktop) shows the running copy's flyout.
        using var mutex = new Mutex(true, MutexName, out var first);
        if (!first)
        {
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); }
            catch (WaitHandleCannotBeOpenedException) { }
            return 0;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, ThemeMode = ThemeMode.System };
        try { Platform.Init(); }
        catch (Exception e)
        {
            MessageBox.Show(e.Message, "TylerCast", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
        Log.Info($"TylerCast app started ({Environment.ProcessPath}); log at {Log.FilePath}");
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Error($"Unhandled: {e.Exception}");
            e.Handled = true;
        };

        var tray = new TrayHost(background: args.Contains("--background"));
        app.SessionEnding += (_, _) => tray.SessionEnding();

        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var waiter = new Thread(() =>
        {
            while (show.WaitOne()) app.Dispatcher.BeginInvoke(tray.ShowFlyout);
        }) { IsBackground = true, Name = "show-request" };
        waiter.Start();

        return app.Run();
    }
}

/// <summary>
/// <c>TylerCast.exe --screenshot DIR</c>: renders the flyout in each state, and the settings
/// window, in light and dark, to PNGs, with made-up TVs. CI runs it on Windows so the
/// layout can be looked at on the Mac before a test round.
/// </summary>
internal static class Screenshots
{
    public static int Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var ui = Dispatcher.CurrentDispatcher;
        var failures = 0;
        foreach (var (theme, name) in new[] { (ThemeMode.Light, "light"), (ThemeMode.Dark, "dark") })
        {
            app.ThemeMode = theme;
            var scenes = new (string, Func<AppModel>)[]
            {
                ("1-ready", () => AppModel.Preview(ui, new MirrorSession.State.Stopped(), 2)),
                ("2-public-network", () => AppModel.Preview(ui, new MirrorSession.State.Stopped(), 2, warning: true)),
                ("3-connecting-first-time", () => AppModel.Preview(ui, new MirrorSession.State.Connecting("asking the TV to play"), 2, firstCast: true)),
                ("4-casting", () => AppModel.Preview(ui, new MirrorSession.State.Playing("Living_Room_TV"), 2)),
                ("5-failed", () => AppModel.Preview(ui, new MirrorSession.State.Failed("The TV can't reach this computer"), 2)),
                ("6-looking", () => AppModel.Preview(ui, new MirrorSession.State.Stopped(), 0, searching: true)),
            };
            foreach (var (scene, make) in scenes)
            {
                try { Save(new PanelView { DataContext = make() }, Path.Combine(dir, $"{name}-{scene}.png")); }
                catch (Exception e) { failures++; Console.Error.WriteLine($"{name}-{scene}: {e}"); }
            }
            try
            {
                var w = new SettingsWindow(AppModel.Preview(ui, new MirrorSession.State.Stopped(), 1));
                var body = (FrameworkElement)w.Content;
                w.Content = null;
                var page = new System.Windows.Controls.Border { Child = body, Width = w.Width, DataContext = w.DataContext, Resources = w.Resources };
                page.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
                Save(page, Path.Combine(dir, $"{name}-7-settings.png"));
            }
            catch (Exception e) { failures++; Console.Error.WriteLine($"{name}-settings: {e}"); }
        }
        Console.WriteLine($"Screenshots in {dir}: {Directory.GetFiles(dir, "*.png").Length} files, {failures} failed");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>Lays the element out on its own and renders it at 150% (as on a typical laptop).</summary>
    private static void Save(FrameworkElement e, string file)
    {
        e.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        e.Arrange(new Rect(e.DesiredSize));
        e.UpdateLayout();
        const double scale = 1.5;
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(e.ActualWidth * scale), (int)Math.Ceiling(e.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bmp.Render(e);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        using var f = File.Create(file);
        png.Save(f);
    }
}
