import AudioToolbox
@preconcurrency import AVFoundation
import CoreAudio
import OSLog

struct AudioLevelMeasurement {
    let peakAmplitude: Float
    let rmsAmplitude: Float

    var peakDbfs: Float {
        AudioLevelMeasurement.toDbfs(peakAmplitude)
    }

    var rmsDbfs: Float {
        AudioLevelMeasurement.toDbfs(rmsAmplitude)
    }

    private static func toDbfs(_ amplitude: Float) -> Float {
        guard amplitude > 0 else { return -99 }
        return max(-99, 20 * log10f(amplitude))
    }
}

struct AudioCaptureChunk {
    let buffer: AVAudioPCMBuffer
    let level: AudioLevelMeasurement
}

struct AudioCaptureSummary {
    let startedAt: Date
    let stoppedAt: Date
    let sampleCount: Int
    let sampleRate: Double

    var durationSeconds: Double {
        Double(sampleCount) / sampleRate
    }
}

enum AudioCaptureEngineError: LocalizedError {
    case microphoneNotAuthorized(AVAuthorizationStatus)
    case missingInputNodeFormat
    case converterInitializationFailed
    case engineStartFailed(String)

    var errorDescription: String? {
        switch self {
        case .microphoneNotAuthorized:
            return "Microphone access is not authorized."
        case .missingInputNodeFormat:
            return "The default input device did not report a usable audio format."
        case .converterInitializationFailed:
            return "Could not create the audio converter for 16 kHz mono capture."
        case .engineStartFailed(let reason):
            return "The audio engine could not start: \(reason)"
        }
    }
}

final class AudioCaptureEngine {
    static let targetSampleRate: Double = 16_000
    static let targetFormat = AVAudioFormat(
        commonFormat: .pcmFormatFloat32,
        sampleRate: AudioCaptureEngine.targetSampleRate,
        channels: 1,
        interleaved: false)!

    var onChunk: ((AudioCaptureChunk) -> Void)?
    var onCaptureError: ((Error) -> Void)?

    private let audioEngine = AVAudioEngine()
    private let logger = Logger(subsystem: "com.scribe.macos", category: "AudioCapture")
    private let outputFormat = AudioCaptureEngine.targetFormat

    private var converter: AVAudioConverter?
    private var isTapInstalled = false
    private(set) var isCapturing = false
    private var startedAt: Date?
    private var sampleCount = 0

    func start() throws {
        guard !isCapturing else { return }

        let status = AVCaptureDevice.authorizationStatus(for: .audio)
        guard status == .authorized else {
            logger.warning("Microphone capture start blocked, authorization status: \(String(describing: status), privacy: .public)")
            throw AudioCaptureEngineError.microphoneNotAuthorized(status)
        }

        let inputNode = audioEngine.inputNode
        applySelectedInputDeviceIfNeeded(to: inputNode)

        let inputFormat = inputNode.inputFormat(forBus: 0)
        guard inputFormat.sampleRate > 0, inputFormat.channelCount > 0 else {
            throw AudioCaptureEngineError.missingInputNodeFormat
        }

        let outputFormat = self.outputFormat

        guard let converter = AVAudioConverter(from: inputFormat, to: outputFormat) else {
            throw AudioCaptureEngineError.converterInitializationFailed
        }

        audioEngine.stop()
        audioEngine.reset()
        if isTapInstalled {
            inputNode.removeTap(onBus: 0)
            isTapInstalled = false
        }

        self.converter = converter
        self.sampleCount = 0
        self.startedAt = Date()

        inputNode.installTap(onBus: 0, bufferSize: 2_048, format: inputFormat) { [weak self] buffer, _ in
            self?.handleTapBuffer(buffer)
        }
        isTapInstalled = true

        do {
            try audioEngine.start()
            isCapturing = true
            logger.info(
                "Started microphone capture at \(inputFormat.sampleRate, format: .fixed(precision: 0)) Hz, \(inputFormat.channelCount) channel(s), resampling to 16000 Hz mono Float32.")
        } catch {
            cleanupCaptureGraph()
            logger.error("Audio engine start failed: \(error.localizedDescription, privacy: .public)")
            throw AudioCaptureEngineError.engineStartFailed(error.localizedDescription)
        }
    }

    func stop() -> AudioCaptureSummary? {
        guard isCapturing, let startedAt else { return nil }

        audioEngine.stop()
        cleanupCaptureGraph()
        isCapturing = false

        let summary = AudioCaptureSummary(
            startedAt: startedAt,
            stoppedAt: Date(),
            sampleCount: sampleCount,
            sampleRate: AudioCaptureEngine.targetSampleRate)

        logger.info(
            "Stopped microphone capture after \(summary.durationSeconds, format: .fixed(precision: 2)) s with \(summary.sampleCount) samples.")

        self.startedAt = nil
        self.sampleCount = 0

        return summary
    }

