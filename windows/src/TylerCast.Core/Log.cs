using System.Globalization;

namespace TylerCast.Core;

/// <summary>
/// One line per event, full bodies of every SSDP, SOAP and HTTP exchange at debug
/// level, so a refusal by the TV can be read back from the file.
///
/// Everything (debug included) goes to the log file: %LOCALAPPDATA%\TylerCast\TylerCast.log
/// on Windows, ~/Library/Logs/TylerCast-dotnet.log on the Mac (kept apart from the
/// Swift app's log). stderr gets info and up, plus debug when <see cref="Verbose"/> is set.
/// </summary>
public static class Log
{
    public enum Level { Debug, Info, Warn, Error }

    public static bool Verbose { get; set; }
    /// <summary>Called with every info-or-higher line (the tray app shows the latest error).</summary>
    public static event Action<Level, string>? Observer;

    public static string FilePath { get; } = DefaultPath();

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
        if (_file is not null) return _file;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var s = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            _file = new StreamWriter(s) { AutoFlush = true };
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return _file;
    }

    public static void Debug(string msg) => Write(Level.Debug, msg);
    public static void Info(string msg) => Write(Level.Info, msg);
    public static void Warn(string msg) => Write(Level.Warn, msg);
    public static void Error(string msg) => Write(Level.Error, msg);

    private static string Name(Level l) => l switch
    {
        Level.Debug => "DEBUG", Level.Info => "INFO ", Level.Warn => "WARN ", _ => "ERROR",
    };

    private static void Write(Level level, string msg)
    {
        var line = $"{DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} TylerCast {Name(level)} {msg}";
        lock (Gate)
        {
            File()?.WriteLine(line);
            if (level != Level.Debug || Verbose) Console.Error.WriteLine(line);
        }
        if (level != Level.Debug) Observer?.Invoke(level, msg);
    }
}
