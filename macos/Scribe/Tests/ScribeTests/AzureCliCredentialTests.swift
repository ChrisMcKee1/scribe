import XCTest
import os

@testable import Scribe

final class AzureCliCommandTests: XCTestCase {
    private let arguments = ["account", "get-access-token", "--resource", "https://ai.azure.com", "--output", "json"]

    /// An app opened from Finder has no Homebrew directory on its `PATH`, so `az` is found by name along the search
    /// path, and what runs is `az` itself: the arguments never assume a `/usr/bin/env` in front of them.
    func testThePathLookupRunsAzItselfWithItsArguments() throws {
        let empty = try makeTemporaryDirectory(label: "no-az")
        let az = try makeScript(named: "az", body: "exit 0")

        let command = try AzureCliCommand.accessToken(
            resource: "https://ai.azure.com", tenantId: "contoso.onmicrosoft.com",
            searchPath: [empty.path(percentEncoded: false), az.deletingLastPathComponent().path(percentEncoded: false)])

        XCTAssertEqual(command.executableURL.lastPathComponent, "az")
        XCTAssertEqual(
            command.executableURL.resolvingSymlinksInPath().path(percentEncoded: false),
            az.resolvingSymlinksInPath().path(percentEncoded: false))
        XCTAssertEqual(command.arguments, arguments + ["--tenant", "contoso.onmicrosoft.com"])
    }

    func testWithoutATenantThereIsNoTenantArgument() throws {
        let az = try makeScript(named: "az", body: "exit 0")

        let command = try AzureCliCommand.accessToken(
            resource: "https://ai.azure.com", tenantId: nil,
            searchPath: [az.deletingLastPathComponent().path(percentEncoded: false)])

        XCTAssertEqual(command.arguments, arguments)
    }

    func testNoAzAnywhereIsReportedAsNotFound() throws {
        let empty = try makeTemporaryDirectory(label: "no-az")

        XCTAssertThrowsError(
            try AzureCliCommand.accessToken(
                resource: "https://ai.azure.com", tenantId: nil, searchPath: [empty.path(percentEncoded: false)])
        ) { error in
            XCTAssertEqual(error as? AzureCredentialError, .cliNotFound)
        }
    }

    func testTheResourceIsTheScopeWithoutItsDefaultSuffix() {
        XCTAssertEqual(AzureCliCredentialProvider.resource(fromScope: "https://ai.azure.com/.default"), "https://ai.azure.com")
        XCTAssertEqual(AzureCliCredentialProvider.resource(fromScope: "https://ai.azure.com"), "https://ai.azure.com")
    }
}

final class EntraErrorCodeTests: XCTestCase {
    /// A number is read from what `az` printed only as Entra writes one, so it never turns into a different, listed
    /// code: a leading zero, a longer run of digits or too few digits is no code at all.
    func testOnlyACodeWrittenAsEntraWritesOneIsRead() {
        XCTAssertEqual(AzureCliFailureReason.aadstsCode(in: "AADSTS70043: The refresh token has expired."), 70_043)
        XCTAssertEqual(AzureCliFailureReason.aadstsCode(in: "first AADSTS50076, then AADSTS70043"), 50_076)
        XCTAssertEqual(AzureCliFailureReason.aadstsCode(in: "(AADSTS7000215)"), 7_000_215)
        for text in ["AADSTS070043: x", "AADSTS1234567890123", "AADSTS123", "AADSTS: x", "aadsts70043", "AADSTS7000x"] {
            XCTAssertNil(AzureCliFailureReason.aadstsCode(in: text), text)
        }
        XCTAssertEqual(AzureCliFailureReason.classify(standardError: "AADSTS070043: x").reason, .other)
    }

    /// Printed, dumped or shaped, a code shows its digits only when `FailureShape` lists it.
    func testACodePrintsItsDigitsOnlyWhenListed() {
        let listed: EntraErrorCode = 7_000_215
        let unlisted: EntraErrorCode = 1_234_567

        XCTAssertTrue(listed.isListed)
        XCTAssertFalse(unlisted.isListed)
        XCTAssertEqual(listed.code, "AADSTS7000215")
        XCTAssertEqual(unlisted.code, "AADSTS1234567")
        for (code, expected) in [(listed, "AADSTS7000215"), (unlisted, "AADSTS")] {
            var dumped = ""
            dump(code, to: &dumped)
            XCTAssertEqual(String(describing: code), expected)
            XCTAssertEqual(String(reflecting: code), expected)
            XCTAssertEqual(dumped, "- \(expected)\n")
        }
        let error = AzureCredentialError.cliFailed(exitStatus: 1, reason: .signInRejected, aadsts: unlisted)
        let shape = FailureShape(error).description
        XCTAssertTrue(shape.hasPrefix("AzureCredentialError.cliFailed "), shape)
        XCTAssertTrue(shape.hasSuffix(" service=AADSTS inner=AzureCliFailureReason.signInRejected"), shape)
        XCTAssertFalse(shape.contains("1234567"), shape)
    }

