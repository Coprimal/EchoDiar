import CoreGraphics
import EchoAudio
import EchoCore
import EchoScreen
import Foundation
import Observation

/// The screen video of the current recording: the armed source (memory only), the video's state and its
/// `ScreenRecorder`. `AppModel` calls it when the recording starts, pauses, resumes and ends; a video that ends early
/// never stops the audio. It only coordinates: shareable content, the writer and the stream run in `ScreenRecorder`,
/// off the main thread.
@MainActor
@Observable
final class ScreenRecordingController {
    /// Why the video ended before the recording.
    enum EndReason: Equatable, Sendable {
        case startFailed, streamStopped, writeFailed, lowDisk, resumeFailed
    }

    enum State: Equatable {
        case off, starting, recording, paused
        case ended(EndReason)
    }

    /// A video whose file is closed.
    struct Outcome: Sendable {
        var stats: ScreenRecorder.Stats
        var endReason: EndReason?
        /// Why the file could not be finished; nil when it was.
        var finishError: String?
    }

    /// What the next recording started with Record also captures; nil = sound only. Memory only, and cleared once any
    /// recording starts: each recording is armed explicitly (a display or an area stays remembered in the
    /// settings' `screenSource` for a one-click re-arm).
    var armedSource: ScreenSource?
    /// The armed source's name (a display's, a window's title) for the screen control's tooltip; nil when unknown.
    private(set) var armedName: String?
    private(set) var state = State.off
    /// Shown on the recording when its video ended early; nil = nothing to say.
    private(set) var notice: String?
    /// The screen toggle switched the video off during the current recording: no other video starts in it (one video
    /// span per recording). Cleared when the recording ends.
    private(set) var stoppedEarly = false
    /// The screen toggle found no source to start: the recording header's screen control opens the source popover and
    /// clears it.
    var sourcePickerRequested = false
    /// The current video's source, from `start` until the video ends or is finished; nil without a video.
    private(set) var videoSource: ScreenSource?

    @ObservationIgnored private let engine: RecordingEngine
    /// The current recording's video, from `start` until `finish(at:)` or until it ends early (`end(_:)`).
    @ObservationIgnored private var recorder: ScreenRecorder?
    /// The outline around the current recording's area, while its video runs.
    @ObservationIgnored private var outline: AreaOutlineWindow?
    /// "Select area…"'s overlays while they are shown.
    @ObservationIgnored private var areaSelection: AreaSelectionOverlay?
    @ObservationIgnored private var videoURL: URL?
    /// The recording is paused (asked while the video was starting or running).
    @ObservationIgnored private var paused = false
    @ObservationIgnored private var endReason: EndReason?
    /// The recording was marked "screen only" when it started.
    @ObservationIgnored private var skipsTranscription = false
    /// The video switched off or ended early during the recording, while its file is finished; handed to the
    /// recording's end.
    @ObservationIgnored private var stopping: Task<Outcome, Never>?
    /// The video that ended early in the current recording, until the recording ends: its first frame may still be
    /// announced to `session.json`.
    @ObservationIgnored private var endedRecorder: ScreenRecorder?
    /// The current video's camera bubble and key stroke options (from the settings at its start, or changed in the
    /// source popover while it runs).
    @ObservationIgnored private var showsCamera = false
    @ObservationIgnored private var showsKeyStrokes = false
    @ObservationIgnored private var showsAllKeys = false
    /// Made on first use, so nothing is created while both options are off.
    @ObservationIgnored private var cameraBubble: CameraBubbleController?
    @ObservationIgnored private var keyStrokes: KeyStrokeHUDController?

    init(engine: RecordingEngine) {
        self.engine = engine
    }

    /// A video is starting or running in the current recording (paused with it included).
    var isOn: Bool {
        switch state {
        case .starting, .recording, .paused: true
        case .off, .ended: false
        }
    }

    /// The screen toggle can still start a video in the current recording: none ran in it yet (one video span per
    /// recording: it may start late and stop early, never restart).
    var canStartDuringRecording: Bool { state == .off && !stoppedEarly }

    /// The screen toggle switched on during the recording the engine runs in `folder` (not paused): starts the armed
    /// source, else the remembered display or area while its display is online, on the audio file's clock, so its
    /// first frame lands at its real file time. Returns false, starting nothing, when there is no such source or no
    /// screen permission (the source popover then asks), so a start that cannot work never takes the recording's one
    /// video span.
    func startDuringRecording(
        folder: URL, settings: AppSettings, skipsTranscription: Bool, excludingWindowIDs: [CGWindowID]
    ) -> Bool {
        guard canStartDuringRecording else { return true }
        guard CGPreflightScreenCaptureAccess(),
              let source = armedSource ?? settings.screenSource.map(Self.source),
              Self.isOnline(source) else { return false }
        start(source: source, folder: folder, settings: settings, skipsTranscription: skipsTranscription,
              excludingWindowIDs: excludingWindowIDs)
        return true
    }

