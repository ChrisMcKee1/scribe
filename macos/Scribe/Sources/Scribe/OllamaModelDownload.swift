import Foundation

/// A model name the download form accepted, or why it did not.
struct OllamaDownloadRequest: Sendable, Equatable {
    let model: String?
    let error: String?
}

enum OllamaDownloadStage: Sendable, Equatable {
    case preparing
    case downloading
    case verifying
    case installing
    case completed
}

/// One progress line of Ollama's `/api/pull`, as Scribe shows it. `percent` is set only while a file with a known
/// size downloads.
struct OllamaDownloadProgress: Sendable, Equatable {
    let stage: OllamaDownloadStage
    var completedBytes: Int64 = 0
    var totalBytes: Int64 = 0
    var percent: Double?
}

/// Why a download did not finish. No case carries what the server or the user wrote: the one server message Scribe
/// acts on, Ollama's missing manifest, is recognized exactly and kept as a flag.
enum OllamaDownloadError: Error, Sendable, Equatable {
    /// The name was refused before anything was sent.
    case invalidName
    /// The address is not Ollama's own on this Mac, so nothing was sent.
    case notOllama
    /// This downloader already runs a download.
    case alreadyDownloading
    /// Ollama does not answer on its address.
    case unreachable
    /// The download ran past `OllamaModelDownloader.overallTimeout`.
    case timedOut
    /// Ollama answered the request with this HTTP status.
    case httpStatus(Int)
    /// Ollama reported an error in its progress stream.
    case serverError(missingManifest: Bool)
    /// A line of the answer was not a progress object.
    case unreadableAnswer
    /// The answer ended without Ollama confirming success.
    case notConfirmed
    /// The connection failed some other way.
    case interrupted
}

/// The download form's validation and notices, ported from Windows' `OllamaModelDownload`.
enum OllamaModelDownload {
    static let catalogURL = URL(string: "https://ollama.com/search")!
    static let maximumNameLength = 200
    /// Ollama's own wording when the name or tag does not exist. Only this exact text gets the missing-model notice.
    static let missingManifestMessage = "pull model manifest: file does not exist"

    static let downloadHint =
        "Enter a model name, such as deepseek-r1 or VicRodger27/Writex:4b, or paste its ollama run or ollama pull "
        + "command. Keep the publisher, name and tag together. A name without a tag asks for latest, which some "
        + "publishers don't offer. Downloading uses the internet and can take several GB of disk space."
    static let preparing = "Ollama is preparing the download..."
    static let canceled =
        "Download canceled. Ollama keeps the downloaded parts so you can resume by choosing Download again."
    static let saved =
        "Model choice saved. Check the AI cleanup status above to see when it's ready. Other unsaved changes stay "
        + "unsaved."
    static let declined = "Downloaded. Your model choice hasn't changed."
    static let missingModel =
        "Ollama couldn't find that model and tag. Copy the exact name and tag from the catalog's run command; some "
        + "publishers don't offer latest."
    static let couldNotFinish =
        "Ollama couldn't finish the download. Check that it's open and you have enough disk space, then try again."

    private static let commands = ["ollama run ", "ollama pull "]

    static func validate(_ input: String?) -> OllamaDownloadRequest {
        guard var model = input?.trimmingCharacters(in: .whitespacesAndNewlines), !model.isEmpty else {
            return OllamaDownloadRequest(model: nil, error: "Enter the name of the model you want to download.")
        }

        for command in commands {
            if let rest = removingASCIIPrefix(command, from: model) {
                model = rest.trimmingCharacters(in: .whitespacesAndNewlines)
                break
            }
        }

        guard model.utf16.count <= maximumNameLength, isModelName(model) else {
            return OllamaDownloadRequest(
                model: nil,
                error:
                    "Enter a model name or its ollama run command, without a web address, extra options or "
                    + "instructions.")
        }

        if let colon = model.lastIndex(of: ":"), model[model.index(after: colon)...].lowercased().hasSuffix("cloud") {
            return OllamaDownloadRequest(
                model: nil,
                error: "Choose a model that runs on this Mac. Ollama cloud models aren't supported here.")
        }

        return OllamaDownloadRequest(model: model, error: nil)
    }

