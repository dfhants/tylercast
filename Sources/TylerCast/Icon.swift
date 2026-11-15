import AppKit

/// TylerCast's icon: a screen on a stand with a T on it. Drawn in code so the
/// menu bar glyph and the app icon come from one design at any size.
enum TylerIcon {
    enum Glyph { case idle, connecting, playing, error }

    /// 18pt template image for the menu bar (macOS tints it for light/dark).
    static func menuBar(_ g: Glyph) -> NSImage {
        let img = NSImage(size: NSSize(width: 18, height: 18), flipped: false) { _ in
            let screen = NSBezierPath(roundedRect: NSRect(x: 1.25, y: 4.75, width: 15.5, height: 11), xRadius: 2.2, yRadius: 2.2)
            // Dim as one layer, so overlapping shapes don't double up.
            let cg = NSGraphicsContext.current!.cgContext
            cg.setAlpha(g == .connecting ? 0.5 : 1)
            cg.beginTransparencyLayer(auxiliaryInfo: nil)
            defer { cg.endTransparencyLayer() }
            NSColor.black.set()
            // stand: neck and foot
            NSBezierPath(rect: NSRect(x: 8.25, y: 2.6, width: 1.5, height: 2.4)).fill()
            NSBezierPath(roundedRect: NSRect(x: 5.5, y: 1.6, width: 7, height: 1.4), xRadius: 0.7, yRadius: 0.7).fill()
            let mark: NSBezierPath = g == .error ? bang() : letterT()
            if g == .playing {
                screen.fill()
                NSGraphicsContext.current?.compositingOperation = .destinationOut
                mark.fill()
                NSGraphicsContext.current?.compositingOperation = .sourceOver
            } else {
                screen.lineWidth = 1.5
                screen.stroke()
                mark.fill()
            }
            return true
        }
        img.isTemplate = true
        img.accessibilityDescription = "TylerCast"
        return img
    }

    private static func letterT() -> NSBezierPath {
        let p = NSBezierPath(roundedRect: NSRect(x: 5.0, y: 11.6, width: 8, height: 1.9), xRadius: 0.5, yRadius: 0.5)
        p.append(NSBezierPath(roundedRect: NSRect(x: 8.05, y: 6.9, width: 1.9, height: 5.6), xRadius: 0.5, yRadius: 0.5))
        return p
    }

    private static func bang() -> NSBezierPath {
        let p = NSBezierPath(roundedRect: NSRect(x: 8.1, y: 9.4, width: 1.8, height: 4.6), xRadius: 0.6, yRadius: 0.6)
        p.append(NSBezierPath(ovalIn: NSRect(x: 8.05, y: 6.7, width: 1.9, height: 1.9)))
        return p
    }

    /// Full-colour app icon, `px` pixels square (macOS icon grid: the tile
    /// fills the middle 824/1024 with a ~185/1024 corner radius).
    static func appIconPNG(px: Int) -> Data? {
        guard let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: px, pixelsHigh: px, bitsPerSample: 8,
                                         samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                         colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0) else { return nil }
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
        let s = CGFloat(px) / 1024
        func r(_ x: CGFloat, _ y: CGFloat, _ w: CGFloat, _ h: CGFloat) -> NSRect { NSRect(x: x * s, y: y * s, width: w * s, height: h * s) }

        // Tile: deep blue to violet.
        let tile = NSBezierPath(roundedRect: r(100, 100, 824, 824), xRadius: 185 * s, yRadius: 185 * s)
        NSGradient(starting: NSColor(srgbRed: 0.16, green: 0.36, blue: 0.95, alpha: 1),
                   ending: NSColor(srgbRed: 0.42, green: 0.20, blue: 0.82, alpha: 1))?.draw(in: tile, angle: -60)

        // Stand.
        NSColor.white.set()
        NSBezierPath(roundedRect: r(482, 262, 60, 90), xRadius: 8 * s, yRadius: 8 * s).fill()
        NSBezierPath(roundedRect: r(372, 232, 280, 52), xRadius: 26 * s, yRadius: 26 * s).fill()

        // Screen: white bezel, dark glass.
        NSBezierPath(roundedRect: r(196, 330, 632, 432), xRadius: 56 * s, yRadius: 56 * s).fill()
        let glass = NSBezierPath(roundedRect: r(226, 360, 572, 372), xRadius: 32 * s, yRadius: 32 * s)
        NSGradient(starting: NSColor(srgbRed: 0.07, green: 0.11, blue: 0.27, alpha: 1),
                   ending: NSColor(srgbRed: 0.15, green: 0.24, blue: 0.52, alpha: 1))?.draw(in: glass, angle: 90)

        // The T.
        NSColor.white.set()
        NSBezierPath(roundedRect: r(362, 610, 300, 70), xRadius: 18 * s, yRadius: 18 * s).fill()
        NSBezierPath(roundedRect: r(477, 405, 70, 270), xRadius: 18 * s, yRadius: 18 * s).fill()

        NSGraphicsContext.restoreGraphicsState()
        return rep.representation(using: .png, properties: [:])
    }
}
