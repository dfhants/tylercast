using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using TylerCast.Core;
using Windows.Win32;
using Windows.Win32.Graphics.Dxgi;

namespace TylerCast.Windows;

/// <summary>
/// A report that is written as it goes (so a crash still leaves everything up to
/// that point), with RESULT lines a script on the Mac can pick out.
/// </summary>
public sealed class Report(string path) : IDisposable
{
    private readonly StreamWriter _w = new(path, append: false, Encoding.UTF8) { AutoFlush = true };

    public void Line(string s = "")
    {
        _w.WriteLine(s);
        Console.WriteLine(s);
    }

    public void Section(string name) => Line($"\n== {name} ==");
    public void Result(string check, bool ok, string detail) => Line($"RESULT {check}: {(ok ? "OK" : "FAIL")}: {detail}");

    /// <summary>Runs one check; an exception is reported as a failure of that check only.</summary>
    public void Try(string check, Action body)
    {
        try { body(); }
        catch (Exception e)
        {
            Result(check, false, Captures.Describe(e));
            Line($"  {e.GetType().FullName}: {e.Message}\n{e.StackTrace}");
        }
    }

    public void Dispose() => _w.Dispose();
}

public static unsafe class Diag
{
    public static void Run(Report r)
    {
        r.Section("System");
        r.Try("windows", () =>
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            string V(string n) => k?.GetValue(n)?.ToString() ?? "?";
            r.Line($"Windows: {V("ProductName")} {V("DisplayVersion")} (build {V("CurrentBuild")}.{V("UBR")}, edition {V("EditionID")})");
            r.Line($"  (Windows 11 reports itself as \"Windows 10\" in ProductName; build 22000 or later is Windows 11)");
            r.Line($"OS version {Environment.OSVersion.Version}, {RuntimeInformation.OSArchitecture}, process {RuntimeInformation.ProcessArchitecture}");
            using var cpu = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            r.Line($"CPU: {cpu?.GetValue("ProcessorNameString")?.ToString()?.Trim()} ({Environment.ProcessorCount} logical processors)");
            var ps = System.Windows.Forms.SystemInformation.PowerStatus;
            r.Line($"Power: {ps.PowerLineStatus}, battery {ps.BatteryLifePercent:P0}");
            r.Line($"TylerCast {typeof(Diag).Assembly.GetName().Version}, .NET {Environment.Version}, log {Log.FilePath}");
        });

