// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "ScribeMac",
    platforms: [
        .macOS(.v13)
    ],
    products: [
        .executable(name: "Scribe", targets: ["Scribe"])
    ],
    targets: [
        .executableTarget(
            name: "Scribe",
            path: "Sources/Scribe"
        ),
        .testTarget(
            name: "ScribeTests",
            dependencies: ["Scribe"],
            path: "Tests/ScribeTests"
        )
    ]
)
