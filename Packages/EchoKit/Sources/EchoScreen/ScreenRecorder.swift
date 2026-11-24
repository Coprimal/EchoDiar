import AVFoundation
import CoreMedia
import Foundation
import ScreenCaptureKit
import Synchronization

public enum ScreenRecorderError: Error, Sendable, Equatable {
    /// `start` called twice or after `stop`.
    case invalidState
    /// The display or window of the source is gone.
    case sourceNotFound
    /// Free space of the output volume is under `minimumFreeBytes`.
    case lowDisk(availableBytes: Int64)
}

/// Records one screen source into a video-only HEVC file (`ScreenVideoWriter`), in real time, on the time base given by
/// `clock` (the audio file's: paused spans removed). One `SCStream` at a time, configured as measured in the spike
/// (docs/research/2026-11-23-screen-recording-spike/rec-main.swift). Knows nothing about audio: a stopped video
/// never stops the audio.
///
/// `@unchecked Sendable`: all state lives on a private serial queue, which is also the stream's sample handler queue;
/// the public API hops onto it.
public final class ScreenRecorder: NSObject, SCStreamOutput, SCStreamDelegate, @unchecked Sendable {
    /// Why the video ended before `stop(at:)`. At most one per recording; the recorder appends nothing afterwards.
    public enum EndEvent: Sendable, Equatable {
        /// The stream stopped with an error (window closed, display gone, permission revoked).
        case streamStopped
        /// The video writer failed.
        case writeFailed
        /// Free space of the output volume fell under `minimumFreeBytes`.
        case lowDisk
    }

    public struct Stats: Sendable, Equatable {
        /// Frames written to the file (the padding frame of `stop(at:)` not included).
        public var frames = 0
        /// Complete frames not written: no or a negative file time, or refused by the writer.
        public var dropped = 0
        /// Size of the file, refreshed every 10 s and after `stop(at:)`.
        public var bytes: Int64 = 0
        /// File time, in seconds, of the first and the last written frame.
        public var firstFileTime: Double?
        public var lastFileTime: Double?
        /// Largest difference, in seconds, between a complete frame's presentation time and the host clock at its
        /// arrival; nil before the first complete frame.
        public var maxHostTimeOffset: Double?

        /// Whether frame times were on the host clock (every complete frame within 1 s of now at arrival).
        public var onHostClock: Bool? { maxHostTimeOffset.map { $0 < 1 } }
    }

    /// The file time, in seconds, of a host time in `mach_absolute_time()` ticks; nil drops the frame.
    public typealias Clock = @Sendable (UInt64) -> Double?

    /// Published once per recording at most; finished by `stop(at:)`, or by a `start` that a `stop(at:)` abandoned.
    public let events: AsyncStream<EndEvent>

    public var stats: Stats { statsLock.withLock { $0 } }

    private enum State { case idle, starting, running, paused, stopped }
    /// A `pause()` or `stop(at:)` that arrived while `start` was still preparing (`.starting`).
    private enum Request { case pause, stop }

    private static let timescale: CMTimeScale = 90_000
    private static let diskCheckInterval = DispatchTimeInterval.seconds(10)

    private let queue = DispatchQueue(label: "EchoDiar.ScreenRecorder", qos: .userInitiated)
    private let queueKey = DispatchSpecificKey<Void>()
    private let continuation: AsyncStream<EndEvent>.Continuation
    private let statsLock = Mutex(Stats())

    private var state = State.idle
    private var request: Request?
    private var stream: SCStream?
    private var writer: ScreenVideoWriter?
    private var filter: SCContentFilter?
    private var configuration: SCStreamConfiguration?
    private var clock: Clock?
    private var url: URL?
    private var frameInterval = CMTime.zero
    private var minimumFreeBytes: Int64 = 0
    private var timer: DispatchSourceTimer?
    private var ended = false
    private var lastBuffer: CMSampleBuffer?
    private var lastFileTime = CMTime.invalid

    override public init() {
        (events, continuation) = AsyncStream.makeStream(of: EndEvent.self, bufferingPolicy: .bufferingNewest(4))
        super.init()
        queue.setSpecific(key: queueKey, value: ())
    }