        r.Section("Graphics and displays");
        r.Try("displays", () =>
        {
            var iid = typeof(IDXGIFactory1).GUID;
            PInvoke.CreateDXGIFactory1(&iid, out var f).ThrowOnFailure();
            var factory = (IDXGIFactory1)f;
            var primary = PInvoke.MonitorFromPoint(new System.Drawing.Point(0, 0), global::Windows.Win32.Graphics.Gdi.MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);
            for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Succeeded; a++)
            {
                var d = adapter.GetDesc1();
                r.Line($"GPU {a}: {d.Description.ToString().Trim()} (vendor 0x{d.VendorId:X4}, device 0x{d.DeviceId:X4}, {(ulong)d.DedicatedVideoMemory / 1048576} MB dedicated)");
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Succeeded; o++)
                {
                    var od = output.GetDesc();
                    var rc = od.DesktopCoordinates;
                    var hdr = "";
                    if (output is IDXGIOutput6 o6)
                    {
                        var d1 = o6.GetDesc1();
                        hdr = $", colour space {d1.ColorSpace}, {d1.BitsPerColor}-bit";
                    }
                    r.Line($"  display {o}: {od.DeviceName.ToString().Trim()} {rc.right - rc.left}x{rc.bottom - rc.top} at ({rc.left},{rc.top}), "
                        + $"rotation {od.Rotation}{hdr}{(od.Monitor == primary ? ", PRIMARY" : "")}");
                }
            }
            r.Line($"Primary screen (physical pixels): {PInvoke.GetSystemMetrics(global::Windows.Win32.UI.WindowsAndMessaging.SYSTEM_METRICS_INDEX.SM_CXSCREEN)}"
                + $"x{PInvoke.GetSystemMetrics(global::Windows.Win32.UI.WindowsAndMessaging.SYSTEM_METRICS_INDEX.SM_CYSCREEN)}");
        });

        r.Section("Encoders");
        r.Try("encoder list", () =>
        {
            var v = EncoderPicker.List();
            r.Line($"H.264 encoders: {v.Count}");
            foreach (var e in v) r.Line($"  {e.Index}: {e.Name} ({(e.Hardware ? "hardware" : "software")}, {(e.Async ? "async" : "sync")}{(e.Vendor.Length > 0 ? ", " + e.Vendor : "")})");
            r.Result("h264 encoders found", v.Count > 0, $"{v.Count}");
            foreach (var c in new[] { AudioCodec.Ac3, AudioCodec.Aac })
            {
                var a = MfAudioEncoder.Available(c);
                r.Result($"{c} encoder found", a.Count > 0, a.Count > 0 ? string.Join(", ", a) : "none");
            }
        });

        r.Section("Sound output");
        r.Try("audio device", () =>
        {
            using var l = new Loopback();
            r.Line($"Default output: {l.DeviceName}");
            r.Line($"Mix format: {l.MixFormat}");
            r.Line($"Volume {l.Volume:P0}, muted {l.Muted}");
        });

        r.Section("Network");
        r.Try("adapters", () =>
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var p = n.GetIPProperties();
                var v4 = p.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => $"{a.Address}/{a.PrefixLength}");
                var gw = p.GatewayAddresses.Where(g => g.Address.AddressFamily == AddressFamily.InterNetwork).Select(g => g.Address.ToString());
                r.Line($"  {n.Name} [{n.Description}] {n.NetworkInterfaceType} {n.OperationalStatus}: {string.Join(", ", v4)}"
                    + (gw.Any() ? $" via {string.Join(", ", gw)}" : ""));
            }
        });
        r.Try("network profile", () => r.Line(PowerShell("Get-NetConnectionProfile | Format-List Name,InterfaceAlias,NetworkCategory,IPv4Connectivity")));
        r.Try("firewall", () =>
        {
            r.Line(PowerShell("Get-NetFirewallProfile | Format-Table Name,Enabled,DefaultInboundAction,AllowInboundRules,NotifyOnListen -AutoSize | Out-String -Width 200"));
            var exe = Environment.ProcessPath ?? "";
            r.Line($"Rules for {exe}:");
            r.Line(PowerShell($"Get-NetFirewallApplicationFilter -Program '{exe.Replace("'", "''")}' -ErrorAction SilentlyContinue | Get-NetFirewallRule | Format-Table DisplayName,Enabled,Direction,Action,Profile -AutoSize | Out-String -Width 200"));
        });
    }

    public static string PowerShell(string command)
    {
        var psi = new ProcessStartInfo("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", command])
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync();
        var err = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(20_000)) { p.Kill(); return "(timed out)"; }
        return (output.Result + err.Result).Trim();
    }
}