    static func describe(_ progress: OllamaDownloadProgress) -> String {
        switch progress.stage {
        case .downloading where progress.totalBytes > 0:
            let completed =
                progress.completedBytes <= 0 ? "0 MB" : LocalAppSetup.formatSize(progress.completedBytes)
            let percent = Int((progress.percent ?? 0).rounded())
            return
                "Downloading a model file: \(completed) of \(LocalAppSetup.formatSize(progress.totalBytes)) "
                + "(\(percent)%)."
        case .verifying:
            return "Checking the downloaded files..."
        case .installing:
            return "Adding the model to Ollama..."
        case .completed:
            return "Download complete."
        case .preparing, .downloading:
            return preparing
        }
    }

    /// The notice for a download that failed. It never quotes the error, the server or the name.
    static func failure(_ error: any Error) -> String {
        guard let error = error as? OllamaDownloadError else {
            return couldNotFinish
        }
        switch error {
        case .timedOut:
            return "The download timed out. Choose Download to resume it."
        case .httpStatus(404), .serverError(missingManifest: true):
            return missingModel
        case .httpStatus(401), .httpStatus(403):
            return "Ollama couldn't access that model. Check it in the catalog and sign in to Ollama if needed."
        case .unreachable, .notOllama:
            return "Scribe can't reach Ollama. Open the Ollama app, then choose Download again."
        case .invalidName:
            return "Enter a model name or its ollama run command, without a web address, extra options or "
                + "instructions."
        case .alreadyDownloading, .httpStatus, .serverError, .unreadableAnswer, .notConfirmed, .interrupted:
            return couldNotFinish
        }
    }

    static func switchQuestion(_ model: String) -> String {
        "Use \(model) for AI cleanup? This saves the model choice immediately. Other unsaved changes stay unsaved."
    }

    /// Why a finished download cannot be offered as the model choice, judged on the reading the refresh after it
    /// returned rather than on what the page shows, which another Check again can clear meanwhile.
    static func completionProblem(_ reading: LocalServerState?, model: String) -> String? {
        guard let reading, reading.reach == .reached else {
            return "Downloaded, but Scribe couldn't refresh Ollama's model list. Choose Check again."
        }
        return reading.models.contains { LocalServerClient.sameModel($0.id, model) }
            ? nil
            : "Downloaded, but Ollama doesn't list it as a model for AI cleanup. Choose a text model from the catalog."
    }

    /// `[publisher/]name[:tag]`, each part an ASCII letter or digit followed by letters, digits, dots, underscores
    /// or hyphens: Windows' pattern, without its regex.
    private static func isModelName(_ name: String) -> Bool {
        let path = name.split(separator: "/", omittingEmptySubsequences: false)
        guard path.count == 1 || path.count == 2 else {
            return false
        }
        if path.count == 2, !isNamePart(path[0]) {
            return false
        }
        let nameAndTag = path[path.count - 1].split(separator: ":", omittingEmptySubsequences: false)
        guard nameAndTag.count == 1 || nameAndTag.count == 2 else {
            return false
        }
        return nameAndTag.allSatisfy(isNamePart)
    }

    private static func isNamePart(_ part: Substring) -> Bool {
        let bytes = Array(part.utf8)
        guard let first = bytes.first, isASCIILetterOrDigit(first) else {
            return false
        }
        return bytes.dropFirst().allSatisfy { byte in
            isASCIILetterOrDigit(byte) || byte == UInt8(ascii: ".") || byte == UInt8(ascii: "_")
                || byte == UInt8(ascii: "-")
        }
    }

    private static func isASCIILetterOrDigit(_ byte: UInt8) -> Bool {
        (UInt8(ascii: "a")...UInt8(ascii: "z")).contains(byte)
            || (UInt8(ascii: "A")...UInt8(ascii: "Z")).contains(byte)
            || (UInt8(ascii: "0")...UInt8(ascii: "9")).contains(byte)
    }

    /// The text after an ASCII `prefix`, compared without case, or `nil`. Byte by byte, so the split falls on a
    /// character boundary whatever else the text holds.
    private static func removingASCIIPrefix(_ prefix: String, from text: String) -> String? {
        let expected = Array(prefix.utf8)
        let bytes = Array(text.utf8)
        guard bytes.count >= expected.count else {
            return nil
        }
        for (index, byte) in expected.enumerated() where lowercasedASCII(bytes[index]) != byte {
            return nil
        }
        return String(decoding: bytes[expected.count...], as: UTF8.self)
    }

    private static func lowercasedASCII(_ byte: UInt8) -> UInt8 {
        (UInt8(ascii: "A")...UInt8(ascii: "Z")).contains(byte) ? byte + 32 : byte
    }
}
