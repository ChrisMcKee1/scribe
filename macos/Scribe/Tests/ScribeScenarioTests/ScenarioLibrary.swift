import Foundation
import XCTest

/// One committed speech fixture from `tests/fixtures/speech`, decoded to 16 kHz mono.
struct ScenarioClip: Sendable {
    /// The manifest key, such as `dict-azure-devops`.
    let name: String
    let file: String
    /// What the voice says, exactly as the manifest records it.
    let text: String
    /// `smoke` for the phrases of `fixtures.json`; `dictionary`, `snippet`, `speech` or `numbers` for the scenario
    /// phrases of `scenario-fixtures.json`.
    let role: String
    /// Whether a real recognizer is held to the word-overlap bar on this phrase. The smoke phrases always are; the
    /// numbers phrase is not, because how numbers are written is not a recognizer contract.
    let asserted: Bool
    /// The length `scenario-fixtures.json` records, rounded to hundredths of a second; nil for the smoke phrases.
    let manifestSeconds: Double?
    let sampleRate: Int
    /// The 16-bit samples exactly as the file stores them.
    let pcm: [Int16]
    /// `pcm` in -1...1, divided by 32,768 the way the capture reads 16-bit input.
    let samples: [Float]

    var seconds: Double { Double(pcm.count) / Double(sampleRate) }
}

/// The committed fixtures: the smoke phrases of `fixtures.json` and the scenario phrases of `scenario-fixtures.json`.
/// They are the files the Windows scenario suite reads (`tools/Scribe.AsrCheck`), which keeps the two manifests apart
/// so that its smoke check gates on the smoke phrases alone; this suite reads both.
struct ScenarioLibrary: Sendable {
    static let smokeManifest = "fixtures.json"
    static let scenarioManifest = "scenario-fixtures.json"
    static let fixtureRate = 16_000

    let directory: URL
    /// Every clip, sorted by name.
    let clips: [ScenarioClip]

    func clip(_ name: String) throws -> ScenarioClip {
        guard let clip = clips.first(where: { $0.name == name }) else {
            throw ScenarioFixtureError.missingClip(name)
        }
        return clip
    }

    /// The library, read once per test process. Throws `XCTSkip` when the fixtures are not there.
    static func shared() throws -> ScenarioLibrary {
        try cached.get()
    }

    private static let cached = Result<ScenarioLibrary, any Error> { try load() }

    /// `SCRIBE_FIXTURES_DIR` when it is set, otherwise the repository's `tests/fixtures/speech`, found from this file.
    static var defaultDirectory: URL {
        if let override = ProcessInfo.processInfo.environment["SCRIBE_FIXTURES_DIR"], !override.isEmpty {
            return URL(fileURLWithPath: override, isDirectory: true)
        }
        // This file is macos/Scribe/Tests/ScribeScenarioTests/ScenarioLibrary.swift in the repository.
        var root = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 {
            root = root.deletingLastPathComponent()
        }
        return root.appendingPathComponent("tests/fixtures/speech", isDirectory: true)
    }

    /// Reads both manifests and every WAV they name. A copy of the package without the fixtures skips the scenarios
    /// (`XCTSkip`) instead of failing them; a fixture that is there but malformed fails them.
    static func load(from directory: URL = ScenarioLibrary.defaultDirectory) throws -> ScenarioLibrary {
        let smoke = directory.appendingPathComponent(smokeManifest)
        let scenario = directory.appendingPathComponent(scenarioManifest)
        guard FileManager.default.fileExists(atPath: smoke.path(percentEncoded: false)),
            FileManager.default.fileExists(atPath: scenario.path(percentEncoded: false))
        else {
            throw XCTSkip(
                "The speech fixtures are not in \(directory.path(percentEncoded: false)); "
                    + "set SCRIBE_FIXTURES_DIR to the folder holding \(smokeManifest) and \(scenarioManifest).")
        }
        let clips =
            try readManifest(smoke, in: directory, smoke: true)
            + readManifest(scenario, in: directory, smoke: false)
        return ScenarioLibrary(directory: directory, clips: clips.sorted { $0.name < $1.name })
    }

    private struct ManifestEntry: Decodable {
        let file: String
        let text: String
        let role: String?
        let asserted: Bool?
        let seconds: Double?
    }

    private static func readManifest(_ url: URL, in directory: URL, smoke: Bool) throws -> [ScenarioClip] {
        let entries = try JSONDecoder().decode([String: ManifestEntry].self, from: Data(contentsOf: url))
        return try entries.map { name, entry in
            let wav = try ScenarioWav.read(directory.appendingPathComponent(entry.file))
            guard wav.sampleRate == fixtureRate, wav.channels == 1, case .pcm16(let pcm) = wav.samples else {
                throw ScenarioFixtureError.unexpectedFormat(entry.file)
            }
            guard smoke || (entry.role != nil && entry.asserted != nil && entry.seconds != nil) else {
                throw ScenarioFixtureError.incompleteEntry(name)
            }
            return ScenarioClip(
                name: name,
                file: entry.file,
                text: entry.text,
                role: smoke ? "smoke" : entry.role ?? "",
                asserted: smoke || entry.asserted == true,
                manifestSeconds: smoke ? nil : entry.seconds,
                sampleRate: wav.sampleRate,
                pcm: pcm,
                samples: pcm.map { Float($0) / 32_768 })
        }
    }
}

