import Foundation

/// Compares dotted version strings such as "0.1.0" or "v0.2.9" component by component as
/// integers, treating a missing trailing component as 0 (so "0.2" == "0.2.0"). A component that
/// isn't a plain integer (e.g. a "-beta" suffix) makes the whole string incomparable, which
/// `UpdateChecker` treats conservatively as "not newer" rather than guessing.
struct SemanticVersion: Comparable {
    let components: [Int]

    init?(_ raw: String) {
        var text = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        if text.hasPrefix("v") || text.hasPrefix("V") {
            text.removeFirst()
        }
        guard !text.isEmpty else { return nil }
        let parts = text.split(separator: ".", omittingEmptySubsequences: false)
        var parsed: [Int] = []
        for part in parts {
            guard let value = Int(part) else { return nil }
            parsed.append(value)
        }
        guard !parsed.isEmpty else { return nil }
        self.components = parsed
    }

    static func < (lhs: SemanticVersion, rhs: SemanticVersion) -> Bool {
        let count = max(lhs.components.count, rhs.components.count)
        for index in 0..<count {
            let left = index < lhs.components.count ? lhs.components[index] : 0
            let right = index < rhs.components.count ? rhs.components[index] : 0
            if left != right { return left < right }
        }
        return false
    }

    static func == (lhs: SemanticVersion, rhs: SemanticVersion) -> Bool {
        let count = max(lhs.components.count, rhs.components.count)
        for index in 0..<count {
            let left = index < lhs.components.count ? lhs.components[index] : 0
            let right = index < rhs.components.count ? rhs.components[index] : 0
            if left != right { return false }
        }
        return true
    }
}

/// The subset of the GitHub Releases API response `UpdateChecker` needs. GitHub's public
/// `releases/latest` endpoint requires no authentication and is not rate-limit sensitive at the
/// scale of one manual check, so no token handling is needed here.
struct GitHubRelease: Decodable, Equatable {
    let tagName: String
    let htmlURL: URL
    let name: String?

    private enum CodingKeys: String, CodingKey {
        case tagName = "tag_name"
        case htmlURL = "html_url"
        case name
    }
}

enum UpdateCheckResult: Equatable {
    case upToDate(current: String)
    case updateAvailable(current: String, latest: String, url: URL)
    /// Covers network failure, a non-2xx response, an undecodable body, or an unparsable version
    /// string; `message` is meant for a one-line status label, not for logging internals.
    case failed(message: String)
}

/// Fetches the latest GitHub release tag for the Scribe repository and compares it against the
/// running app's `CFBundleShortVersionString`. This is deliberately a manual, user-initiated
/// check (an "About > Check for Updates" button), not a background auto-updater: Scribe has no
/// code-signing/notarization pipeline set up yet (see notarize.sh), so there is nothing here that
/// silently downloads or installs anything, only a link to the GitHub Releases page.
struct UpdateChecker {
    /// Matches the repo referenced elsewhere in AboutView (support/star links) so all three stay
    /// in sync if the repo ever moves.
    static let repositorySlug = "x3nc0n/scribe"

    private let session: URLSession

    init(session: URLSession = .shared) {
        self.session = session
    }

    func checkForUpdate(currentVersion: String) async -> UpdateCheckResult {
        guard let url = URL(string: "https://api.github.com/repos/\(Self.repositorySlug)/releases/latest") else {
            return .failed(message: "Could not build the release check URL.")
        }
        var request = URLRequest(url: url)
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")

        let release: GitHubRelease
        do {
            let (data, response) = try await session.data(for: request)
            guard let httpResponse = response as? HTTPURLResponse, (200...299).contains(httpResponse.statusCode) else {
                return .failed(message: "Could not reach GitHub (unexpected response).")
            }
            release = try JSONDecoder().decode(GitHubRelease.self, from: data)
        } catch {
            return .failed(message: "Could not reach GitHub. Check your connection and try again.")
        }

        return Self.compare(currentVersion: currentVersion, release: release)
    }

    /// Pulled out as a pure function so tests can exercise every comparison outcome without any
    /// network access, given only a decoded `GitHubRelease`.
    static func compare(currentVersion: String, release: GitHubRelease) -> UpdateCheckResult {
        guard let current = SemanticVersion(currentVersion) else {
            return .failed(message: "Could not parse the current app version.")
        }
        guard let latest = SemanticVersion(release.tagName) else {
            return .failed(message: "Could not parse the latest release version.")
        }
        if latest > current {
            return .updateAvailable(current: currentVersion, latest: release.tagName, url: release.htmlURL)
        }
        return .upToDate(current: currentVersion)
    }
}