    /// Points the AUHAL input unit at the user's chosen microphone (via `AudioDeviceStore`)
    /// instead of leaving it on whatever CoreAudio currently considers the system default. This
    /// is what makes an explicit in-app microphone choice stick even if the user later changes
    /// their system-wide default input in System Settings > Sound. Setting no device (the common
    /// case) is a deliberate no-op: the AUHAL unit already tracks the system default on its own,
    /// which is also how a Bluetooth microphone works with zero code here, as long as it is the
    /// current system default input.
    private func applySelectedInputDeviceIfNeeded(to inputNode: AVAudioInputNode) {
        guard var deviceID = AudioDeviceStore.resolveSelectedDeviceID() else { return }
        guard let audioUnit = inputNode.audioUnit else {
            logger.warning("Could not select the saved microphone: the input node has no underlying audio unit yet.")
            return
        }

        let status = AudioUnitSetProperty(
            audioUnit,
            kAudioOutputUnitProperty_CurrentDevice,
            kAudioUnitScope_Global,
            0,
            &deviceID,
            UInt32(MemoryLayout<AudioDeviceID>.size))

        if status != noErr {
            logger.warning("Failed to select the saved microphone (OSStatus \(status, privacy: .public)); falling back to the system default input.")
        }
    }

    private func cleanupCaptureGraph() {
        let inputNode = audioEngine.inputNode
        if isTapInstalled {
            inputNode.removeTap(onBus: 0)
            isTapInstalled = false
        }

        converter?.reset()
        converter = nil
        audioEngine.reset()
    }

    private func handleTapBuffer(_ inputBuffer: AVAudioPCMBuffer) {
        guard let converter else { return }
        let outputFormat = self.outputFormat

        do {
            let convertedBuffers = try convert(inputBuffer, using: converter, outputFormat: outputFormat)
            for buffer in convertedBuffers where buffer.frameLength > 0 {
                sampleCount += Int(buffer.frameLength)
                let level = measureLevels(in: buffer)
                onChunk?(AudioCaptureChunk(buffer: buffer, level: level))
            }
        } catch {
            logger.error("Capture conversion failed: \(error.localizedDescription, privacy: .public)")
            onCaptureError?(error)
            _ = stop()
        }
    }

    private func convert(
        _ inputBuffer: AVAudioPCMBuffer,
        using converter: AVAudioConverter,
        outputFormat: AVAudioFormat
    ) throws -> [AVAudioPCMBuffer] {
        // NOTE: AVAudioConverter's simple `convert(to:from:)` overload enforces
        // `outputBuffer.frameCapacity >= inputBuffer.frameLength` even when downsampling makes
        // that requirement nonsensical (e.g. 48kHz -> 16kHz halves the frame count), and throws
        // an uncaught Objective-C exception (not a Swift `Error`) when violated, crashing the
        // process. The block-based `convert(to:error:withInputFrom:)` overload is the API Apple
        // documents for real-time sample-rate conversion and has no such capacity constraint;
        // it pulls exactly one input buffer via the callback and lets the converter size its own
        // internal buffering.
        let ratio = outputFormat.sampleRate / inputBuffer.format.sampleRate
        let capacity = max(1, Int(ceil(Double(inputBuffer.frameLength) * ratio)) + 32)
        guard let outputBuffer = AVAudioPCMBuffer(
            pcmFormat: outputFormat,
            frameCapacity: AVAudioFrameCount(capacity)) else {
            throw AudioCaptureEngineError.converterInitializationFailed
        }

        // Boxed in a reference type (rather than a captured `var`) because the input-provider
        // closure runs synchronously on this same call stack, but the compiler cannot see that
        // and otherwise flags a Sendable capture warning; this keeps the build warning-clean.
        final class ConsumedFlag: @unchecked Sendable {
            var value = false
        }
        let consumed = ConsumedFlag()
        var conversionError: NSError?
        let status = converter.convert(to: outputBuffer, error: &conversionError) { _, inputStatus in
            if consumed.value {
                inputStatus.pointee = .noDataNow
                return nil
            }
            consumed.value = true
            inputStatus.pointee = .haveData
            return inputBuffer
        }

        if let conversionError {
            throw conversionError
        }
        guard status != .error else {
            throw AudioCaptureEngineError.converterInitializationFailed
        }

        return outputBuffer.frameLength > 0 ? [outputBuffer] : []
    }

    private func measureLevels(in buffer: AVAudioPCMBuffer) -> AudioLevelMeasurement {
        guard
            let channelData = buffer.floatChannelData?[0]
        else {
            return AudioLevelMeasurement(peakAmplitude: 0, rmsAmplitude: 0)
        }

        let frameCount = Int(buffer.frameLength)
        guard frameCount > 0 else {
            return AudioLevelMeasurement(peakAmplitude: 0, rmsAmplitude: 0)
        }

        var peak: Float = 0
        var sumSquares: Float = 0

        for index in 0..<frameCount {
            let sample = channelData[index]
            let magnitude = abs(sample)
            peak = max(peak, magnitude)
            sumSquares += sample * sample
        }

        let rms = sqrtf(sumSquares / Float(frameCount))
        return AudioLevelMeasurement(peakAmplitude: peak, rmsAmplitude: rms)
    }
}