    deinit {
        timer?.cancel()
        continuation.finish()
    }

    // MARK: - Public API

    /// Creates the file at `url` (`.mov` extension: QuickTime, else MPEG-4) and starts capturing `source` at `fps`.
    /// Throws when free space of the output volume is under `minimumFreeBytes`, the source is gone, the writer refuses
    /// the settings or the stream cannot start; the file is then removed. `excludingWindowIDs` applies to display and
    /// area sources. `clock` is called on the recorder's queue for every complete frame.
    ///
    /// While `start` prepares (shareable content, writer), a `pause()` or `stop(at:)` is recorded and honored once the
    /// preparation is done: after a `pause()`, `start` returns with the recorder paused and no stream running
    /// (`resume()` starts one); after a `stop(at:)`, `start` throws `CancellationError` (or the error that ended the
    /// preparation), removes the file and finishes `events`, and the recorder stays stopped. Once the stream is being
    /// started, `pause()` and `stop(at:)` act at once and own the state and the file: if the stream then fails to
    /// start, `start` still throws, but the file is not removed and the recorder keeps its writer until `stop(at:)`
    /// finishes it.
    public func start(
        source: ScreenSource, fps: Int, quality: Double, showsCursor: Bool, showsClicks: Bool, url: URL,
        excludingWindowIDs: [CGWindowID] = [], minimumFreeBytes: Int64, clock: @escaping Clock
    ) async throws {
        try onQueue {
            guard state == .idle else { throw ScreenRecorderError.invalidState }
            state = .starting
        }
        var created = false
        do {
            let folder = url.deletingLastPathComponent()
            if let available = Self.availableCapacity(at: folder), available < minimumFreeBytes {
                throw ScreenRecorderError.lowDisk(availableBytes: available)
            }
            let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
            let (filter, pixelSize, sourceRect) = try Self.filter(for: source, content: content, excluding: excludingWindowIDs)

            let configuration = SCStreamConfiguration()
            configuration.width = pixelSize.width
            configuration.height = pixelSize.height
            if let sourceRect { configuration.sourceRect = sourceRect }
            configuration.scalesToFit = true
            configuration.captureResolution = .best
            configuration.minimumFrameInterval = CMTime(value: 1, timescale: CMTimeScale(fps))
            configuration.showsCursor = showsCursor
            configuration.showMouseClicks = showsClicks
            configuration.queueDepth = 6

            let fileType: AVFileType = url.pathExtension.lowercased() == "mov" ? .mov : .mp4
            let writer = try ScreenVideoWriter(
                url: url, width: pixelSize.width, height: pixelSize.height, fps: fps, quality: quality, fileType: fileType)
            created = true
            let stream = try makeStream(filter: filter, configuration: configuration)
            // A stop requested meanwhile abandons the start (the catch below tears down); a pause ends it paused.
            let paused: Bool = try onQueue {
                guard state == .starting, request != .stop else { throw CancellationError() }
                self.writer = writer
                self.filter = filter
                self.configuration = configuration
                self.clock = clock
                self.url = url
                self.minimumFreeBytes = minimumFreeBytes
                frameInterval = CMTime(value: CMTimeValue(Self.timescale) / CMTimeValue(max(fps, 1)), timescale: Self.timescale)
                let paused = request == .pause
                request = nil
                if paused {
                    state = .paused
                } else {
                    self.stream = stream
                    state = .running
                }
                return paused
            }
            if paused {
                onQueue { startDiskTimer() }
                return
            }
            do {
                try await stream.startCapture()
            } catch {
                onQueue {
                    guard self.stream === stream else { return }
                    self.stream = nil
                    self.writer = nil
                    state = .starting
                }
                throw error
            }
            // A `pause()` or `stop(at:)` during `startCapture()` took the stream; stop it in case its stop came too early.
            if onQueue({ self.stream !== stream }) { try? await stream.stopCapture() }
            onQueue { startDiskTimer() }
        } catch {
            // Once the stream is being started, a `pause()` or `stop(at:)` owns the state and the file.
            let failedStart = onQueue {
                guard state == .starting else { return false }
                if request == .stop {
                    state = .stopped
                    continuation.finish()
                } else {
                    state = .idle
                }
                request = nil
                return true
            }
            if failedStart, created { try? FileManager.default.removeItem(at: url) }
            throw error
        }
    }

