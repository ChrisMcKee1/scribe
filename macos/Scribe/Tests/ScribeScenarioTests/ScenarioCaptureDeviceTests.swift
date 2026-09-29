import AVFoundation
import XCTest
import os

@testable import Scribe

/// The scenario device itself: every delivery is filled into a buffer the device keeps for as long as it exists, never
/// into a new one it lets go of straight after (see `ScenarioCaptureDevice` for why), and each still holds exactly its
/// own frames of the clip.
final class ScenarioCaptureDeviceTests: XCTestCase {
    func testEveryDeliveryIsFilledIntoTheBufferTheDeviceKeeps() async throws {
        let clip = try ScenarioLibrary.shared().clip("dict-kubernetes")
        let audio = ScenarioDeviceAudio(sampleRate: 16_000, encoding: .float32, channels: [clip.samples])
        let device = ScenarioCaptureDevice(audio: audio)
        // Holding every delivered buffer means a device that made one per delivery could not hand the same address
        // out twice, so the identities below tell one kept buffer from many.
        let delivered = OSAllocatedUnfairLock<[AVAudioPCMBuffer]>(uncheckedState: [])
        let samples = OSAllocatedUnfairLock<[[Float]]>(initialState: [])
        try device.start(
            deliver: { buffer in
                let frames = Int(buffer.frameLength)
                let copy = buffer.floatChannelData.map { Array(UnsafeBufferPointer(start: $0[0], count: frames)) }
                delivered.withLockUnchecked { $0.append(buffer) }
                samples.withLock { $0.append(copy ?? []) }
            },
            configurationChanged: {})

        let first = ScenarioAudio.frameCounts(total: audio.frameCount, seed: 31, within: 17...2_049)
        let firstStream = device.stream(frameCounts: first)
        try await underWatchdog("the first stream") { try await firstStream.finished.wait() }
        let largest = try XCTUnwrap(first.max())
        let second = ScenarioAudio.frameCounts(total: audio.frameCount, seed: 32, within: 17...largest)
        let secondStream = device.stream(frameCounts: second)
        try await underWatchdog("the second stream") { try await secondStream.finished.wait() }

        let buffers = delivered.withLockUnchecked { $0 }
        let copies = samples.withLock { $0 }
        XCTAssertEqual(buffers.count, first.count + second.count)
        XCTAssertEqual(
            Set(buffers.map { ObjectIdentifier($0) }).count, 1, "a delivery was filled into a buffer of its own")
        XCTAssertEqual(copies.map(\.count), first + second)
        XCTAssertTrue(copies.prefix(first.count).flatMap { $0 } == clip.samples, "the first stream is not the clip")
        XCTAssertTrue(copies.dropFirst(first.count).flatMap { $0 } == clip.samples, "the second stream is not the clip")
    }
}
