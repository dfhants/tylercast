import AppKit
import MirrorCore
import ScreenCaptureKit
import ServiceManagement

/// Everything the panel shows and does: the TVs on this network, the
/// mirroring session, margin, permissions, open-at-login.
@MainActor
final class AppModel: ObservableObject {
    @Published private(set) var state: MirrorSession.State = .stopped
    @Published private(set) var startedAt: Date?
    /// TVs that answered a search on the network this Mac is on now. A TV
    /// from another network is never offered: it could not fetch the stream.
    @Published private(set) var renderers: [Renderer] = []
    @Published private(set) var searching = false
    /// The TV being mirrored to (or being connected to).
    @Published private(set) var activeLocation: String?
    /// The TV the last error was about, so it shows on that TV's row.
    @Published private(set) var errorLocation: String?
    @Published private(set) var screenOK = ScreenCapture.hasPermission
    @Published private(set) var networkBlocked = false
    @Published private(set) var openAtLogin = SMAppService.mainApp.status == .enabled
    @Published private(set) var loginItemError: String?
    @Published private(set) var margin: Double
    /// Send the Mac's sound to the TV (off: a silent track the TV still accepts).
    @Published var soundOn: Bool {
        didSet { applySound(); defaults.set(soundOn, forKey: "soundOn") }
    }
    /// Mute the Mac's speakers while mirroring, so sound comes from the TV only.
    @Published var tvOnlySound: Bool {
        didSet { applySound(); defaults.set(tvOnlySound, forKey: "tvOnlySound") }
    }
    @Published var testPattern: Bool {
        didSet { session.options.testPattern = testPattern; defaults.set(testPattern, forKey: "testPattern") }
    }

    static let marginRange: ClosedRange<Double> = 0...10

    private let session = MirrorSession(options: MirrorOptions())
    private let defaults = UserDefaults.standard
    private var searchRetries = 0
    private var lastSearchAt: Date?
    /// The last TV mirrored to; it is listed first.
    private var lastLocation: String? {
        get { defaults.string(forKey: "tvLocation") }
        set { defaults.set(newValue, forKey: "tvLocation") }
    }

    init() {
        margin = defaults.double(forKey: "margin")
        testPattern = defaults.bool(forKey: "testPattern")
        soundOn = defaults.object(forKey: "soundOn") as? Bool ?? true
        tvOnlySound = defaults.object(forKey: "tvOnlySound") as? Bool ?? true
        defaults.removeObject(forKey: "lastTV")  // older builds offered it even when it wasn't on this network
        session.options.marginPercent = margin
        session.options.testPattern = testPattern
        applySound()
        session.onState = { [weak self] s in
            guard let self else { return }
            if case .playing = s, self.startedAt == nil { self.startedAt = Date() }
            if case .playing = s {} else { self.startedAt = nil }
            if case .error = s { self.errorLocation = self.activeLocation }
            if !self.session.isActive { self.activeLocation = nil }
            self.state = s
        }
        watchSleep()
        Log.info("TylerCast started; log at \(Log.fileURL.path)")
        requestPermissions()
        search()
    }

    // MARK: derived

    var isActive: Bool { session.isActive }
    var isPlaying: Bool { if case .playing = state { return true }; return false }
    var errorText: String? { if case let .error(e) = state { return e }; return nil }
    var glyph: TylerIcon.Glyph {
        switch state {
        case .stopped: return .idle
        case .connecting: return .connecting
        case .playing: return .playing
        case .error: return .error
        }
    }

    // MARK: actions

    /// A TV's switch: start mirroring to it, or stop. Turning on a second TV
    /// moves the mirror there.
    func setMirroring(_ tv: Renderer, _ on: Bool) {
        if !on {
            if activeLocation == tv.location { Task { await session.stop() } }
            return
        }
        guard activeLocation != tv.location else { return }
        let wasActive = isActive
        activeLocation = tv.location
        errorLocation = nil
        lastLocation = tv.location
        Task {
            if wasActive { await session.stop(); activeLocation = tv.location }
            await session.start(on: tv)
        }
    }

    /// Takes effect the next time mirroring starts.
    private func applySound() {
        session.options.audio = soundOn ? .aac : .silentAC3
        session.options.soundOnTVOnly = tvOnlySound
    }

    /// Sleep (lid closed, Apple menu → Sleep, display turned off) ends
    /// mirroring cleanly: the TV gets Stop, the panel goes back to Off, and
    /// nothing restarts on wake. Idle display sleep can't happen while
    /// mirroring (MirrorSession holds a power assertion).
    private func watchSleep() {
        let nc = NSWorkspace.shared.notificationCenter
        for name in [NSWorkspace.willSleepNotification, NSWorkspace.screensDidSleepNotification] {
            nc.addObserver(forName: name, object: nil, queue: .main) { [weak self] note in
                MainActor.assumeIsolated {
                    guard let self else { return }
                    let what = note.name == NSWorkspace.willSleepNotification ? "Mac" : "display"
                    if self.isActive {
                        Log.info("The \(what) is going to sleep: stopping mirroring")
                        Task { await self.session.stop() }
                    } else if case .error = self.state {
                        // Capture may have failed first, as the display went: that's a stop, not an error.
                        Task { await self.session.stop() }
                    }
                }
            }
        }
    }

