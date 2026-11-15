import Foundation

/// Logging, as `mirror.py` does it: one line per event, full bodies of every SSDP,
/// SOAP and HTTP exchange at debug level, so a refusal by the TV can be read back.
///
/// Everything (debug included) goes to ~/Library/Logs/TylerCast.log. stderr gets
/// info and up, plus debug when `verbose` is set (`-v` on the command line).
public enum Log {
    public enum Level: String { case debug = "DEBUG", info = "INFO ", warn = "WARN ", error = "ERROR" }

    public static var verbose = false
    /// Called with every info-or-higher line (the menu bar app shows the latest error).
    public static var observer: ((Level, String) -> Void)?

    public static let fileURL: URL = {
        let dir = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Logs")
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir.appendingPathComponent("TylerCast.log")
    }()

    private static let lock = NSLock()
    private static var file: FileHandle? = {
        if !FileManager.default.fileExists(atPath: fileURL.path) {
            FileManager.default.createFile(atPath: fileURL.path, contents: nil)
        }
        let h = try? FileHandle(forWritingTo: fileURL)
        _ = try? h?.seekToEnd()
        return h
    }()
    private static let stamp: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "HH:mm:ss.SSS"
        return f
    }()

    public static func debug(_ msg: @autoclosure () -> String) { write(.debug, msg()) }
    public static func info(_ msg: @autoclosure () -> String) { write(.info, msg()) }
    public static func warn(_ msg: @autoclosure () -> String) { write(.warn, msg()) }
    public static func error(_ msg: @autoclosure () -> String) { write(.error, msg()) }

    private static func write(_ level: Level, _ msg: String) {
        lock.lock()
        let line = "\(stamp.string(from: Date())) TylerCast \(level.rawValue) \(msg)\n"
        file?.write(Data(line.utf8))
        if level != .debug || verbose { FileHandle.standardError.write(Data(line.utf8)) }
        lock.unlock()
        if level != .debug { observer?(level, msg) }
    }
}
