import AVFoundation
import CoreAudio
import Foundation
import os

@testable import Scribe

/// A capture device that plays a scenario's audio. The production engine opens it as it would the microphone, and the
/// test streams its buffers from the device's own serial queue, the way AVAudioEngine's tap thread delivers them.
/// Buffers are filled on that queue from sendable sample arrays, so no `AVAudioPCMBuffer` ever crosses threads.
///
/// Every delivery is filled into a buffer the device allocated on its own queue and keeps for as long as it exists
/// (`DeliveryBuffers`), never into a new one it lets go of straight after. An `AVAudioPCMBuffer`'s sample storage comes
/// from an allocator the thread sanitizer does not track, so it cannot see storage one queue lets go of being handed
/// to the next buffer allocated on another: while a device whose recording was stopped goes on delivering beside the
/// next recording's device, that reuse would read as a race between the two queues' writes. The engine reads a
/// buffer only while `deliver` runs, so refilling one is safe.
final class ScenarioCaptureDevice: CaptureDevice, Sendable {
    let audio: ScenarioDeviceAudio
    /// Opened when the engine starts the device, which is when its tap would begin delivering.
    let started = ScenarioLatch()
    private let queue = DispatchQueue(label: "com.scribe.macos.scenario-device", qos: .userInitiated)
    private let state = OSAllocatedUnfairLock(initialState: State())
    private let buffers = DeliveryBuffers()

    private struct State: Sendable {
        var deliver: (@Sendable (AVAudioPCMBuffer) -> Void)?
        var running = false
        var closes = 0
    }

    init(audio: ScenarioDeviceAudio) {
        self.audio = audio
    }

    /// A device that cannot open, for an engine asked for more microphones than the scenario queued.
    static func unavailable() -> ScenarioCaptureDevice {
        ScenarioCaptureDevice(audio: ScenarioDeviceAudio(sampleRate: 0, encoding: .float32, channels: []))
    }

    func prepare() throws -> AVAudioFormat {
        guard let format = audio.format() else {
            throw AudioCaptureEngineError.missingInputNodeFormat
        }
        return format
    }

    func start(
        deliver: @escaping @Sendable (AVAudioPCMBuffer) -> Void,
        configurationChanged: @escaping @Sendable () -> Void
    ) throws {
        state.withLock { state in
            state.deliver = deliver
            state.running = true
        }
        started.open()
    }

    var isRunning: Bool {
        state.withLock { $0.running }
    }

    /// Keeps the tap closure, as AVAudioEngine can for a callback already under way, so buffers streamed after the
    /// close still reach the engine, which must drop them.
    func close() {
        state.withLock { state in
            state.closes += 1
            state.running = false
        }
    }

    var closeCount: Int {
        state.withLock { $0.closes }
    }

    var currentInputDeviceID: AudioDeviceID? { nil }

    /// Delivers the audio in consecutive buffers of `frameCounts` frames on the device's queue. With `pauseAt`, the
    /// queue delivers that many buffers, opens `paused` and waits for `resume()` before it goes on; with `signalAt`, it
    /// opens `reached` after that many and goes straight on.
    @discardableResult
    func stream(frameCounts: [Int], pauseAt: Int? = nil, signalAt: Int? = nil) -> ScenarioStream {
        let stream = ScenarioStream()
        let audio = audio
        let state = state
        let buffers = buffers
        queue.async {
            let largest = frameCounts.max() ?? 0
            let buffer = audio.format().flatMap { buffers.buffer(format: $0, holding: largest) }
            var start = 0
            for (index, count) in frameCounts.enumerated() {
                if index == signalAt {
                    stream.reached.open()
                }
                if index == pauseAt {
                    stream.paused.open()
                    stream.waitForResume()
                }
                let end = min(audio.frameCount, start + count)
                guard start < end else { break }
                let deliver = state.withLock { $0.deliver }
                if let deliver, let buffer, audio.fill(buffer, with: start..<end) {
                    deliver(buffer)
                }
                start = end
            }
            if signalAt == frameCounts.count {
                stream.reached.open()
            }
            stream.finished.open()
        }
        return stream
    }
}

/// The buffers one device delivers from. Each is allocated on the device's queue the first time a stream needs one
/// that large, and kept until the device goes, so none of their storage is handed back while a device may be
/// delivering.
///
/// `@unchecked Sendable` because the compiler cannot see the confinement: `allocated` is only read or written in
/// `buffer(format:holding:)`, which only `ScenarioCaptureDevice.stream` calls, on the device's serial queue, where
/// every buffer it returns is also filled and delivered.
private final class DeliveryBuffers: @unchecked Sendable {
    private var allocated: [AVAudioPCMBuffer] = []

    /// A buffer of `format` that holds at least `frames` frames.
    func buffer(format: AVAudioFormat, holding frames: Int) -> AVAudioPCMBuffer? {
        if let kept = allocated.first(where: { $0.format == format && Int($0.frameCapacity) >= frames }) {
            return kept
        }
        guard let made = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(max(1, frames))) else {
            return nil
        }
        allocated.append(made)
        return made
    }
}

/// One run of buffers a scenario device is delivering.
final class ScenarioStream: Sendable {
    /// Opened when the stream has delivered the buffers before its pause and waits for `resume()`.
    let paused = ScenarioLatch()
    /// Opened once the stream has delivered the buffers before its signal point, without stopping.
    let reached = ScenarioLatch()
    /// Opened once every buffer has been delivered.
    let finished = ScenarioLatch()
    private let resumption = DispatchSemaphore(value: 0)

    /// Lets a paused stream go on. Safe to call before it pauses, and more than once.
    func resume() {
        resumption.signal()
    }

    /// The device's queue waits here while the stream is paused, for the watchdog's time at most, so a test that fails
    /// before it resumes the stream never strands the queue.
    fileprivate func waitForResume() {
        _ = resumption.wait(timeout: .now() + ScenarioLimits.watchdogSeconds)
    }
}

/// Hands the capture engine the next queued scenario device each time it opens the microphone.
final class ScenarioDeviceQueue: Sendable {
    private let pending = OSAllocatedUnfairLock<[ScenarioCaptureDevice]>(initialState: [])

    func enqueue(_ device: ScenarioCaptureDevice) {
        pending.withLock { $0.append(device) }
    }

    /// For `AudioCaptureEngine(makeDevice:)`. With nothing queued, the device fails to open, as a missing microphone
    /// would.
    var makeDevice: @Sendable () -> any CaptureDevice {
        { [pending] in
            let next = pending.withLock { queued -> ScenarioCaptureDevice? in
                queued.isEmpty ? nil : queued.removeFirst()
            }
            return next ?? ScenarioCaptureDevice.unavailable()
        }
    }
}

/// A recording's capture events, collected on whatever thread the engine posts them.
final class ScenarioEventLog: Sendable {
    /// Opened by the first stop request.
    let stopRequested = ScenarioLatch()
    private let events = OSAllocatedUnfairLock<[CaptureEvent]>(initialState: [])

    var sink: @Sendable (CaptureEvent) -> Void {
        { [events, stopRequested] event in
            events.withLock { $0.append(event) }
            if case .stopRequested = event.kind {
                stopRequested.open()
            }
        }
    }

    var all: [CaptureEvent] {
        events.withLock { $0 }
    }

    var stopReasons: [CaptureEndReason] {
        all.compactMap { event in
            if case .stopRequested(let reason) = event.kind {
                return reason
            }
            return nil
        }
    }

    var levelCount: Int {
        all.filter { event in
            if case .level = event.kind {
                return true
            }
            return false
        }.count
    }
}