public static class SelfTest
{
    /// <summary>
    /// Every check that needs no TV, each in its own child process (tylercast selftest-part),
    /// so a check that crashes the process (round 1: Windows' AC-3 encoder, 0xC0000409) fails
    /// on its own and the rest still run. Files land in <paramref name="dir"/>; report.txt has
    /// every check's output in order, with RESULT lines.
    /// </summary>
    public static int Run(string dir, bool tone)
    {
        Directory.CreateDirectory(dir);
        using var r = new Report(Path.Combine(dir, "report.txt"));
        r.Line($"TylerCast selftest {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        var parts = new List<string> { "diag", "discovery" };
        parts.AddRange(EncoderPicker.List().Select(e => $"encoder:{e.Index}"));
        parts.AddRange(["capture:dxgi", "capture:gdi", "loopback", "audio:Ac3", "audio:Aac", "pipeline:aac", "pipeline:ac3"]);
        foreach (var part in parts)
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!, ["selftest-part", part, dir, tone ? "tone" : "no-tone"])
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            using var child = Process.Start(psi)!;
            child.OutputDataReceived += (_, e) => { if (e.Data is not null) r.Line(e.Data); };
            child.ErrorDataReceived += (_, e) => { if (e.Data is not null && e.Data.Contains(" ERROR ")) r.Line(e.Data); };
            child.BeginOutputReadLine();
            child.BeginErrorReadLine();
            if (!child.WaitForExit(180_000))
            {
                child.Kill(entireProcessTree: true);
                r.Result($"part {part}", false, "hung for 3 minutes; stopped");
                continue;
            }
            child.WaitForExit(); // flush the output events
            if (child.ExitCode != 0)
                r.Result($"part {part}", false, $"the check's process ended with 0x{child.ExitCode:X8}"
                    + (child.ExitCode == unchecked((int)0xC0000409) ? " (a crash inside Windows or a driver)" : ""));
        }
        r.Section("Done");
        r.Line($"Files in {dir}");
        return 0;
    }

    /// <summary>One check, in this (child) process. Output goes to stdout for the parent's report.</summary>
    public static int RunPart(string part, string dir, bool tone)
    {
        using var r = new Report(Path.Combine(dir, $"part-{part.Replace(':', '-')}.txt"));
        var v = new VideoSettings();
        switch (part.Split(':'))
        {
            case ["diag"]:
                Diag.Run(r);
                break;
            case ["discovery"]:
                r.Section("Discovery (SSDP)");
                r.Try("discovery", () =>
                {
                    var found = Discovery.RenderersAsync(timeout: 4).GetAwaiter().GetResult();
                    foreach (var d in found) r.Line($"  {d.Name} [{d.Model}] at {d.Ip}, AVTransport {d.AVTransport}");
                    r.Result("discovery", found.Count > 0, found.Count > 0 ? string.Join(", ", found.Select(d => $"{d.Name} {d.Ip}"))
                        : $"no TV answered (send error: {Ssdp.LastSendError?.ToString() ?? "none"}); firewall, network profile, or TV off?");
                });
                break;
            case ["encoder", var n]:
                r.Section($"H.264 encoder {n} (2s of the test pattern)");
                r.Try($"encoder {n}", () =>
                {
                    var e = EncoderPicker.List().First(x => x.Index.ToString(System.Globalization.CultureInfo.InvariantCulture) == n);
                    var t = EncoderPicker.Try(e, v, frames: 50);
                    var file = $"encoder-{e.Index}.ts";
                    if (t.TransportStream.Length > 0) File.WriteAllBytes(Path.Combine(dir, file), t.TransportStream);
                    r.Result($"encoder {e.Index} {e.Name}", t.Ok,
                        $"{t.Detail}; {t.MsPerFrame:0.0}ms/frame to submit; file {file}" + (t.Refused.Count > 0 ? $"; refused: {string.Join(", ", t.Refused)}" : ""));
                });
                break;
            case ["capture", var method]:
                r.Section($"Screen capture: {method}");
                r.Try($"capture {method}", () => CaptureCheck(r, method, dir));
                break;
            case ["loopback"]:
                r.Section("Sound: loopback");
                r.Try("loopback", () => LoopbackCheck(r, dir, tone));
                break;
            case ["audio", var codecName]:
                var c = Enum.Parse<AudioCodec>(codecName);
                r.Section($"Sound: {c} encoder");
                r.Try($"{c} encode", () =>
                {
                    using var enc = new MfAudioEncoder(c);
                    var mux = new OfflineMux(new TsMuxer.Config { AudioCodec = c });
                    var frames = 0;
                    enc.Output += (f, t) => { mux.Audio(f, t / 1e7); frames++; };
                    var pcm = Pcm.Sine(1000, 2, 0.5f);
                    for (var i = 0; i < pcm.Length; i += 960) enc.Encode(Pcm.ToInt16(pcm.AsSpan(i, Math.Min(960, pcm.Length - i))), i / 2 * 10_000_000L / 48_000);
                    var file = $"audio-{c.ToString().ToLowerInvariant()}.ts";
                    File.WriteAllBytes(Path.Combine(dir, file), mux.ToArray());
                    r.Result($"{c} encode", frames > 10, $"{enc.Name}: {frames} frames from 2s of a 1 kHz tone, {enc.Bitrate / 1000} kbit/s; file {file}");
                });
                break;
            case ["pipeline", var kind]:
                r.Section($"Full pipeline, {kind}: 20s of screen and sound to a file");
                r.Try($"pipeline {kind}", () => PipelineCheck(r, dir, kind == "ac3" ? MirrorOptions.AudioKind.Ac3 : MirrorOptions.AudioKind.Aac));
                break;
            default:
                r.Result($"part {part}", false, "unknown part");
                return 2;
        }
        return 0;
    }

    private static void PipelineCheck(Report r, string dir, MirrorOptions.AudioKind wanted)
    {
        var audio = Platform.Resolve(wanted);
        var o = new MirrorOptions { Audio = audio, SoundOnTvOnly = false };
        var p = new Pipeline(o, new WindowsVideoSource(o), o.CapturesSound ? new WindowsAudioSource(o.Codec, muteComputer: false) : null);
        var file = $"pipeline-{MirrorOptions.Name(wanted)}.ts";
        long bytes = 0;
        string? failed = null;
        p.OnFailure = why => failed = why;
        using (var f = File.Create(Path.Combine(dir, file)))
        {
            var gate = new Lock();
            p.Broadcaster.Subscribe((d, _) => { if (d is not null) lock (gate) { f.Write(d); bytes += d.Length; } });
            p.StartAsync(serve: false).GetAwaiter().GetResult();
            Thread.Sleep(20_500); // the second 10s window is the steady one (the first includes start-up)
            p.Stop();
        }
        var s = p.Pacer.LastStats;
        var frames = s is { } st ? $"{st.VideoFrames} video frames in 10s (25fps = 250), {st.AudioFrames} audio, {st.LateFrames} late" : "no stats";
        r.Result($"pipeline {MirrorOptions.Name(wanted)}", failed is null && bytes > 10_000_000 && s is { LateFrames: 0, VideoFrames: > 230 },
            $"{bytes / 1_000_000.0:0.0} MB ({MirrorOptions.Name(audio)}); {frames}; {failed ?? "no failures"}; file {file}");
    }

    /// <summary>
    /// Shows a window of known colours at the top-left of the screen, captures, and checks the
    /// pixels came back right; also measures capture and convert speed and saves a screenshot.
    /// </summary>
    private static void CaptureCheck(Report r, string method, string dir)
    {
        using var shown = new ManualResetEventSlim();
        using var close = new ManualResetEventSlim();
        System.Windows.Forms.Form? form = null;
        var ui = new Thread(() =>
        {
            form = new System.Windows.Forms.Form
            {
                FormBorderStyle = System.Windows.Forms.FormBorderStyle.None, TopMost = true, ShowInTaskbar = false,
                StartPosition = System.Windows.Forms.FormStartPosition.Manual, Location = new System.Drawing.Point(0, 0),
                Size = new System.Drawing.Size(400, 200), BackColor = System.Drawing.Color.FromArgb(255, 0, 255),
            };
            // Painted, not just shown: on a slow machine the window can be on screen but still blank.
            form.Paint += (_, _) => shown.Set();
            var timer = new System.Windows.Forms.Timer { Interval = 50 };
            timer.Tick += (_, _) => { if (close.IsSet) form.Close(); };
            timer.Start();
            System.Windows.Forms.Application.Run(form);
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        var painted = shown.Wait(10_000);
        Thread.Sleep(700); // let the compositor put it on screen
        try
        {
            using var cap = Captures.Open(method);
            var img = new ScreenImage(cap.Width, cap.Height);
            var sw = Stopwatch.StartNew();
            var got = 0;
            var calls = 0;
            while (sw.ElapsedMilliseconds < 1000) { calls++; if (cap.Capture(img, 40)) got++; }
            if (got == 0 && !cap.Capture(img, 500)) throw new InvalidOperationException("no image");
            // The window's pixels (scaled by DPI: check well inside it).
            var px = img.Pixels.AsSpan((50 * img.Width + 50) * 4, 4);
            var magenta = px[0] > 200 && px[1] < 60 && px[2] > 200;
            if (!magenta)
            {
                // One more look after a pause: the window may have arrived late.
                Thread.Sleep(1000);
                cap.Capture(img, 500);
                px = img.Pixels.AsSpan((50 * img.Width + 50) * 4, 4);
                magenta = px[0] > 200 && px[1] < 60 && px[2] > 200;
            }
            var conv = new FrameConverter(1920, 1080);
            var nv12 = new byte[conv.FrameBytes];
            sw.Restart();
            for (var i = 0; i < 10; i++) conv.Convert(img.Pixels, img.Width, img.Height, img.Stride, nv12, 0);
            var convertMs = sw.Elapsed.TotalMilliseconds / 10;
            var file = $"capture-{method}.bmp";
            Bitmap.SaveHalf(img, Path.Combine(dir, file));
            r.Result($"capture {method}", magenta, $"{cap.Name}; test window {(magenta ? "seen" : $"NOT seen (pixel B{px[0]} G{px[1]} R{px[2]}, window {(painted ? "painted" : "never painted")})")}; "
                + $"{got} new images in 1s ({calls} calls); convert to 1080p NV12 {convertMs:0.0}ms; screenshot {file}");
        }
        finally
        {
            close.Set();
            ui.Join(3000);
        }
    }

    /// <summary>
    /// Records the loopback while playing a 1 kHz tone (if <paramref name="tone"/>), then again with the
    /// speakers muted: tells whether "sound on the TV only" can work on this PC.
    /// </summary>
    private static void LoopbackCheck(Report r, string dir, bool tone)
    {
        using var l = new Loopback();
        r.Line($"Loopback from {l.DeviceName}: {l.MixFormat}, volume {l.Volume:P0}, muted {l.Muted}");
        float[] Record(double seconds, bool play)
        {
            var got = new List<float>();
            var gate = new Lock();
            l.Start((pcm, _) => { lock (gate) got.AddRange(pcm); });
            if (play)
            {
                using var ms = new MemoryStream(Pcm.Wav(Pcm.Sine(1000, seconds - 0.5, 0.3f)));
                using var player = new System.Media.SoundPlayer(ms);
                Thread.Sleep(200);
                player.PlaySync();
            }
            else Thread.Sleep((int)(seconds * 1000));
            Thread.Sleep(300);
            l.Stop();
            lock (gate) return [.. got];
        }
        var quiet = Record(1.5, play: false);
        r.Line($"  quiet: {quiet.Length / 2} samples, 1 kHz level {Pcm.ToneLevel(quiet, 2, 48_000, 1000):0.0000}");
        if (!tone)
        {
            r.Line("  tone check skipped (--no-tone)");
            return;
        }
        var heard = Record(3, play: true);
        var level = Pcm.ToneLevel(heard, 2, 48_000, 1000);
        File.WriteAllBytes(Path.Combine(dir, "loopback-tone.wav"), Pcm.Wav(heard));
        r.Result("loopback hears the computer", level > 0.01, $"1 kHz level {level:0.0000} (played at 0.3 × volume {l.Volume:P0}); file loopback-tone.wav");
        var was = l.SetMute(true);
        try
        {
            var muted = Record(3, play: true);
            var mutedLevel = Pcm.ToneLevel(muted, 2, 48_000, 1000);
            File.WriteAllBytes(Path.Combine(dir, "loopback-muted.wav"), Pcm.Wav(muted));
            r.Result("loopback while speakers muted", mutedLevel > 0.01,
                $"1 kHz level {mutedLevel:0.0000} ({(mutedLevel > level * 0.5 ? "muting the speakers keeps the TV's sound" : "muting the speakers silences the capture too")}); file loopback-muted.wav");
        }
        finally { l.SetMute(was); }
    }
}

/// <summary>Writes a half-size 24-bit BMP of a capture (small enough to upload, big enough to read).</summary>
public static class Bitmap
{
    public static void SaveHalf(ScreenImage img, string path)
    {
        int w = img.Width / 2, h = img.Height / 2, row = (w * 3 + 3) & ~3;
        using var f = new BinaryWriter(File.Create(path));
        f.Write("BM"u8); f.Write(54 + row * h); f.Write(0); f.Write(54);
        f.Write(40); f.Write(w); f.Write(h); f.Write((short)1); f.Write((short)24); f.Write(0); f.Write(row * h); f.Write(2835); f.Write(2835); f.Write(0); f.Write(0);
        var line = new byte[row];
        for (var y = h - 1; y >= 0; y--) // bottom-up
        {
            for (var x = 0; x < w; x++)
            {
                var o = ((y * 2) * img.Width + x * 2) * 4;
                line[x * 3] = img.Pixels[o]; line[x * 3 + 1] = img.Pixels[o + 1]; line[x * 3 + 2] = img.Pixels[o + 2];
            }
            f.Write(line);
        }
    }
}
