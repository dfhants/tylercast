using System.Globalization;

namespace TylerCast.Core;

/// <summary>
/// One line per event, full bodies of every SSDP, SOAP and HTTP exchange at debug
/// level, so a refusal by the TV can be read back from the file.
///
/// Everything (debug included) goes to the log file: %LOCALAPPDATA%\TylerCast\TylerCast.log
/// on Windows, ~/Library/Logs/TylerCast-dotnet.log on the Mac (kept apart from the
/// Swift app's log). stderr gets info and up, plus debug when <see cref="Verbose"/> is set.
///
/// Devices on the network supply much of what is logged, so (security review, finding 5): the
/// file is capped at <see cref="MaxBytes"/> and rotated once (.1), bodies are truncated
/// (<see cref="Body"/>), and every line after a message's first is indented, so nothing a
/// device sends can pass for a line of TylerCast's own.
/// </summary>
public static class Log
{
    public enum Level { Debug, Info, Warn, Error }

    public static bool Verbose { get; set; }
    /// <summary>Called with every info-or-higher line (the tray app shows the latest error).</summary>
    public static event Action<Level, string>? Observer;

    public static string FilePath { get; } = DefaultPath();

    public const long MaxBytes = 5_000_000;
    private static readonly Lock Gate = new();
    private static StreamWriter? _file;

    private static string DefaultPath()
    {
        string dir = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TylerCast")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs");
        return Path.Combine(dir, OperatingSystem.IsWindows() ? "TylerCast.log" : "TylerCast-dotnet.log");
    }

    private static StreamWriter? File()
    {
        if (_file is not null && _file.BaseStream.Length < MaxBytes) return _file;
        try
        {
            if (_file is not null)
            {
                _file.Dispose();
                _file = null;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            if (System.IO.File.Exists(FilePath) && new FileInfo(FilePath).Length >= MaxBytes)
                System.IO.File.Move(FilePath, FilePath + ".1", overwrite: true);
            var s = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            _file = new StreamWriter(s) { AutoFlush = true };
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return _file;
    }

    /// <summary>A device's (or our own) message body for the log: at most <paramref name="max"/> characters.</summary>
    public static string Body(string text, int max = 4096) =>
        text.Length <= max ? text.Trim() : $"{text[..max].Trim()}\n... ({text.Length - max} more characters)";

    public static void Debug(string msg) => Write(Level.Debug, msg);
    public static void Info(string msg) => Write(Level.Info, msg);
    public static void Warn(string msg) => Write(Level.Warn, msg);
    public static void Error(string msg) => Write(Level.Error, msg);

    /// <summary>Continuation lines indented; other control characters shown as '?'.</summary>
    private static string Clean(string msg)
    {
        var sb = new System.Text.StringBuilder(msg.Length + 16);
        for (var i = 0; i < msg.Length; i++)
        {
            var c = msg[i];
            if (c == '\r' && i + 1 < msg.Length && msg[i + 1] == '\n') continue;
            if (c is '\n' or '\r') sb.Append("\n    ");
            else if (c < ' ' && c != '\t' || c == '\u007f') sb.Append('?');
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static string Name(Level l) => l switch
    {
        Level.Debug => "DEBUG", Level.Info => "INFO ", Level.Warn => "WARN ", _ => "ERROR",
    };

    private static void Write(Level level, string msg)
    {
        var line = $"{DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} TylerCast {Name(level)} {Clean(msg)}";
        lock (Gate)
        {
            File()?.WriteLine(line);
            if (level != Level.Debug || Verbose) Console.Error.WriteLine(line);
        }
        if (level != Level.Debug) Observer?.Invoke(level, msg);
    }
}