    /// The codes Scribe gives a hint of its own, as Windows' `AzureSignInDiagnostics` does, and whether each is
    /// named in the description. A hinted code that is on Microsoft's error code reference is in `FailureShape`'s
    /// list; AADSTS900023 is not on it, so it keeps its hint but stays unnamed outside Settings.
    func testEachHintedCodeGetsItsOwnHint() throws {
        let cases: [(number: Int, hint: String, listed: Bool)] = [
            (7_000_215, "can take a moment to become active", true),
            (7_000_222, "the client secret has expired", true),
            (700_016, "the application was not found in this tenant", true),
            (90_002, "the tenant was not found", true),
            (50_034, "no service principal exists", true),
            (900_023, "neither a directory (tenant) ID nor a domain name", false),
        ]

        for (number, hint, listed) in cases {
            let code = EntraErrorCode(number)
            XCTAssertEqual(code.isListed, listed, code.code)
            let error = AzureCredentialError.tokenRejected(status: 400, aadsts: code, reply: .empty)
            let description = try XCTUnwrap(error.errorDescription)
            XCTAssertTrue(description.contains(hint), description)
            XCTAssertEqual(description.contains(code.code), listed, description)
            XCTAssertEqual(error.settingsDetail, listed ? nil : code.code)
        }
    }
}

final class AzureCliCredentialProviderTests: XCTestCase {
    private let scope = MicrosoftFoundryCleanupProvider.inferenceScope

    private func azDirectory() throws -> String {
        try makeScript(named: "az", body: "exit 1").deletingLastPathComponent().path(percentEncoded: false)
    }

    /// End to end with a stand-in `az` script run through `ProcessRunner`: the arguments it received and the token
    /// it printed.
    func testAnAzRunThroughTheProcessRunnerReturnsItsToken() async throws {
        let directory = try makeTemporaryDirectory(label: "az")
        let received = directory.appendingPathComponent("arguments")
        let az = directory.appendingPathComponent("az")
        let script = """
            #!/bin/sh
            printf '%s\\n' "$@" > '\(received.path(percentEncoded: false))'
            echo '{"accessToken":"cli-token","expires_on":4102444800,"tokenType":"Bearer"}'
            """
        try Data(script.utf8).write(to: az)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: az.path(percentEncoded: false))
        let provider = AzureCliCredentialProvider(
            tenantId: "contoso.onmicrosoft.com", searchPath: [directory.path(percentEncoded: false)], lane: AsyncLane())

        let token = try await provider.accessToken(scope: scope)

