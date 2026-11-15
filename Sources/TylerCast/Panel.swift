import MirrorCore
import SwiftUI

/// The menu bar panel, styled like the Wi-Fi panel: a row per TV found on
/// this network (click to mirror), then settings, then plain menu rows.
struct Panel: View {
    @ObservedObject var model: AppModel
    /// Holding Option turns "Show Log…" into the test-pattern switch (the
    /// way Option reveals extra items in the Wi-Fi panel), without resizing.
    @StateObject private var keys = OptionKey()

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            header
            separator
            permissionCallouts
            sectionLabel("TVs")
            tvList
            separator
            marginRow
            switchRow("Sound on TV", isOn: $model.soundOn,
                      help: "Send the Mac's sound to the TV. Changes apply the next time you start mirroring.")
            switchRow("Mute Mac", isOn: $model.tvOnlySound, enabled: model.soundOn,
                      help: "Play the sound on the TV only; the Mac's speakers come back when mirroring stops.")
            loginRow
            separator
            if keys.held {
                MenuRow(model.testPattern ? "✓ Send Test Pattern Instead of Screen" : "Send Test Pattern Instead of Screen") {
                    model.testPattern.toggle()
                }
                .disabled(model.isActive)
            } else {
                MenuRow("Show Log…") { model.showLog() }
            }
            MenuRow("Quit TylerCast") { NSApp.terminate(nil) }.keyboardShortcut("q")
        }
        .padding(.horizontal, 6)
        .padding(.vertical, 8)
        .frame(width: 300)
        .onAppear {
            model.searchIfStale()
            keys.start()
        }
        .onDisappear {
            model.panelClosed()
            keys.stop()
        }
    }

    // MARK: pieces in the Wi-Fi panel's style

    private var separator: some View {
        Divider().padding(.horizontal, 8).padding(.vertical, 5)
    }

    private func sectionLabel(_ text: String) -> some View {
        HStack {
            Text(text).font(.system(size: 12, weight: .semibold)).foregroundStyle(.secondary)
            Spacer()
            if model.searching {
                ProgressView().controlSize(.small).scaleEffect(0.8).frame(width: 18, height: 18).help("Searching for TVs…")
            } else {
                Button { model.search() } label: {
                    Image(systemName: "arrow.clockwise").font(.system(size: 11, weight: .semibold)).frame(width: 18, height: 18)
                }
                .buttonStyle(.borderless).foregroundStyle(.secondary).help("Search for TVs")
            }
        }
        .padding(.horizontal, 8).padding(.top, 2).padding(.bottom, 4)
    }

    private var header: some View {
        Text("TylerCast").font(.system(size: 13, weight: .bold))
            .padding(.horizontal, 8).padding(.vertical, 6)
    }

    // MARK: TVs

    @ViewBuilder private var tvList: some View {
        if model.renderers.isEmpty {
            // Same height as a TV row, so the first TV to answer replaces it in place.
            Text(model.searching ? "Looking for TVs…" : "No TVs on this network")
                .foregroundStyle(.secondary)
                .frame(maxWidth: .infinity, minHeight: 34, alignment: .leading)
                .padding(.horizontal, 8).padding(.vertical, 1)
                .help("TVs appear here when they're switched on and on the same network as this Mac")
        }
        ForEach(model.renderers) { tv in
            TVRow(tv: tv, model: model)
        }
    }

    static func elapsed(_ from: Date, _ to: Date) -> String {
        let s = max(0, Int(to.timeIntervalSince(from)))
        return s >= 3600 ? String(format: "%d:%02d:%02d", s / 3600, s / 60 % 60, s % 60) : String(format: "%d:%02d", s / 60, s % 60)
    }

    // MARK: permissions

    @ViewBuilder private var permissionCallouts: some View {
        if !model.screenOK {
            callout("Screen Recording isn't allowed yet.", button: "Allow…", action: model.openScreenSettings,
                    second: ("Reopen", model.relaunch))
        }
        if model.networkBlocked {
            callout("Local Network access is needed to find TVs.", button: "Allow…", action: model.openNetworkSettings)
        }
    }

    private func callout(_ text: String, button: String, action: @escaping () -> Void,
                         second: (String, () -> Void)? = nil) -> some View {
        HStack(alignment: .top, spacing: 8) {
            Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange)
            Text(text).fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 4)
            VStack(alignment: .trailing, spacing: 4) {
                Button(button, action: action).controlSize(.small)
                if let second { Button(second.0, action: second.1).controlSize(.small) }
            }
        }
        .padding(8)
        .background(RoundedRectangle(cornerRadius: 8).fill(Color.orange.opacity(0.12)))
        .padding(.horizontal, 2).padding(.bottom, 8)
    }

    // MARK: settings

    private var marginRow: some View {
        HStack(spacing: 6) {
            Text("Margin")
            Spacer()
            stepButton("minus", -1).disabled(model.margin <= AppModel.marginRange.lowerBound)
            Text("\(Int(model.margin))%").monospacedDigit().foregroundStyle(.secondary).frame(width: 32)
            stepButton("plus", 1).disabled(model.margin >= AppModel.marginRange.upperBound)
        }
        .frame(minHeight: 30)
        .padding(.horizontal, 8)
        .help("Shrinks the picture if the TV cuts off the edges")
    }

    private func stepButton(_ symbol: String, _ delta: Double) -> some View {
        Button { model.stepMargin(delta) } label: {
            Image(systemName: symbol).font(.system(size: 10, weight: .bold))
                .frame(width: 22, height: 22)
                .background(Circle().fill(Color.primary.opacity(0.08)))
        }
        .buttonStyle(.plain)
    }

    /// A settings row with a small switch, like Open at Login. Sound settings
    /// apply when mirroring starts, so they're locked while it runs.
    private func switchRow(_ title: String, isOn: Binding<Bool>, enabled: Bool = true, help: String) -> some View {
        let usable = enabled && !model.isActive
        return HStack {
            Text(title).foregroundStyle(usable ? .primary : .secondary)
            Spacer()
            Toggle(title, isOn: isOn).toggleStyle(.switch).controlSize(.mini).labelsHidden().disabled(!usable)
        }
        .frame(minHeight: 30)
        .padding(.horizontal, 8)
        .help(model.isActive ? help + " (Stop mirroring to change it.)" : help)
    }

    private var loginRow: some View {
        VStack(alignment: .leading, spacing: 2) {
            HStack {
                Text("Open at Login")
                Spacer()
                Toggle("Open at Login", isOn: Binding(get: { model.openAtLogin }, set: { model.setOpenAtLogin($0) }))
                    .toggleStyle(.switch).controlSize(.mini).labelsHidden()
            }
            if let e = model.loginItemError { Text(e).font(.caption).foregroundStyle(.orange) }
        }
        .frame(minHeight: 30)
        .padding(.horizontal, 8)
    }
}