    /// Stops the stream; frames until `resume()` are not captured. While `start` prepares, returns at once and makes
    /// `start` end paused (unless a `stop(at:)` was requested too). Otherwise no-op unless recording.
    public func pause() async {
        let stream: SCStream? = onQueue {
            if state == .starting, request == nil { request = .pause }
            guard state == .running else { return nil }
            state = .paused
            let stream = self.stream
            self.stream = nil
            return stream
        }
        try? await stream?.stopCapture()
    }

    /// Starts a new stream with the same filter and configuration. No-op when not paused or once an end event was
    /// emitted. Throws, staying paused, if the stream cannot start. While `start` prepares, cancels a pending `pause()`.
    public func resume() async throws {
        let stream: SCStream? = try onQueue {
            if state == .starting, request == .pause { request = nil }
            guard state == .paused, !ended, let filter, let configuration else { return nil }
            let stream = try makeStream(filter: filter, configuration: configuration)
            self.stream = stream
            state = .running
            return stream
        }
        guard let stream else { return }
        do {
            try await stream.startCapture()
        } catch {
            onQueue {
                if self.stream === stream {
                    self.stream = nil
                    if state == .running { state = .paused }
                }
            }
            throw error
        }
    }

    /// Stops the stream and finishes the file, ending the track at `fileTime` (seconds). When the last written frame is
    /// earlier than `fileTime` minus one frame interval, a copy of it is appended there first, so the sample table
    /// (what ffprobe reads) reaches the end too. Throws when the writer failed. While `start` prepares, returns at once
    /// without a file: `start` then throws, removes its file and finishes `events`. No-op when not started.
    public func stop(at fileTime: Double) async throws {
        let end = Self.time(fileTime)
        let detached: (SCStream?, ScreenVideoWriter)? = onQueue {
            if state == .starting { request = .stop }
            guard state == .running || state == .paused, let writer else { return nil }
            state = .stopped
            timer?.cancel()
            timer = nil
            let padTime = end - frameInterval
            if let lastBuffer, lastFileTime.isValid, lastFileTime < padTime {
                writer.append(lastBuffer, fileTime: padTime)
            }
            let stream = self.stream
            self.stream = nil
            self.writer = nil
            lastBuffer = nil
            return (stream, writer)
        }
        guard let (stream, writer) = detached else { return }
        try? await stream?.stopCapture()
        defer {
            refreshBytes()
            continuation.finish()
        }
        try await writer.finish(at: end)
    }

    // MARK: - SCStreamOutput, SCStreamDelegate