    func stepMargin(_ delta: Double) {
        let m = min(max(margin + delta, Self.marginRange.lowerBound), Self.marginRange.upperBound)
        guard m != margin else { return }
        margin = m
        defaults.set(m, forKey: "margin")
        Task { await session.setMargin(m) }  // applies live while playing
    }

    func setOpenAtLogin(_ on: Bool) {
        do {
            if on { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
            loginItemError = nil
        } catch {
            loginItemError = "Couldn't change Open at Login: \(error.localizedDescription)"
            Log.error(loginItemError!)
        }
        openAtLogin = SMAppService.mainApp.status == .enabled
        if SMAppService.mainApp.status == .requiresApproval {
            loginItemError = "Allow TylerCast in System Settings → General → Login Items"
        }
    }

    /// Called when the panel opens: a TV switched on (or a network joined)
    /// since the last look shows up without anyone pressing anything.
    func searchIfStale() {
        if let t = lastSearchAt, Date().timeIntervalSince(t) < 30 { return }
        search()
    }

    /// The panel closed: an error has been seen, so the row and the menu bar
    /// icon go back to normal.
    func panelClosed() {
        if case .error = state { state = .stopped; errorLocation = nil }
    }

    /// For `preview-panel` only.
    func preview(tvs: [Renderer], error: String?) {
        renderers = tvs
        lastSearchAt = Date()
        if let error, let first = tvs.first { state = .error(error); errorLocation = first.location }
    }

    func search() {
        guard !searching else { return }
        searching = true
        lastSearchAt = Date()
        Task {
            // Each TV appears as soon as it answers; the list is settled when the search ends.
            let found = await Discovery.renderers { [weak self] r in
                await MainActor.run {
                    guard let self, !self.renderers.contains(where: { $0.location == r.location }) else { return }
                    self.renderers = self.ordered(self.renderers + [r])
                }
            }
            networkBlocked = SSDP.localNetworkBlocked
            var list = found
            // A TV in use that missed this search (busy) stays listed.
            if let a = activeLocation, !list.contains(where: { $0.location == a }),
               let tv = renderers.first(where: { $0.location == a }) { list.append(tv) }
            renderers = ordered(list)
            searching = false
            Log.info("Found \(found.count) TV(s)")
            // Until Local Network access is granted, keep looking so the TV
            // appears by itself once the user clicks Allow.
            if found.isEmpty, networkBlocked, searchRetries < 60 {
                searchRetries += 1
                try? await Task.sleep(nanoseconds: 3_000_000_000)
                search()
            } else {
                searchRetries = 0
            }
        }
    }

    private func ordered(_ list: [Renderer]) -> [Renderer] {
        list.sorted { a, b in
            if (a.location == lastLocation) != (b.location == lastLocation) { return a.location == lastLocation }
            return a.name.localizedStandardCompare(b.name) == .orderedAscending
        }
    }

    func stopForQuit() async { await session.stop() }

    // MARK: permissions

    private static let screenSettings = "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture"
    private static let networkSettings = "x-apple.systempreferences:com.apple.preference.security?Privacy_LocalNetwork"

    private func requestPermissions() {
        LocalNetworkPrompt.trigger()
        if !screenOK { askForScreenRecording() }
    }

    /// Makes macOS register TylerCast for Screen Recording, so it is listed in
    /// Settings (after a reset it isn't, until the app asks again).
    private func askForScreenRecording() {
        Log.info("Asking for Screen Recording permission")
        if !CGRequestScreenCaptureAccess() {
            Log.warn("No Screen Recording permission yet: allow TylerCast in System Settings, then reopen it")
        }
        Task.detached { _ = try? await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true) }
    }

    func openScreenSettings() {
        askForScreenRecording()
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { NSWorkspace.shared.open(URL(string: Self.screenSettings)!) }
    }

    func openNetworkSettings() { NSWorkspace.shared.open(URL(string: Self.networkSettings)!) }

    func relaunch() {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/sh")
        p.arguments = ["-c", "sleep 1; open \"\(Bundle.main.bundlePath)\""]
        try? p.run()
        NSApp.terminate(nil)
    }

    func showLog() { NSWorkspace.shared.open(Log.fileURL) }
}

extension Renderer {
    /// "Living_Room_TV" reads better as "Living Room TV".
    var displayName: String { name.replacingOccurrences(of: "_", with: " ") }
}