/// A plain text row that highlights on hover, like "Wi-Fi Settings…".
private struct MenuRow: View {
    let title: String
    let action: () -> Void
    @StateObject private var hover = Flag()
    @Environment(\.isEnabled) private var enabled
    init(_ title: String, action: @escaping () -> Void) { self.title = title; self.action = action }

    var body: some View {
        Button(action: action) {
            Text(title).lineLimit(1)
                .foregroundStyle(enabled ? .primary : .secondary)
                .frame(maxWidth: .infinity, minHeight: 26, alignment: .leading)
                .padding(.horizontal, 8)
                .contentShape(Rectangle())
                .background(RoundedRectangle(cornerRadius: 6).fill(hover.on && enabled ? Color.primary.opacity(0.1) : .clear))
        }
        .buttonStyle(.plain)
        .onHover { hover.on = $0 }
    }
}

/// One TV: round icon (blue while mirroring), name, status on the right.
/// Click to mirror; click again to stop.
private struct TVRow: View {
    let tv: Renderer
    @ObservedObject var model: AppModel
    @StateObject private var hover = Flag()

    private var active: Bool { model.activeLocation == tv.location }

    var body: some View {
        Button { model.setMirroring(tv, !active) } label: {
            HStack(spacing: 10) {
                Image(systemName: "tv.fill")
                    .font(.system(size: 12, weight: .semibold))
                    .frame(width: 26, height: 26)
                    .background(Circle().fill(active ? Color.accentColor : Color.primary.opacity(0.1)))
                    .foregroundStyle(active ? Color.white : Color.primary)
                VStack(alignment: .leading, spacing: 0) {
                    Text(tv.displayName).lineLimit(1)
                    if model.errorLocation == tv.location, let why = model.errorText {
                        Text(why).font(.caption).foregroundStyle(.orange).lineLimit(1).help(why)
                    }
                }
                Spacer(minLength: 6)
                status.lineLimit(1)
            }
            .frame(minHeight: 34)
            .padding(.horizontal, 8).padding(.vertical, 1)
            .contentShape(Rectangle())
            .background(RoundedRectangle(cornerRadius: 6).fill(hover.on ? Color.primary.opacity(0.1) : .clear))
        }
        .buttonStyle(.plain)
        .onHover { hover.on = $0 }
        .help(active ? "Stop mirroring to \(tv.displayName)" : "Mirror to \(tv.displayName) (\(tv.ip))")
        .accessibilityAddTraits(active ? .isSelected : [])
    }

    @ViewBuilder private var status: some View {
        if model.errorLocation == tv.location, model.errorText != nil {
            Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange)
        } else if active, model.isPlaying, let since = model.startedAt {
            TimelineView(.periodic(from: since, by: 1)) { ctx in
                Text((model.testPattern ? "Test pattern " : "") + Panel.elapsed(since, ctx.date))
                    .foregroundStyle(.secondary).monospacedDigit()
            }
        } else if active {
            ProgressView().controlSize(.small).help("Connecting…")
        }
    }
}

// View-local state lives in small ObservableObjects rather than @State: in the
// macOS 26+ SDK @State is a macro whose plugin ships only with Xcode, so the
// Command Line Tools can't build it.

/// A hover (or any other) flag for one view.
final class Flag: ObservableObject { @Published var on = false }

/// Whether Option is held while the panel is open.
@MainActor final class OptionKey: ObservableObject {
    @Published private(set) var held = false
    private var monitor: Any?

    func start() {
        held = NSEvent.modifierFlags.contains(.option)
        guard monitor == nil else { return }
        monitor = NSEvent.addLocalMonitorForEvents(matching: .flagsChanged) { [weak self] e in
            self?.held = e.modifierFlags.contains(.option)
            return e
        }
    }

    func stop() {
        if let monitor { NSEvent.removeMonitor(monitor) }
        monitor = nil
    }
}
