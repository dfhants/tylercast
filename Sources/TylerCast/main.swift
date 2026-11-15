import AppKit
import MirrorCore
import SwiftUI

let argv = Array(CommandLine.arguments.dropFirst())
if argv.first == "preview-panel", argv.count > 1 {
    // Renders the panel to a PNG, light and dark (for design review; not in help).
    MainActor.assumeIsolated {
        _ = NSApplication.shared
        let model = AppModel()
        // preview-panel <prefix> [none | <error text>]: one TV by default.
        let tv = Renderer(location: "http://192.0.2.175:55000/dmr/ddd.xml", ip: "192.0.2.175",
                          name: "Living_Room_TV", model: "Example Vision", services: [:])
        let extra = argv.count > 2 && !argv[2].isEmpty ? argv[2] : nil
        model.preview(tvs: extra == "none" ? [] : [tv], error: extra == "none" ? nil : extra)
        for (name, appearance) in [("light", NSAppearance.Name.aqua), ("dark", .darkAqua)] {
            let host = NSHostingView(rootView: Panel(model: model))
            host.appearance = NSAppearance(named: appearance)
            host.frame = NSRect(origin: .zero, size: host.fittingSize)
            let win = NSWindow(contentRect: host.frame, styleMask: .borderless, backing: .buffered, defer: false)
            win.appearance = host.appearance
            win.backgroundColor = appearance == .darkAqua ? NSColor(white: 0.17, alpha: 1) : NSColor(white: 0.95, alpha: 1)
            win.contentView = host
            host.layoutSubtreeIfNeeded()
            RunLoop.main.run(until: Date().addingTimeInterval(0.3))
            guard let rep = host.bitmapImageRepForCachingDisplay(in: host.bounds) else { continue }
            host.cacheDisplay(in: host.bounds, to: rep)
            try? rep.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: argv[1] + "-\(name).png"))
        }
    }
    exit(0)
} else if let cmd = argv.first, CLI.commands.contains(cmd) {
    Task { @MainActor in exit(await CLI.run(argv)) }
    dispatchMain()
} else {
    TylerCastApp.main()
}
