// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "ClipSync",
    platforms: [
        .macOS(.v14)
    ],
    products: [
        .executable(name: "ClipSync", targets: ["ClipSync"])
    ],
    targets: [
        .executableTarget(
            name: "ClipSync",
            path: "Sources"
        )
    ]
)
