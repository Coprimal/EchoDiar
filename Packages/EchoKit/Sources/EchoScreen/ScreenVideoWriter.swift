import AVFoundation
import CoreMedia

public enum ScreenVideoWriterError: Error, Sendable, Equatable {
    /// The writer refused the HEVC settings (size, quality or frame rate).
    case settingsRejected
    /// The writer failed without an error of its own.
    case writerFailed
}

/// Writes a video-only HEVC file at constant quality, in real time, on the audio file's time base: every frame is
/// re-timed to its file time (paused spans already removed), the session starts at zero so the first frame sits at its
/// file time, and the file ends at the file time given to `finish(at:)`. Fragmented every 10 s, so a killed process
/// leaves a readable file. Settings as measured in the spike (docs/research/2026-11-23-screen-recording-spike).
///
/// Not thread-safe: the owner calls it from one serial queue.
public final class ScreenVideoWriter {
    private let writer: AVAssetWriter
    private let input: AVAssetWriterInput
    private var lastFileTime = CMTime.invalid

    public init(url: URL, width: Int, height: Int, fps: Int, quality: Double, fileType: AVFileType) throws {
        writer = try AVAssetWriter(outputURL: url, fileType: fileType)
        writer.movieFragmentInterval = CMTime(value: 10, timescale: 1)
        let settings: [String: Any] = [
            AVVideoCodecKey: AVVideoCodecType.hevc,
            AVVideoWidthKey: width,
            AVVideoHeightKey: height,
            AVVideoCompressionPropertiesKey: [
                AVVideoQualityKey: quality,
                AVVideoMaxKeyFrameIntervalDurationKey: 10,
                AVVideoExpectedSourceFrameRateKey: fps,
            ],
        ]
        guard writer.canApply(outputSettings: settings, forMediaType: .video) else {
            throw ScreenVideoWriterError.settingsRejected
        }
        input = AVAssetWriterInput(mediaType: .video, outputSettings: settings)
        input.expectsMediaDataInRealTime = true
        writer.add(input)
        guard writer.startWriting() else { throw writer.error ?? ScreenVideoWriterError.writerFailed }
        writer.startSession(atSourceTime: .zero)
    }

    /// The writer failed: nothing more can be appended and `finish(at:)` throws.
    public var failed: Bool { writer.status == .failed }

    /// Appends a copy of `sampleBuffer` presented at `fileTime`. Returns false when the frame is dropped: `fileTime`
    /// not greater than the last appended one, the encoder not ready, or the writer failed.
    @discardableResult
    public func append(_ sampleBuffer: CMSampleBuffer, fileTime: CMTime) -> Bool {
        guard writer.status == .writing, fileTime.isNumeric,
              !lastFileTime.isValid || fileTime > lastFileTime,
              input.isReadyForMoreMediaData else { return false }
        var timing = CMSampleTimingInfo(duration: .invalid, presentationTimeStamp: fileTime, decodeTimeStamp: .invalid)
        var copy: CMSampleBuffer?
        guard CMSampleBufferCreateCopyWithNewTiming(allocator: kCFAllocatorDefault, sampleBuffer: sampleBuffer,
                                                    sampleTimingEntryCount: 1, sampleTimingArray: &timing,
                                                    sampleBufferOut: &copy) == noErr,
              let copy, input.append(copy) else { return false }
        lastFileTime = fileTime
        return true
    }

    /// Ends the track at `fileTime` and finishes the file.
    public func finish(at fileTime: CMTime) async throws {
        if writer.status == .writing {
            writer.endSession(atSourceTime: fileTime)
            input.markAsFinished()
            await writer.finishWriting()
        }
        guard writer.status == .completed else { throw writer.error ?? ScreenVideoWriterError.writerFailed }
    }
}