enum ScenarioFixtureError: Error, CustomStringConvertible {
    case missingClip(String)
    case unexpectedFormat(String)
    case incompleteEntry(String)
    case malformedWav(String, reason: String)

    var description: String {
        switch self {
        case .missingClip(let name):
            return "No fixture named \(name)."
        case .unexpectedFormat(let file):
            return "\(file) is not 16 kHz mono 16-bit PCM, which every committed fixture is."
        case .incompleteEntry(let name):
            return "The scenario manifest entry \(name) lacks its role, asserted flag or seconds."
        case .malformedWav(let file, let reason):
            return "\(file) is not a WAV this suite can read: \(reason)."
        }
    }
}

/// A RIFF WAVE file as the fixtures and Scribe's own scratch recordings store one: 16-bit PCM or 32-bit float, any
/// channel count, samples interleaved. The chunk walk steps over chunks it does not know, as the Windows reader does.
struct ScenarioWav: Sendable {
    enum Samples: Sendable {
        case pcm16([Int16])
        case float32([Float])
    }

    let sampleRate: Int
    let channels: Int
    let samples: Samples

    static func read(_ url: URL) throws -> ScenarioWav {
        try parse(Data(contentsOf: url), name: url.lastPathComponent)
    }

    static func parse(_ data: Data, name: String) throws -> ScenarioWav {
        let bytes = [UInt8](data)
        func fail(_ reason: String) -> ScenarioFixtureError {
            .malformedWav(name, reason: reason)
        }
        guard bytes.count >= 12, fourCC(bytes, at: 0) == "RIFF", fourCC(bytes, at: 8) == "WAVE" else {
            throw fail("not a RIFF WAVE file")
        }

        var offset = 12
        var format: (tag: UInt16, channels: Int, sampleRate: Int, bits: Int)?
        while offset + 8 <= bytes.count {
            let id = fourCC(bytes, at: offset)
            let size = Int(uint32(bytes, at: offset + 4))
            let body = offset + 8
            guard size <= bytes.count - body else {
                throw fail("its \(id) chunk runs past the end of the file")
            }
            switch id {
            case "fmt ":
                guard size >= 16 else { throw fail("its fmt chunk is \(size) bytes") }
                var tag = uint16(bytes, at: body)
                // WAVE_FORMAT_EXTENSIBLE names the real format in the first two bytes of its sub-format GUID.
                if tag == 0xFFFE, size >= 26 {
                    tag = uint16(bytes, at: body + 24)
                }
                format = (
                    tag: tag,
                    channels: Int(uint16(bytes, at: body + 2)),
                    sampleRate: Int(uint32(bytes, at: body + 4)),
                    bits: Int(uint16(bytes, at: body + 14))
                )
            case "data":
                guard let format else { throw fail("its data chunk comes before its fmt chunk") }
                guard format.channels > 0, format.sampleRate > 0 else { throw fail("it has no channels or no rate") }
                switch (format.tag, format.bits) {
                case (1, 16):
                    let values = (0..<(size / 2)).map { Int16(bitPattern: uint16(bytes, at: body + $0 * 2)) }
                    return ScenarioWav(
                        sampleRate: format.sampleRate, channels: format.channels, samples: .pcm16(values))
                case (3, 32):
                    let values = (0..<(size / 4)).map { Float(bitPattern: uint32(bytes, at: body + $0 * 4)) }
                    return ScenarioWav(
                        sampleRate: format.sampleRate, channels: format.channels, samples: .float32(values))
                default:
                    throw fail("it holds format \(format.tag) at \(format.bits) bits")
                }
            default:
                break
            }
            // A chunk of odd size is followed by one pad byte.
            offset = body + size + (size % 2)
        }
        throw fail("it has no data chunk")
    }

    private static func fourCC(_ bytes: [UInt8], at offset: Int) -> String {
        String(decoding: bytes[offset..<(offset + 4)], as: UTF8.self)
    }

    private static func uint16(_ bytes: [UInt8], at offset: Int) -> UInt16 {
        UInt16(bytes[offset]) | UInt16(bytes[offset + 1]) << 8
    }

    private static func uint32(_ bytes: [UInt8], at offset: Int) -> UInt32 {
        let low = UInt32(bytes[offset]) | UInt32(bytes[offset + 1]) << 8
        let high = UInt32(bytes[offset + 2]) << 16 | UInt32(bytes[offset + 3]) << 24
        return low | high
    }
}