    /// The screen toggle switched off during the recording: finishes the video at `fileTime`, the audio file's current
    /// time; no other video starts in this recording, even when it was switched off before its first frame. The
    /// recording's end waits for the file (`finish(at:)`) and logs a finish error.
    func stopDuringRecording(at fileTime: Double) {
        let current = recorder
        guard isOn, let task = finish(at: fileTime) else { return }
        // Lets `announce` still write the video to the session when it ends between the first frame and the size lookup.
        endedRecorder = current
        stopping = task
        stoppedEarly = true
    }

    /// Arms `source` for the next recording started with Record; nil = sound only. Returns what the settings'
    /// `screenSource` remembers: a display or an area. Every source is armed for the next recording only; a window is
    /// not remembered (its ID does not survive), so nil.
    @discardableResult
    func arm(_ source: ScreenSource?, name: String? = nil) -> RememberedScreenSource? {
        armedSource = source
        armedName = source == nil ? nil : name
        return source.flatMap(Self.remembered)
    }

    /// "Select area…" in the source popover: shows the selection overlays at once (one per screen). A confirmed area is
    /// armed and handed to `onSelect` with what the settings remember; Esc arms nothing.
    func selectArea(onSelect: @escaping (RememberedScreenSource) -> Void) {
        guard areaSelection == nil else { return }
        let overlay = AreaSelectionOverlay { [weak self] source in
            guard let self else { return }
            areaSelection = nil
            guard let source, let remembered = arm(source, name: String(localized: "the selected area")) else { return }
            onSelect(remembered)
        }
        areaSelection = overlay
        overlay.show()
    }

    static func remembered(_ source: ScreenSource) -> RememberedScreenSource? {
        switch source {
        case .display(let id): RememberedScreenSource(displayID: id)
        case .area(let id, let rect): RememberedScreenSource(displayID: id, area: rect)
        case .window: nil
        }
    }

    static func source(_ remembered: RememberedScreenSource) -> ScreenSource {
        if let area = remembered.area { .area(display: remembered.displayID, rect: area) } else { .display(remembered.displayID) }
    }

    /// Starts the video of the recording the engine just started in `folder`, on the audio file's clock; it is added
    /// to `session.json` at its first frame. `excludingWindowIDs` (EchoDiar's own windows) applies to display and area
    /// sources, plus the area's outline and any area selection overlay shown. Disarms `source` when it is the armed
    /// one. No-op when a video already runs or the engine stopped meanwhile (its end was handled without one).
    func start(
        source: ScreenSource, folder: URL, settings: AppSettings, skipsTranscription: Bool,
        excludingWindowIDs: [CGWindowID]
    ) {
        guard recorder == nil, engine.currentSnapshot().state != .idle else { return }
        // Each recording is armed explicitly.
        if armedSource == source { arm(nil) }
        let recorder = ScreenRecorder()
        let url = folder.appendingPathComponent(ScreenSource.videoFileName)
        self.recorder = recorder
        videoURL = url
        paused = false
        endReason = nil
        notice = nil
        state = .starting
        self.skipsTranscription = skipsTranscription
        videoSource = source
        setOverlays(settings)
        let engine = engine
        if case .area(let display, let rect) = source { outline = AreaOutlineWindow(display: display, rect: rect) }
        let ownWindowIDs = [outline?.windowID].compactMap { $0 } + (areaSelection?.windowIDs ?? [])
        let excluded: [CGWindowID] = if case .window = source { [] } else { excludingWindowIDs + ownWindowIDs }
        let video = RecordingSession.Video(
            fileName: ScreenSource.videoFileName, source: Self.kind(of: source), fps: settings.screenFPS,
            quality: settings.screenQuality, width: 0, height: 0)
        // Weak: a recorder whose start failed never finishes `events`; its deinit does.
        let events = recorder.events
        Task { [weak self, weak recorder] in
            for await event in events {
                if let recorder { self?.ended(event, of: recorder) }
            }
        }
        Task { [weak self] in
            do {
                try await recorder.start(
                    source: source, fps: settings.screenFPS, quality: settings.screenQuality,
                    showsCursor: settings.screenShowsCursor, showsClicks: settings.screenShowsClicks, url: url,
                    excludingWindowIDs: excluded, minimumFreeBytes: RecordingEngine.lowDiskBytes,
                    clock: { engine.fileTime(atHostTime: $0) })
            } catch {
                // A `finish(at:)` during the start took the recorder: nothing to say.
                guard let self, self.recorder === recorder else { return }
                NSLog("EchoDiar: could not start the screen recording: \(error)")
                end(.startFailed)
                return
            }
            guard let self, self.recorder === recorder, state == .starting else { return }
            state = paused ? .paused : .recording
            syncOverlays()
            await announce(video, source: source, of: recorder)
        }
    }

