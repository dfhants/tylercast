// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "TylerCast",
    platforms: [.macOS(.v14)],
    products: [.executable(name: "TylerCast", targets: ["TylerCast"])],
    targets: [
        // Everything but the UI: discovery, SOAP, HTTP server, TS muxer, capture, encoding.
        .target(name: "MirrorCore"),
        // The menu bar app, plus `discover` / `play` / `record` command-line modes for debugging.
        .executableTarget(name: "TylerCast", dependencies: ["MirrorCore"]),
        .testTarget(name: "MirrorCoreTests", dependencies: ["MirrorCore"]),
    ]
)
