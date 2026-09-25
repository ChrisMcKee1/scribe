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
            path: "Sources/Scribe",
            resources: [
                .copy("Resources/Libraries")
            ]
        ),
        .testTarget(
            name: "ScribeTests",
            dependencies: ["Scribe"],
            path: "Tests/ScribeTests"
        ),
        // Headless scenarios on the committed speech fixtures (tests/fixtures/speech) through the production capture,
        // silence, pipeline and storage code. A target of its own, so `swift test --filter ScribeScenarioTests` runs
        // them alone, as the workflow's optional real speech recognition job does.
        .testTarget(
            name: "ScribeScenarioTests",
            dependencies: ["Scribe"],
            path: "Tests/ScribeScenarioTests"
        ),
    ]
)