    /// Before the engine pauses: stops the stream (a pause while the video starts makes it start paused).
    func pause() async {
        guard let recorder, state == .starting || state == .recording else { return }
        paused = true
        if state == .recording { state = .paused }
        syncOverlays()
        await recorder.pause()
    }

    /// After the engine resumed: starts a new stream. A failure ends the video; the audio goes on.
    func resume() async {
        guard let recorder, paused else { return }
        paused = false
        guard state == .starting || state == .paused else { return }
        if state == .paused { state = .recording }
        syncOverlays()
        do {
            try await recorder.resume()
        } catch {
            guard self.recorder === recorder else { return }
            NSLog("EchoDiar: could not resume the screen recording: \(error)")
            end(.resumeFailed)
        }
    }

    /// Detaches the current video at once (a next recording starts its own) and finishes its file at `fileTime`, the
    /// audio file's length, so both end together; nil when the recording had no video. A file without any frame is
    /// removed. A video switched off or ended earlier in the recording returns its own finishing task.
    func finish(at fileTime: Double) -> Task<Outcome, Never>? {
        sourcePickerRequested = false
        let reason = endReason
        paused = false
        endReason = nil
        notice = nil
        state = .off
        videoSource = nil
        syncOverlays()
        guard let recorder else {
            let stopping = stopping
            self.stopping = nil
            endedRecorder = nil
            stoppedEarly = false
            return stopping
        }
        let url = videoURL
        self.recorder = nil
        videoURL = nil
        removeOutline()
        return Self.close(recorder, url: url, at: fileTime, reason: reason)
    }

    /// The camera bubble and key presses options changed in the source popover: applies them to the running video.
    func setOverlays(_ settings: AppSettings) {
        showsCamera = settings.screenCamera
        showsKeyStrokes = settings.screenKeyStrokes
        showsAllKeys = settings.screenAllKeys
        syncOverlays()
    }

    /// Hides the camera bubble and the key stroke HUD (removes the event tap, releases the camera). Called when the app
    /// quits.
    func hideOverlays() {
        cameraBubble?.hide()
        keyStrokes?.hide()
    }

    // MARK: - Private

    /// Shows the camera bubble and the key stroke HUD whose option is on while a display or area video records (not
    /// paused), inside the recorded display or area so they are in the picture (they are not in the capture filter's
    /// excluded windows); hides them in every other state. Their `show` does nothing without their permission.
    private func syncOverlays() {
        var place: (display: CGDirectDisplayID, area: CGRect?)?
        if state == .recording {
            switch videoSource {
            case .display(let id): place = (id, nil)
            case .area(let id, let rect): place = (id, rect)
            case .window, nil: place = nil
            }
        }
        if let place, showsCamera {
            if cameraBubble == nil { cameraBubble = CameraBubbleController() }
            cameraBubble?.show(on: place.display, within: place.area)
        } else {
            cameraBubble?.hide()
        }
        if let place, showsKeyStrokes {
            if keyStrokes == nil { keyStrokes = KeyStrokeHUDController() }
            keyStrokes?.show(on: place.display, within: place.area, allKeys: showsAllKeys)
        } else {
            keyStrokes?.hide()
        }
    }

    private func ended(_ event: ScreenRecorder.EndEvent, of recorder: ScreenRecorder) {
        guard self.recorder === recorder else { return }
        switch event {
        case .streamStopped: end(.streamStopped)
        case .writeFailed: end(.writeFailed)
        case .lowDisk: end(.lowDisk)
        }
    }

    /// The video ended on its own: its file is finished at the audio file's current time (not padded with its last
    /// frame up to the recording's end), and the recording's end waits for it (`finish(at:)`).
    private func end(_ reason: EndReason) {
        guard endReason == nil, let recorder else { return }
        endReason = reason
        state = .ended(reason)
        notice = Self.notice(for: reason, skipsTranscription: skipsTranscription)
        videoSource = nil
        syncOverlays()
        removeOutline()
        let url = videoURL
        self.recorder = nil
        videoURL = nil
        endedRecorder = recorder
        // The frames' clock: the written audio (`elapsed`) lags it while recording.
        stopping = Self.close(
            recorder, url: url, at: engine.fileTime(atHostTime: mach_absolute_time()) ?? engine.currentSnapshot().elapsed,
            reason: reason)
    }