        XCTAssertEqual(token.token, "cli-token")
        XCTAssertEqual(token.expiresAt, Date(timeIntervalSince1970: 4_102_444_800))
        let arguments = try String(contentsOf: received, encoding: .utf8).split(separator: "\n").map(String.init)
        XCTAssertEqual(
            arguments,
            [
                "account", "get-access-token", "--resource", "https://ai.azure.com", "--output", "json", "--tenant",
                "contoso.onmicrosoft.com",
            ])
    }

    /// What `az` printed is reduced to a reason; the words themselves, the signed-in account among them, are dropped.
    func testAFailingAzIsClassifiedAndWhatItPrintedIsDropped() async throws {
        let directory = try makeTemporaryDirectory(label: "az")
        let az = directory.appendingPathComponent("az")
        let script = """
            #!/bin/sh
            echo "ERROR: Please run 'az login' to setup account. Signed in as dana-private@contoso.com" >&2
            exit 1
            """
        try Data(script.utf8).write(to: az)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: az.path(percentEncoded: false))
        let provider = AzureCliCredentialProvider(searchPath: [directory.path(percentEncoded: false)], lane: AsyncLane())

        do {
            _ = try await provider.accessToken(scope: scope)
            XCTFail("Expected az to fail")
        } catch let error as AzureCredentialError {
            XCTAssertEqual(error, .cliFailed(exitStatus: 1, reason: .notSignedIn, aadsts: nil))
            let description = try XCTUnwrap(error.errorDescription)
            XCTAssertTrue(description.contains("az login"), description)
            for text in [description, String(describing: error), FailureShape(error).description] {
                XCTAssertFalse(text.contains("dana-private"), text)
            }
        }
    }

    func testAnEntraErrorFromAzKeepsOnlyItsNumber() async throws {
        let fake = FakeAzureCli(outcomes: [
            .exited(1, standardError: "AADSTS70043: The refresh token has expired due to inactivity. Trace ID: t-1")
        ])
        let path = try azDirectory()
        let provider = AzureCliCredentialProvider(searchPath: [path], lane: AsyncLane(), launch: fake.launch)

        do {
            _ = try await provider.accessToken(scope: scope)
            XCTFail("Expected az to fail")
        } catch let error as AzureCredentialError {
            XCTAssertEqual(error, .cliFailed(exitStatus: 1, reason: .signInRejected, aadsts: 70_043))
            XCTAssertEqual(error.failureServiceCode, "AADSTS70043")
            XCTAssertFalse(String(describing: error).contains("Trace"))
            let description = try XCTUnwrap(error.errorDescription)
            XCTAssertTrue(description.contains("(AADSTS70043)"), description)
            XCTAssertNil(error.settingsDetail, "a listed code is in the description already")
        }
    }

    /// An Entra code `FailureShape` does not list keeps its digits out of the shape and the description, as the digits
    /// after `AADSTS` are the service's to choose; only the Settings text, which is never logged, shows the code.
    func testAnUnlistedEntraCodeFromAzReachesOnlyTheSettingsText() async throws {
        let fake = FakeAzureCli(outcomes: [.exited(1, standardError: "AADSTS1234567: Something new went wrong.")])
        let path = try azDirectory()
        let provider = AzureCliCredentialProvider(searchPath: [path], lane: AsyncLane(), launch: fake.launch)

        do {
            _ = try await provider.accessToken(scope: scope)
            XCTFail("Expected az to fail")
        } catch let error as AzureCredentialError {
            XCTAssertEqual(error, .cliFailed(exitStatus: 1, reason: .signInRejected, aadsts: 1_234_567))
            let shape = FailureShape(error).description
            XCTAssertTrue(shape.contains("service=AADSTS"), shape)
            let description = try XCTUnwrap(error.errorDescription)
            XCTAssertTrue(description.contains("az login"), description)
            for text in [shape, description, String(describing: error)] {
                XCTAssertFalse(text.contains("1234567"), text)
            }
            XCTAssertTrue(
                CleanupFailureText.forSettings(error, providerName: nil).hasSuffix("Microsoft Entra reported AADSTS1234567."))
        }
    }

    func testAStoppedAzIsReportedByWhyItStopped() async throws {
        let path = try azDirectory()
        let timedOut = AzureCliCredentialProvider(
            searchPath: [path], lane: AsyncLane(), launch: FakeAzureCli(outcomes: [.stopped(.timedOut)]).launch)
        let cancelled = AzureCliCredentialProvider(
            searchPath: [path], lane: AsyncLane(), launch: FakeAzureCli(outcomes: [.stopped(.cancelled)]).launch)

        do {
            _ = try await timedOut.accessToken(scope: scope)
            XCTFail("Expected a timeout")
        } catch let error as AzureCredentialError {
            XCTAssertEqual(error, .cliTimedOut)
        }
        do {
            _ = try await cancelled.accessToken(scope: scope)
            XCTFail("Expected a cancellation")
        } catch {
            XCTAssertTrue(error is CancellationError, "\(FailureShape(error))")
        }
    }

    func testTheTokenIsReusedUntilAMinuteBeforeItExpires() async throws {
        let clock = TestClock()
        let expiry = Int(clock.date.timeIntervalSince1970) + 3600
        let fake = FakeAzureCli(outcomes: [.azToken("first", expiresOn: expiry), .azToken("second", expiresOn: expiry + 3600)])
        let path = try azDirectory()
        let provider = AzureCliCredentialProvider(
            searchPath: [path], lane: AsyncLane(), launch: fake.launch, now: clock.now)

        let first = try await provider.accessToken(scope: scope)
        clock.advance(by: 3539)
        let reused = try await provider.accessToken(scope: scope)
        clock.advance(by: 1)
        let renewed = try await provider.accessToken(scope: scope)

        XCTAssertEqual([first.token, reused.token, renewed.token], ["first", "first", "second"])
        XCTAssertEqual(fake.launches, 2)
    }

    /// A second request that waits in the lane while the first runs `az` finds the new token instead of running `az`
    /// again.
    func testConcurrentRequestsForOneIdentityRunAzOnce() async throws {
        let gate = SettingsTestGate()
        let lane = AsyncLane()
        let clock = TestClock()
        let fake = FakeAzureCli(
            outcomes: [.azToken("shared", expiresOn: Int(clock.date.timeIntervalSince1970) + 3600)], gate: gate)
        let path = try azDirectory()
        let provider = AzureCliCredentialProvider(
            searchPath: [path], lane: lane, launch: fake.launch, now: clock.now)

        let scope = self.scope
        async let first = provider.accessToken(scope: scope)
        await waitBounded("the first caller to hold the lane") { await gate.waitForArrival() }
        async let second = provider.accessToken(scope: scope)
        await waitBounded("the second caller to queue") { await lane.waitUntilWaiting(atLeast: 1) }
        await gate.open()
        let tokens = try await [first, second]

        XCTAssertEqual(tokens.map(\.token), ["shared", "shared"])
        XCTAssertEqual(fake.launches, 1)
    }

    /// Windows' `AzureCliProcessCoordinator` runs one `az` at a time for the whole process, whoever asks; so does the
    /// lane, even for two identities that share nothing else.
    func testTwoIdentitiesTakeTurnsInTheOneLane() async throws {
        let gate = SettingsTestGate()
        let lane = AsyncLane()
        let clock = TestClock()
        let fake = FakeAzureCli(
            outcomes: [.azToken("token", expiresOn: Int(clock.date.timeIntervalSince1970) + 3600)], gate: gate)
        let path = try azDirectory()
        let dictation = AzureCliCredentialProvider(
            tenantId: "tenant-a.example.com", searchPath: [path], lane: lane, launch: fake.launch, now: clock.now)
        let settingsCheck = AzureCliCredentialProvider(
            tenantId: "tenant-b.example.com", searchPath: [path], lane: lane, launch: fake.launch, now: clock.now)

        let scope = self.scope
        async let first = dictation.accessToken(scope: scope)
        await waitBounded("the first caller to hold the lane") { await gate.waitForArrival() }
        async let second = settingsCheck.accessToken(scope: scope)
        await waitBounded("the second caller to queue") { await lane.waitUntilWaiting(atLeast: 1) }
        XCTAssertEqual(fake.launches, 1)
        await gate.open()
        _ = try await [first, second]

        XCTAssertEqual(fake.launches, 2)
        XCTAssertEqual(fake.mostRunningAtOnce, 1)
        XCTAssertEqual(fake.commands.map { $0.arguments.last }, ["tenant-a.example.com", "tenant-b.example.com"])
    }
}

