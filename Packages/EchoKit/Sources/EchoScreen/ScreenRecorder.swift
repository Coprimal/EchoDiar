import AVFoundation
import CoreMedia
import Foundation
import ScreenCaptureKit
import Synchronization

public enum ScreenRecorderError: Error, Sendable, Equatable {
    /// `start` called twice or after `stop`; `screenOn` while the screen is on, paused or after `stop`.
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
/// One file per recording: `screenOff(at:)` stops the stream and writes the placeholder frame, which holds until the
/// next real frame; `screenOn(source:...)` starts a new stream, of any source, into the same file at the same output
/// size. Pause and screen off are independent: a recording can be paused with the screen on or off, and `resume()`
/// starts a stream only when the screen is on. A `screenOff(at:)` that comes while `start` or `screenOn` prepares is
/// kept and honoured before their stream would start: that stream never starts.
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
        /// Frames written to the file (the padding frame of `stop(at:)` and placeholder frames not included).
        public var frames = 0
        /// Complete frames not written: no or a negative file time, or refused by the writer; also placeholder frames
        /// the writer did not take.
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
    /// The screen was turned off by `screenOff(at:)`: no stream, whether running or paused, until `screenOn`.
    private var screenOff = false
    /// The file time, in seconds, of a `screenOff(at:)` that came while `start` prepared or a `screenOn` looked up
    /// its source; honoured by them before their stream would start.
    private var pendingOff: Double?
    /// A `screenOn` is looking up its source: from its first check until it installs its stream or fails.
    private var turningOn = false
    private var lastBuffer: CMSampleBuffer?
    private var lastFileTime = CMTime.invalid
    /// The "Screen recording off" frame at the writer's size, rendered once by `start`; nil when it could not be made.
    private var placeholder: CMSampleBuffer?
    /// A window source: its window, the display it is cropped from now (`updateWindowPlace` switches it when the
    /// window moves to another display; `resume()` restarts on it) and EchoDiar's overlay windows let into the picture
    /// next to it. Nil while the screen is off.
    private var windowCapture: (windowID: CGWindowID, display: SCDisplay, included: Set<CGWindowID>)?

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
    /// area sources. A window source records the window's display cropped to the window (`ScreenSource.windowCrop`),
    /// with only that window and `includingWindowIDs` (EchoDiar's overlays) in the picture, at the window's size at
    /// the start; `updateWindowPlace(display:crop:)` follows the window, also to another display, and
    /// `updateIncludedWindows(_:)` adds overlays. Overlays
    /// not listed in the shareable content yet are looked up once more. `clock` is called on the recorder's queue for
    /// every complete frame.
    ///
    /// While `start` prepares (shareable content, writer), a `pause()` or `stop(at:)` is recorded and honored once the
    /// preparation is done: after a `pause()`, `start` returns with the recorder paused and no stream running
    /// (`resume()` starts one); after a `stop(at:)`, `start` throws `CancellationError` (or the error that ended the
    /// preparation), removes the file and finishes `events`, and the recorder stays stopped. A `screenOff(at:)` is kept
    /// too: `start` then returns with the screen off, its placeholder written at that file time, and never starts the
    /// stream (`screenOn` starts one). Once the stream is being started, `pause()`, `screenOff(at:)` and `stop(at:)`
    /// act at once and own the state and the file: if the stream then fails to start, `start` still throws, but the
    /// file is not removed and the recorder keeps its writer until `stop(at:)` finishes it.
    ///
    /// `placeholderText` (already localized) is drawn once on the placeholder frame (`ScreenPlaceholder`), at the
    /// output size, that stands for the spans where the screen was off. With `leadsWithPlaceholder` (the screen turned
    /// on after the recording began), the placeholder is written at file time 0 before the stream starts, so the video
    /// covers the recording from its start; a `stop(at:)` before the first real frame pads with it. Placeholder frames
    /// are not counted in `stats` (`frames`, `firstFileTime`, `lastFileTime`): a file with only placeholders still has
    /// no frames.
    public func start(
        source: ScreenSource, fps: Int, quality: Double, showsCursor: Bool, showsClicks: Bool, url: URL,
        excludingWindowIDs: [CGWindowID] = [], includingWindowIDs: [CGWindowID] = [], minimumFreeBytes: Int64,
        clock: @escaping Clock, placeholderText: String = "Screen recording off", leadsWithPlaceholder: Bool = false
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
            var content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
            if case .window = source, !Self.lists(includingWindowIDs, in: content) {
                content = try await Self.contentAgain()
            }
            let (filter, pixelSize, sourceRect, windowDisplay) = try Self.filter(
                for: source, content: content, excluding: excludingWindowIDs, including: includingWindowIDs)

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
            let placeholder = ScreenPlaceholder.frame(
                width: pixelSize.width, height: pixelSize.height, text: placeholderText)
            if placeholder == nil { NSLog("EchoDiar: screen placeholder frame could not be made") }
            let stream = try makeStream(filter: filter, configuration: configuration)
            // A stop requested meanwhile abandons the start (the catch below tears down); a pause ends it paused and a
            // turn-off with the screen off, and neither starts the stream.
            let captures: Bool = try onQueue {
                guard state == .starting, request != .stop else { throw CancellationError() }
                self.writer = writer
                self.placeholder = placeholder
                self.filter = filter
                self.configuration = configuration
                self.clock = clock
                self.url = url
                self.minimumFreeBytes = minimumFreeBytes
                let off = pendingOff
                pendingOff = nil
                if off == nil, case .window(let id) = source, let windowDisplay {
                    let listed = Set(includingWindowIDs).intersection(content.windows.map(\.windowID))
                    windowCapture = (id, windowDisplay, listed)
                }
                frameInterval = CMTime(value: CMTimeValue(Self.timescale) / CMTimeValue(max(fps, 1)), timescale: Self.timescale)
                if leadsWithPlaceholder { appendPlaceholder(at: .zero) }
                if let off {
                    screenOff = true
                    if lastBuffer !== placeholder { appendOffPlaceholder(at: off) }
                }
                let paused = request == .pause
                request = nil
                state = paused ? .paused : .running
                let captures = !paused && off == nil
                if captures { self.stream = stream }
                return captures
            }
            if !captures {
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
                    self.placeholder = nil
                    lastBuffer = nil
                    lastFileTime = .invalid
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
                pendingOff = nil
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

    /// Stops the stream; frames until `resume()` are not captured. While the screen is off there is no stream to stop.
    /// While `start` prepares, returns at once and makes `start` end paused (unless a `stop(at:)` was requested too).
    /// Otherwise no-op unless recording.
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

    /// Starts a new stream with the same filter and configuration. While the screen is off, only resumes the recording:
    /// no stream starts until `screenOn`. No-op when not paused or once an end event was emitted. Throws, staying
    /// paused, if the stream cannot start. While `start` prepares, cancels a pending `pause()`.
    public func resume() async throws {
        let stream: SCStream? = try onQueue {
            if state == .starting, request == .pause { request = nil }
            guard state == .paused, !ended, let filter, let configuration else { return nil }
            if screenOff {
                state = .running
                return nil
            }
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
        // A `pause()`, `screenOff(at:)` or `stop(at:)` during `startCapture()` took the stream; stop it in case its
        // stop came too early.
        if onQueue({ self.stream !== stream }) { try? await stream.stopCapture() }
    }

    /// Turns the screen off: stops the stream and writes the placeholder frame at `fileTime` (seconds), or just after
    /// the last frame when that is later. The placeholder holds until the next real frame or the end of the file. The
    /// recording goes on, running or paused, until `screenOn` starts a stream again. While `start` prepares (unless a
    /// `stop(at:)` was requested) or a `screenOn` looks up its source, the turn-off is kept and honoured before their
    /// stream would start: it never starts, and the placeholder goes in at `fileTime`. No-op when the screen is
    /// already off, after `stop(at:)` or once an end event was emitted.
    public func screenOff(at fileTime: Double) async {
        let stream: SCStream? = onQueue {
            if (state == .starting && request != .stop) || turningOn {
                if pendingOff == nil { pendingOff = fileTime }
                return nil
            }
            guard state == .running || state == .paused, !ended, !screenOff, writer != nil else { return nil }
            let stream = self.stream
            self.stream = nil
            screenOff = true
            windowCapture = nil
            appendOffPlaceholder(at: fileTime)
            return stream
        }
        try? await stream?.stopCapture()
    }

    /// Turns the screen on again with `source`: starts a new stream into the same file, keeping the output size
    /// (`scalesToFit` letterboxes a source of another shape). `excludingWindowIDs` and `includingWindowIDs` work as in
    /// `start`. Throws `ScreenRecorderError.invalidState` unless recording, not paused, with the screen off, no other
    /// `screenOn` looking up its source and no end event; `CancellationError` when a `pause()`, `screenOff(at:)` or `stop(at:)` came while the source was looked
    /// up. On any failure the screen stays off, with no stream, and the file is untouched (apart from the placeholder
    /// such a `screenOff(at:)` writes if the screen's last frame was not one).
    public func screenOn(
        source: ScreenSource, excludingWindowIDs: [CGWindowID] = [], includingWindowIDs: [CGWindowID] = []
    ) async throws {
        let configuration: SCStreamConfiguration = try onQueue {
            guard state == .running, screenOff, !turningOn, !ended, writer != nil, let current = self.configuration else {
                throw ScreenRecorderError.invalidState
            }
            let configuration = SCStreamConfiguration()
            configuration.width = current.width
            configuration.height = current.height
            configuration.scalesToFit = true
            configuration.captureResolution = current.captureResolution
            configuration.minimumFrameInterval = current.minimumFrameInterval
            configuration.showsCursor = current.showsCursor
            configuration.showMouseClicks = current.showMouseClicks
            configuration.queueDepth = current.queueDepth
            turningOn = true
            return configuration
        }
        let content: SCShareableContent, filter: SCContentFilter, windowDisplay: SCDisplay?, stream: SCStream
        do {
            var listed = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
            if case .window = source, !Self.lists(includingWindowIDs, in: listed) {
                listed = try await Self.contentAgain()
            }
            content = listed
            let sourceRect: CGRect?
            (filter, _, sourceRect, windowDisplay) = try Self.filter(
                for: source, content: content, excluding: excludingWindowIDs, including: includingWindowIDs)
            if let sourceRect { configuration.sourceRect = sourceRect }
            stream = try makeStream(filter: filter, configuration: configuration)
        } catch {
            onQueue {
                turningOn = false
                pendingOff = nil
            }
            throw error
        }
        try onQueue {
            turningOn = false
            // A turn-off during the lookup: the stream never starts; the placeholder of the earlier turn-off holds.
            if let off = pendingOff {
                pendingOff = nil
                if !ended, lastBuffer !== placeholder { appendOffPlaceholder(at: off) }
                throw CancellationError()
            }
            guard state == .running, screenOff, !ended else { throw CancellationError() }
            self.filter = filter
            self.configuration = configuration
            if case .window(let id) = source, let windowDisplay {
                let listed = Set(includingWindowIDs).intersection(content.windows.map(\.windowID))
                windowCapture = (id, windowDisplay, listed)
            } else {
                windowCapture = nil
            }
            self.stream = stream
            screenOff = false
        }
        do {
            try await stream.startCapture()
        } catch {
            // Back off, unless another stream took over meanwhile (a `pause()` then `resume()`).
            onQueue {
                guard self.stream === stream || self.stream == nil else { return }
                self.stream = nil
                screenOff = true
                windowCapture = nil
            }
            throw error
        }
        // A `pause()`, `screenOff(at:)` or `stop(at:)` during `startCapture()` took the stream; stop it in case its stop
        // came too early.
        if onQueue({ self.stream !== stream }) { try? await stream.stopCapture() }
    }

    /// A window source's window moved, was resized or moved to another display: records `displayID` cropped to `crop`
    /// (points local to that display, top-left origin, from `ScreenSource.windowPlace`), keeping the output size
    /// (`scalesToFit` letterboxes a resized window or a display of another size or scale). On the same display only the
    /// crop changes. On another one, the window's filter is rebuilt there (with the overlays let in so far) and applied
    /// with `updateContentFilter`, then the crop with `updateConfiguration`; the filter is kept for `resume()`. Applies
    /// to the running stream and to the one a later `resume()` starts. Returns false only when the switch to another
    /// display failed (shareable content unavailable, the display or the window not listed): the recorder keeps its
    /// display and crop, so the caller can try again. No-op (true) for other sources, before `start` returned, while
    /// the screen is off or after `stop(at:)`. A failed stream update never ends the video.
    @discardableResult
    public func updateWindowPlace(display displayID: CGDirectDisplayID, crop: CGRect) async -> Bool {
        let sameDisplay: Bool = onQueue {
            guard let capture = windowCapture, state == .running || state == .paused, let configuration else {
                return true
            }
            guard capture.display.displayID == displayID else { return false }
            if configuration.sourceRect != crop {
                configuration.sourceRect = crop
                stream?.updateConfiguration(configuration) { _ in }
            }
            return true
        }
        guard !sameDisplay else { return true }
        guard let content = try? await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
        else { return false }
        return onQueue {
            guard state == .running || state == .paused, var capture = windowCapture, let configuration else {
                return true
            }
            guard let display = content.displays.first(where: { $0.displayID == displayID }),
                  let window = content.windows.first(where: { $0.windowID == capture.windowID }) else { return false }
            capture.display = display
            windowCapture = capture
            let filter = Self.windowFilter(window, on: display, including: capture.included, content: content)
            self.filter = filter
            configuration.sourceRect = crop
            stream?.updateContentFilter(filter) { _ in }
            stream?.updateConfiguration(configuration) { _ in }
            return true
        }
    }

    /// A window source: lets `ids` (EchoDiar's overlay windows) into the picture next to the window, on the running
    /// stream and on the one `resume()` starts; windows let in before stay in. No-op for other sources, before `start`
    /// returned, while the screen is off or after `stop(at:)`. A failed update never ends the video.
    public func updateIncludedWindows(_ ids: [CGWindowID]) async {
        guard onQueue({ windowCapture.map { !Set(ids).isSubset(of: $0.included) } ?? false }),
              var content = try? await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
        else { return }
        if !Self.lists(ids, in: content), let again = try? await Self.contentAgain() { content = again }
        onQueue {
            guard state == .running || state == .paused, var capture = windowCapture,
                  let window = content.windows.first(where: { $0.windowID == capture.windowID }) else { return }
            capture.included.formUnion(ids.filter { id in content.windows.contains { $0.windowID == id } })
            capture.display = content.displays.first { $0.displayID == capture.display.displayID } ?? capture.display
            windowCapture = capture
            let filter = Self.windowFilter(window, on: capture.display, including: capture.included, content: content)
            self.filter = filter
            stream?.updateContentFilter(filter) { _ in }
        }
    }

    /// Stops the stream and finishes the file, ending the track at `fileTime` (seconds). When the last written frame is
    /// earlier than `fileTime` minus one frame interval, a copy of it is appended there first, so the sample table
    /// (what ffprobe reads) reaches the end too; while the screen is off, that frame is the placeholder. Throws when the
    /// writer failed. While `start` prepares, returns at once without a file: `start` then throws, removes its file and
    /// finishes `events`. No-op when not started.
    public func stop(at fileTime: Double) async throws {
        let end = Self.time(fileTime)
        let detached: (SCStream?, ScreenVideoWriter)? = onQueue {
            pendingOff = nil
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
            placeholder = nil
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

    /// Appends the placeholder at `fileTime` and makes it the frame `stop(at:)` pads with. While the encoder is not
    /// ready, tries again for about 100 ms; not when `fileTime` is not after the last frame or the writer failed. A
    /// placeholder not appended is logged and counted in `dropped`, and never ends the video.
    @discardableResult
    private func appendPlaceholder(at fileTime: CMTime) -> Bool {
        guard let writer, let placeholder else { return false }
        for attempt in 1...20 {
            if writer.append(placeholder, fileTime: fileTime) {
                lastBuffer = placeholder
                lastFileTime = fileTime
                return true
            }
            guard attempt < 20, !writer.failed, fileTime.isNumeric, !lastFileTime.isValid || fileTime > lastFileTime
            else { break }
            usleep(5_000)
        }
        NSLog("EchoDiar: screen placeholder frame not written at %.3f s", fileTime.seconds)
        statsLock.withLock { $0.dropped += 1 }
        return false
    }

    /// Appends the placeholder for a screen turned off at `fileTime` (seconds), or just after the last frame when that
    /// is later.
    private func appendOffPlaceholder(at fileTime: Double) {
        var time = Self.time(fileTime)
        if lastFileTime.isValid {
            time = CMTimeMaximum(time, lastFileTime + CMTime(value: 1, timescale: Self.timescale))
        }
        appendPlaceholder(at: time)
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

    /// Whether every window of `ids` is in `content`.
    private static func lists(_ ids: [CGWindowID], in content: SCShareableContent) -> Bool {
        let listed = Set(content.windows.map(\.windowID))
        return ids.allSatisfy { listed.contains($0) }
    }

    /// The shareable content again, a moment later: a window just ordered front may not be listed yet.
    private static func contentAgain() async throws -> SCShareableContent {
        try await Task.sleep(for: .milliseconds(250))
        return try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
    }

    /// `display` with only `window` and the listed windows of `ids` in the picture: other apps' windows over the
    /// window are not captured.
    private static func windowFilter(
        _ window: SCWindow, on display: SCDisplay, including ids: Set<CGWindowID>, content: SCShareableContent
    ) -> SCContentFilter {
        let overlays = content.windows.filter { ids.contains($0.windowID) && $0.windowID != window.windowID }
        return SCContentFilter(display: display, including: [window] + overlays)
    }

    /// The filter, the output size in pixels (rounded down to even), the display-local `sourceRect` for `source` and,
    /// for a window, the display it is cropped from.
    private static func filter(
        for source: ScreenSource, content: SCShareableContent, excluding windowIDs: [CGWindowID],
        including includedIDs: [CGWindowID]
    ) throws -> (SCContentFilter, (width: Int, height: Int), CGRect?, SCDisplay?) {
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
            let frames = content.displays.map(\.frame)
            guard let window = content.windows.first(where: { $0.windowID == id }),
                  let index = ScreenSource.windowDisplayIndex(windowBounds: window.frame, displays: frames),
                  let crop = ScreenSource.windowCrop(windowBounds: window.frame, on: frames[index], displays: frames)
            else { throw ScreenRecorderError.sourceNotFound }
            let display = content.displays[index]
            let filter = windowFilter(window, on: display, including: Set(includedIDs), content: content)
            return (filter, pixels(window.frame.size, filter), crop, display)
        case .display(let id):
            let filter = try display(id)
            let size = filter.contentRect.size
            return (filter, pixels(size, filter), CGRect(origin: .zero, size: size), nil)
        case .area(let id, let rect):
            let filter = try display(id)
            return (filter, pixels(rect.size, filter), rect, nil)
        }
    }

    private static func availableCapacity(at url: URL) -> Int64? {
        var url = url
        url.removeAllCachedResourceValues()
        return try? url.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey]).volumeAvailableCapacityForImportantUsage
    }
}