    /// Called on `queue`.
    public func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .screen, stream === self.stream, state == .running, !ended, let writer, let clock,
              sampleBuffer.isValid,
              let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false)
                  as? [[SCStreamFrameInfo: Any]],
              let raw = attachments.first?[.status] as? Int, SCFrameStatus(rawValue: raw) == .complete,
              sampleBuffer.imageBuffer != nil else { return }

        let presentation = sampleBuffer.presentationTimeStamp
        let offset = abs(presentation.seconds - CMClockGetTime(CMClockGetHostTimeClock()).seconds)
        statsLock.withLock { $0.maxHostTimeOffset = max($0.maxHostTimeOffset ?? 0, offset) }

        guard let seconds = clock(CMClockConvertHostTimeToSystemUnits(presentation)), seconds >= 0 else {
            statsLock.withLock { $0.dropped += 1 }
            return
        }
        let fileTime = Self.time(seconds)
        guard writer.append(sampleBuffer, fileTime: fileTime) else {
            statsLock.withLock { $0.dropped += 1 }
            if writer.failed { end(.writeFailed) }
            return
        }
        lastBuffer = sampleBuffer
        lastFileTime = fileTime
        statsLock.withLock {
            $0.frames += 1
            if $0.firstFileTime == nil { $0.firstFileTime = seconds }
            $0.lastFileTime = seconds
        }
    }

    public func stream(_ stream: SCStream, didStopWithError error: any Error) {
        let id = ObjectIdentifier(stream)
        queue.async { [self] in
            guard let current = self.stream, ObjectIdentifier(current) == id, state == .running else { return }
            end(.streamStopped)
        }
    }

    // MARK: - Recorder queue

    private func onQueue<T>(_ body: () throws -> T) rethrows -> T {
        if DispatchQueue.getSpecific(key: queueKey) != nil { return try body() }
        return try queue.sync(execute: body)
    }

    private func makeStream(filter: SCContentFilter, configuration: SCStreamConfiguration) throws -> SCStream {
        let stream = SCStream(filter: filter, configuration: configuration, delegate: self)
        try stream.addStreamOutput(self, type: .screen, sampleHandlerQueue: queue)
        return stream
    }

    /// Emits `event` once and stops the stream; the file is finished by `stop(at:)`.
    private func end(_ event: EndEvent) {
        guard !ended else { return }
        ended = true
        continuation.yield(event)
        stream?.stopCapture { _ in }
        stream = nil
    }

    private func startDiskTimer() {
        guard state == .running || state == .paused, timer == nil else { return }
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + Self.diskCheckInterval, repeating: Self.diskCheckInterval, leeway: .seconds(1))
        timer.setEventHandler { [weak self] in self?.checkDisk() }
        timer.resume()
        self.timer = timer
    }

    private func checkDisk() {
        guard state == .running || state == .paused, let url else { return }
        refreshBytes()
        if !ended, let available = Self.availableCapacity(at: url.deletingLastPathComponent()), available < minimumFreeBytes {
            end(.lowDisk)
        }
    }

    private func refreshBytes() {
        guard let url = onQueue({ self.url }) else { return }
        let size = (try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? NSNumber)?.int64Value ?? 0
        statsLock.withLock { $0.bytes = size }
    }

    // MARK: - Helpers

    private static func time(_ seconds: Double) -> CMTime {
        CMTime(value: CMTimeValue((seconds * Double(timescale)).rounded()), timescale: timescale)
    }

    /// The filter, the output size in pixels (rounded down to even) and the display-local `sourceRect` for `source`.
    private static func filter(for source: ScreenSource, content: SCShareableContent, excluding windowIDs: [CGWindowID])
        throws -> (SCContentFilter, (width: Int, height: Int), CGRect?)
    {
        func display(_ id: CGDirectDisplayID) throws -> SCContentFilter {
            guard let display = content.displays.first(where: { $0.displayID == id }) else {
                throw ScreenRecorderError.sourceNotFound
            }
            let excluded = content.windows.filter { windowIDs.contains($0.windowID) }
            return SCContentFilter(display: display, excludingWindows: excluded)
        }
        func pixels(_ size: CGSize, _ filter: SCContentFilter) -> (width: Int, height: Int) {
            let scale = CGFloat(filter.pointPixelScale)
            return (Int(size.width * scale) / 2 * 2, Int(size.height * scale) / 2 * 2)
        }

        switch source {
        case .window(let id):
            guard let window = content.windows.first(where: { $0.windowID == id }) else {
                throw ScreenRecorderError.sourceNotFound
            }
            let filter = SCContentFilter(desktopIndependentWindow: window)
            return (filter, pixels(filter.contentRect.size, filter), nil)
        case .display(let id):
            let filter = try display(id)
            let size = filter.contentRect.size
            return (filter, pixels(size, filter), CGRect(origin: .zero, size: size))
        case .area(let id, let rect):
            let filter = try display(id)
            return (filter, pixels(rect.size, filter), rect)
        }
    }

    private static func availableCapacity(at url: URL) -> Int64? {
        var url = url
        url.removeAllCachedResourceValues()
        return try? url.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey]).volumeAvailableCapacityForImportantUsage
    }
}