final class AsyncLaneTests: XCTestCase {
    func testACallerCancelledWhileWaitingLeavesTheLaneAtOnce() async throws {
        let lane = AsyncLane()
        let gate = SettingsTestGate()
        let holder = Task { try await lane.run { () async -> Int in
            await gate.pass()
            return 1
        } }
        await waitBounded("the first caller to hold the lane") { await gate.waitForArrival() }
        let waiter = Task { try await lane.run { 2 } }
        await waitBounded("the second caller to queue") { await lane.waitUntilWaiting(atLeast: 1) }

        waiter.cancel()

        do {
            _ = try await waiter.value
            XCTFail("Expected the waiting caller to be cancelled")
        } catch {
            XCTAssertTrue(error is CancellationError, "\(FailureShape(error))")
        }
        XCTAssertEqual(lane.waitingCount, 0)
        await gate.open()
        let held = try await holder.value
        XCTAssertEqual(held, 1)
        let afterwards = try await lane.run { 3 }
        XCTAssertEqual(afterwards, 3)
    }

    func testWaitersAreServedInTheOrderTheyArrived() async throws {
        let lane = AsyncLane()
        let gate = SettingsTestGate()
        let order = RequestOrder()
        let holder = Task { try await lane.run { () async -> Int in
            await gate.pass()
            order.append(0)
            return 0
        } }
        await waitBounded("the first caller to hold the lane") { await gate.waitForArrival() }
        var waiters: [Task<Int, any Error>] = []
        for index in 1...3 {
            waiters.append(Task { try await lane.run { () -> Int in
                order.append(index)
                return index
            } })
            await waitBounded("caller \(index) to queue") { await lane.waitUntilWaiting(atLeast: index) }
        }

        await gate.open()
        _ = try await holder.value
        for waiter in waiters {
            _ = try await waiter.value
        }

        XCTAssertEqual(order.values, [0, 1, 2, 3])
    }

    func testACallerCancelledBeforeItArrivesNeverRuns() async throws {
        let lane = AsyncLane()
        let order = RequestOrder()
        let task = Task {
            _ = withUnsafeCurrentTask { $0?.cancel() }
            return try await lane.run { () -> Int in
                order.append(1)
                return 1
            }
        }

        do {
            _ = try await task.value
            XCTFail("Expected a cancellation")
        } catch {
            XCTAssertTrue(error is CancellationError, "\(FailureShape(error))")
        }
        XCTAssertEqual(order.values, [])
        XCTAssertEqual(lane.waitingCount, 0)
    }
}

/// Integers appended from any task, in the order they arrive.
final class RequestOrder: Sendable {
    private let recorded = OSAllocatedUnfairLock<[Int]>(initialState: [])

    func append(_ value: Int) {
        recorded.withLock { $0.append(value) }
    }

    var values: [Int] {
        recorded.withLock { $0 }
    }
}
