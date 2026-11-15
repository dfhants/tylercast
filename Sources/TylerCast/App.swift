import MirrorCore
import AppKit
import SwiftUI

/// The menu bar app: a TylerCast icon that opens a Control Center-style panel.
struct TylerCastApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate
    @StateObject private var model = AppDelegate.model

    var body: some Scene {
        MenuBarExtra {
            Panel(model: model)
        } label: {
            Image(nsImage: TylerIcon.menuBar(model.glyph))
                .accessibilityLabel("TylerCast")
        }
        .menuBarExtraStyle(.window)
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    @MainActor static let model = AppModel()

    func applicationWillFinishLaunching(_ note: Notification) {
        NSApp.setActivationPolicy(.accessory)  // menu bar only, also when run outside the bundle
    }

    /// `--dump-windows` (for checking the panel's look): each time the panel
    /// opens, logs its window and views.
    @MainActor func applicationDidFinishLaunching(_ note: Notification) {
        guard CommandLine.arguments.contains("--dump-windows") else { return }
        var shown = false
        Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { _ in
            MainActor.assumeIsolated {
                let panels = NSApp.windows.filter { $0.isVisible && !String(describing: type(of: $0)).contains("StatusBar") }
                defer { shown = !panels.isEmpty }
                guard !shown else { return }
                for w in panels { Self.dump(w) }
            }
        }
    }

    @MainActor static func dump(_ w: NSWindow) {
        Log.info("Window \(type(of: w)) frame \(w.frame) opaque \(w.isOpaque) bg \(w.backgroundColor) shadow \(w.hasShadow)")
        func dump(_ v: NSView, _ depth: Int) {
            var extra = ""
            if let e = v as? NSVisualEffectView { extra = " material \(e.material.rawValue) blending \(e.blendingMode.rawValue) state \(e.state.rawValue)" }
            if v.responds(to: NSSelectorFromString("cornerRadius")), let r = v.value(forKey: "cornerRadius") as? CGFloat { extra += " glassRadius \(r)" }
            if let l = v.layer { extra += " radius \(l.cornerRadius) bg \(l.backgroundColor.map { "\($0.components ?? [])" } ?? "nil")" }
            Log.info(String(repeating: "  ", count: depth) + "\(type(of: v)) \(v.frame)" + extra)
            if depth < 5 { v.subviews.forEach { dump($0, depth + 1) } }
        }
        if let c = w.contentView { dump(c, 1) }
    }

    @MainActor func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        guard Self.model.isActive else { return .terminateNow }
        Task { await Self.model.stopForQuit(); sender.reply(toApplicationShouldTerminate: true) }
        return .terminateLater
    }
}