    /// Finishes `recorder`'s file at `fileTime`; a file without any frame is removed.
    private static func close(
        _ recorder: ScreenRecorder, url: URL?, at fileTime: Double, reason: EndReason?
    ) -> Task<Outcome, Never> {
        Task.detached(priority: .userInitiated) {
            var finishError: String?
            do {
                try await recorder.stop(at: fileTime)
            } catch {
                finishError = String(describing: error)
            }
            let stats = recorder.stats
            if stats.frames == 0, let url { try? FileManager.default.removeItem(at: url) }
            return Outcome(stats: stats, endReason: reason, finishError: finishError)
        }
    }

    private func removeOutline() {
        outline?.close()
        outline = nil
    }

    /// Adds the video to `session.json` once its first frame is in the file; nothing when it ends without one.
    private func announce(_ video: RecordingSession.Video, source: ScreenSource, of recorder: ScreenRecorder) async {
        while recorder.stats.firstFileTime == nil {
            guard self.recorder === recorder, endReason == nil else { return }
            try? await Task.sleep(for: .milliseconds(100))
        }
        let size = await Task.detached(priority: .utility) { Self.pixelSize(of: source) }.value
        // A video that ended early meanwhile still has its frames in this recording's file.
        guard self.recorder === recorder || endedRecorder === recorder else { return }
        var video = video
        video.width = size.width
        video.height = size.height
        engine.setVideo(video)
    }

    private static func notice(for reason: EndReason, skipsTranscription: Bool) -> String {
        switch reason {
        case .startFailed where skipsTranscription:
            String(localized: """
                The screen could not be recorded. This recording will not be transcribed automatically; you can \
                transcribe it later.
                """)
        case .startFailed: String(localized: "The screen could not be recorded. The sound is still recording.")
        case .streamStopped:
            String(localized: "Screen recording stopped: the screen or window is no longer available. The sound is still recording.")
        case .writeFailed: String(localized: "Screen recording stopped: could not write the video. The sound is still recording.")
        case .lowDisk: String(localized: "Screen recording stopped: less than 1 GB of disk space left. The sound is still recording.")
        case .resumeFailed: String(localized: "Screen recording could not resume. The sound is still recording.")
        }
    }

    /// A display or an area's display is connected; a window is checked by the recorder when it starts.
    private static func isOnline(_ source: ScreenSource) -> Bool {
        switch source {
        case .display(let id), .area(let id, _): CGDisplayIsOnline(id) != 0
        case .window: true
        }
    }

    private static func kind(of source: ScreenSource) -> RecordingSession.Video.Source {
        switch source {
        case .display: .display
        case .window: .window
        case .area: .area
        }
    }

    /// The video's size in pixels as `ScreenRecorder` derives it: the source's size in points times its display's
    /// scale, rounded down to even numbers; 0 x 0 when the source is gone.
    nonisolated private static func pixelSize(of source: ScreenSource) -> (width: Int, height: Int) {
        func scale(_ display: CGDirectDisplayID) -> CGFloat {
            guard let mode = CGDisplayCopyDisplayMode(display), mode.width > 0 else { return 1 }
            return CGFloat(mode.pixelWidth) / CGFloat(mode.width)
        }
        func pixels(_ size: CGSize, _ scale: CGFloat) -> (width: Int, height: Int) {
            (Int(size.width * scale) / 2 * 2, Int(size.height * scale) / 2 * 2)
        }
        switch source {
        case .display(let id):
            return pixels(CGDisplayBounds(id).size, scale(id))
        case .area(let id, let rect):
            return pixels(rect.size, scale(id))
        case .window(let id):
            guard let info = (CGWindowListCopyWindowInfo([.optionIncludingWindow], id) as? [[String: Any]])?.first,
                  let bounds = info[kCGWindowBounds as String] as? NSDictionary,
                  let frame = CGRect(dictionaryRepresentation: bounds) else { return (0, 0) }
            var display: CGDirectDisplayID = 0
            var count: UInt32 = 0
            CGGetDisplaysWithPoint(CGPoint(x: frame.midX, y: frame.midY), 1, &display, &count)
            return pixels(frame.size, count > 0 ? scale(display) : 1)
        }
    }
}
