import XCTest

@testable import Scribe

/// The two schedules the review found, step by step, against the value the cache applies each step to under its lock.
/// Each step here is one critical section of `CleanupProviderCache`, so these are the interleavings threads can
/// produce, run in a fixed order.
final class CleanupProviderCacheStateTests: XCTestCase {
    private let identity = AzureIdentity.servicePrincipal(
        tenantId: "tenant-1", clientId: "client-1", secretRevision: "revision-1")

    private func connection(_ deployment: String) -> CleanupConnection {
        CleanupConnection(
            target: .microsoftFoundry(
                inferenceBase: URL(string: "https://my-res.openai.azure.com/openai/v1/")!, deployment: deployment,
                identity: identity),
            source: .settings)
    }

    private func entry(_ deployment: String) -> CleanupProviderCacheState.Entry {
        CleanupProviderCacheState.Entry(connection: connection(deployment), provider: StaticCleanupProvider())
    }

    private func held(_ credential: RecordingCredential) -> CleanupProviderCacheState.HeldCredential {
        CleanupProviderCacheState.HeldCredential(identity: identity, credential: credential)
    }

    private func same(_ first: (any AzureCredentialProvider)?, _ second: any AzureCredentialProvider) -> Bool {
        guard let first else { return false }
        return (first as AnyObject) === (second as AnyObject)
    }

    /// A state holding a provider for deployment "a" and its credential, as after a first dictation.
    private func warmState(_ credential: RecordingCredential) -> CleanupProviderCacheState {
        var state = CleanupProviderCacheState()
        let snapshot = state.snapshot(for: connection("a"))
        _ = state.publish(entry("a"), credential: held(credential), since: snapshot)
        return state
    }

    /// Schedule 1: a build that found the old credential must not keep a provider holding it once `invalidate()` has
    /// run, and a build that starts after `invalidate()` finds no credential at all, because both tiers are cleared in
    /// the same step that starts the new epoch.
    func testABuildHoldingTheOldCredentialKeepsNothingAfterAnInvalidation() {
        let old = RecordingCredential()
        var state = warmState(old)

        let building = state.snapshot(for: connection("b"))
        XCTAssertTrue(same(building.credential?.credential, old), "the build found the credential held then")
        state.invalidate()
        XCTAssertNil(state.snapshot(for: connection("b")).credential, "a build starting now finds none")
        let handedOut = state.publish(entry("b"), credential: nil, since: building)

        XCTAssertEqual(handedOut.connection, connection("b"), "the build's own caller still gets its provider")
        XCTAssertNil(state.entry)
        XCTAssertNil(state.credential)
    }

    /// Schedule 2: a credential made by a build that began before `invalidate()` is not published into the cleared
    /// cache, so the next build cannot pick it up under the new epoch.
    func testACredentialMadeAcrossAnInvalidationIsNotKept() {
        var state = CleanupProviderCacheState()
        let building = state.snapshot(for: connection("a"))
        let made = RecordingCredential()

        state.invalidate()
        _ = state.publish(entry("a"), credential: held(made), since: building)

        XCTAssertNil(state.credential)
        XCTAssertNil(state.entry)
        let next = state.snapshot(for: connection("a"))
        XCTAssertNil(next.credential)
        XCTAssertNil(next.entry)
    }

    /// Without an invalidation, a build keeps its provider and the credential it made, and a later build of the same
    /// identity finds that credential.
    func testABuildKeepsWhatItMadeWhenNothingInvalidatedIt() {
        let made = RecordingCredential()
        let state = warmState(made)

        XCTAssertEqual(state.entry?.connection, connection("a"))
        XCTAssertTrue(same(state.snapshot(for: connection("b")).credential?.credential, made))
        XCTAssertEqual(state.epoch, 0)
    }

    /// Two builds of one connection in one epoch share the first one's provider, and the second keeps nothing of its
    /// own.
    func testTheFirstOfTwoBuildsOfOneConnectionWins() {
        var state = CleanupProviderCacheState()
        let first = state.snapshot(for: connection("a"))
        let second = state.snapshot(for: connection("a"))
        let firstCredential = RecordingCredential()
        let secondCredential = RecordingCredential()

        let kept = state.publish(entry("a"), credential: held(firstCredential), since: first)
        let alsoKept = state.publish(entry("a"), credential: held(secondCredential), since: second)

        XCTAssertTrue((kept.provider as AnyObject) === (alsoKept.provider as AnyObject))
        XCTAssertTrue(same(state.credential?.credential, firstCredential))
    }

    /// Every invalidation starts a new epoch, and a snapshot from any earlier epoch publishes nothing.
    func testEveryInvalidationStartsANewEpoch() {
        var state = warmState(RecordingCredential())
        let stale = state.snapshot(for: connection("b"))

        state.invalidate()
        state.invalidate()
        _ = state.publish(entry("b"), credential: nil, since: stale)

        XCTAssertEqual(state.epoch, 2)
        XCTAssertNil(state.entry)
        let fresh = state.snapshot(for: connection("b"))
        _ = state.publish(entry("b"), credential: held(RecordingCredential()), since: fresh)
        XCTAssertEqual(state.entry?.connection, connection("b"))
    }
}
