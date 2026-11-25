import CoreGraphics
import EchoAudio
import EchoCore
import EchoScreen
import Foundation
import Observation

/// The screen video of the current recording: the source chooser's request, the video's state, its `ScreenRecorder`
/// and the outline around what it records. `AppModel` calls it when the recording starts, pauses, resumes and ends; a
/// video that ends early never stops the audio. It only coordinates: shareable content, the writer and the stream run
/// in `ScreenRecorder`, off the main thread.
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

    /// Which control the screen source chooser opens from: the toolbar's Record (a new recording) or the recording
    /// header's screen control (the running recording).
    enum ChooserRequest: Equatable {
        case newRecording, duringRecording
    }

    private(set) var state = State.off
    /// Shown on the recording when its video ended early; nil = nothing to say.
    private(set) var notice: String?
    /// The screen toggle switched the video off during the current recording: no other video starts in it (one video
    /// span per recording). Cleared when the recording ends.
    private(set) var stoppedEarly = false
    /// The source chooser should open from its control, which clears it once the main window shows. Cleared when a
    /// recording starts or ends, and when the main window closes, so a stale request never opens it later.
    var chooserRequest: ChooserRequest?
    /// The current video's source, from `start` until the video ends or is finished; nil without a video.
    private(set) var videoSource: ScreenSource?

    @ObservationIgnored private let engine: RecordingEngine
    /// The current recording's video, from `start` until `finish(at:)` or until it ends early (`end(_:)`).
    @ObservationIgnored private var recorder: ScreenRecorder?
    /// The outline around what the current video records, from `start` until the video ends; shown while it records.
    @ObservationIgnored private var outline: RecordingOutline?
    /// "Portion of the screen"'s selection overlays while they are shown.
    @ObservationIgnored private var areaSelection: AreaSelectionOverlay?
    /// "Entire screen" (several displays) and "Window"'s picking overlays while they are shown.
    @ObservationIgnored private var screenPicker: ScreenPickerOverlay?
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
    /// chevron menu or Settings while it runs).
    @ObservationIgnored private var showsCamera = false
    @ObservationIgnored private var blursCamera = false
    @ObservationIgnored private var showsKeyStrokes = false
    @ObservationIgnored private var showsAllKeys = false
    /// Made on first use, so nothing is created while both options are off.
    @ObservationIgnored private var cameraBubble: CameraBubbleController?
    @ObservationIgnored private var keyStrokes: KeyStrokeHUDController?
    /// A window video's display and its crop of the window there (display-local points, top-left origin), from its
    /// start; both follow the window (`followWindow`), also to another display, and place the overlays.
    @ObservationIgnored private var windowPlace: (display: CGDirectDisplayID, crop: CGRect)?
    /// Reads a window video's window place every 250 ms, from its start until the video ends.
    @ObservationIgnored private var following: Task<Void, Never>?

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

    /// A source picked in the chooser during the recording the engine runs in `folder` (not paused): starts it on the
    /// audio file's clock, so its first frame lands at its real file time. Starts nothing without the screen permission
    /// or when a display or an area's display is gone, so a start that cannot work never takes the recording's one
    /// video span.
    func startDuringRecording(
        _ source: ScreenSource, folder: URL, settings: AppSettings, skipsTranscription: Bool,
        excludingWindowIDs: [CGWindowID]
    ) {
        guard canStartDuringRecording, CGPreflightScreenCaptureAccess(), Self.isOnline(source) else { return }
        start(source: source, folder: folder, settings: settings, skipsTranscription: skipsTranscription,
              excludingWindowIDs: excludingWindowIDs)
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

    /// "Portion of the screen" in the chooser: shows the selection overlays at once (one per screen). A confirmed area
    /// is handed to `onSelect`; Esc selects nothing.
    func selectArea(onSelect: @escaping (ScreenSource) -> Void) {
        guard areaSelection == nil, screenPicker == nil else { return }
        let overlay = AreaSelectionOverlay { [weak self] source in
            self?.areaSelection = nil
            if let source { onSelect(source) }
        }
        areaSelection = overlay
        overlay.show()
    }

    /// "Entire screen" with several displays or "Window" in the chooser: shows the picking overlays at once (one per
    /// screen). The display or window clicked is handed to `onSelect`; Esc picks nothing.
    func pickSource(_ mode: ScreenPickerOverlay.Mode, onSelect: @escaping (ScreenSource) -> Void) {
        guard areaSelection == nil, screenPicker == nil else { return }
        let overlay = ScreenPickerOverlay(mode: mode) { [weak self] source in
            self?.screenPicker = nil
            if let source { onSelect(source) }
        }
        screenPicker = overlay
        overlay.show()
    }

    /// Starts the video of the recording the engine just started in `folder`, on the audio file's clock; it is added
    /// to `session.json` at its first frame. `excludingWindowIDs` (EchoDiar's own windows) applies to display and area
    /// sources, plus the outline and any area selection overlay shown. A window video lets only the window and the
    /// camera bubble and key stroke HUD into the picture: they show from its start, so the filter can include them.
    /// No-op when a video already runs or the engine stopped meanwhile (its end was handled without one).
    func start(
        source: ScreenSource, folder: URL, settings: AppSettings, skipsTranscription: Bool,
        excludingWindowIDs: [CGWindowID]
    ) {
        guard recorder == nil, engine.currentSnapshot().state != .idle else { return }
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
        // Before the capture filter is built (once, reused on resume): its window ID is left out of the picture, so
        // the same window is only hidden and shown again until the video ends.
        outline = RecordingOutline(source: source)
        if case .window(let id) = source { windowPlace = Self.windowPlace(id) }
        setOverlays(settings)
        let engine = engine
        let ownWindowIDs = [outline?.windowID].compactMap { $0 } + (areaSelection?.windowIDs ?? [])
            + (screenPicker?.windowIDs ?? [])
        let excluded: [CGWindowID] = if case .window = source { [] } else { excludingWindowIDs + ownWindowIDs }
        let included: [CGWindowID] = if case .window = source { overlayWindowIDs } else { [] }
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
                    excludingWindowIDs: excluded, includingWindowIDs: included,
                    minimumFreeBytes: RecordingEngine.lowDiskBytes,
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
            if case .window(let id) = source { follow(id, of: recorder) }
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
        chooserRequest = nil
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
        stopFollowing()
        return Self.close(recorder, url: url, at: fileTime, reason: reason)
    }

    /// The camera bubble and key presses options changed in the chevron menu or Settings: applies them to the running
    /// video.
    func setOverlays(_ settings: AppSettings) {
        showsCamera = settings.screenCamera
        blursCamera = settings.screenCameraBlur
        showsKeyStrokes = settings.screenKeyStrokes
        showsAllKeys = settings.screenAllKeys
        syncOverlays()
    }

    /// Hides the camera bubble and the key stroke HUD (removes the event tap, releases the camera) and removes the
    /// outline. Called when the app quits.
    func hideOverlays() {
        cameraBubble?.hide()
        keyStrokes?.hide()
        removeOutline()
        stopFollowing()
    }

    // MARK: - Private

    /// Shows the camera bubble and the key stroke HUD whose option is on while a display or area video records (not
    /// paused), inside the recorded display or area so they are in the picture (they are not in the capture filter's
    /// excluded windows), and while a window video starts or records (not paused), inside the window's crop, where
    /// the window's filter lets them in (`updateIncludedWindows` for one shown after the start); hides them in every
    /// other state. Their `show` does nothing without their permission. The outline shows while the video starts or
    /// records, not while paused.
    private func syncOverlays() {
        let running = state == .recording || (state == .starting && !paused)
        outline?.setVisible(running)
        var place: (display: CGDirectDisplayID, area: CGRect?)?
        switch videoSource {
        case .display(let id) where state == .recording: place = (id, nil)
        case .area(let id, let rect) where state == .recording: place = (id, rect)
        case .window where running: place = windowPlace.map { ($0.display, $0.crop) }
        default: place = nil
        }
        #if DEBUG
        let kind = switch videoSource {
        case .display: "display"
        case .area: "area"
        case .window: "window"
        case nil: "none"
        }
        let why = place == nil ? "no place: not recording this source" : showsKeyStrokes ? "option on" : "option off"
        KeyStrokeHUDController.debugLog("""
            syncOverlays state=\(state) paused=\(paused) source=\(kind) keyStrokes=\(showsKeyStrokes) \
            allKeys=\(showsAllKeys) -> \(place != nil && showsKeyStrokes ? "show" : "hide") (\(why))
            """)
        #endif
        if let place, showsCamera {
            if cameraBubble == nil { cameraBubble = CameraBubbleController() }
            cameraBubble?.show(on: place.display, within: place.area, blur: blursCamera)
        } else {
            cameraBubble?.hide()
        }
        if let place, showsKeyStrokes {
            if keyStrokes == nil { keyStrokes = KeyStrokeHUDController() }
            keyStrokes?.show(on: place.display, within: place.area, allKeys: showsAllKeys)
        } else {
            keyStrokes?.hide()
        }
        // In `.starting` the start's filter takes them; the recorder ignores windows it already has.
        if case .window = videoSource, state == .recording, let recorder, !overlayWindowIDs.isEmpty {
            let ids = overlayWindowIDs
            Task { await recorder.updateIncludedWindows(ids) }
        }
    }

    /// The camera bubble's and the key stroke HUD's windows while they show.
    private var overlayWindowIDs: [CGWindowID] {
        [cameraBubble?.windowID, keyStrokes?.windowID].compactMap { $0 }
    }

    /// Follows a window video's window every 250 ms until the video ends: moves the stream to its new place, on its
    /// display or another one, and the overlays with it. A closed window ends the video, as its stream did when it
    /// captured the window alone.
    private func follow(_ id: CGWindowID, of recorder: ScreenRecorder) {
        following?.cancel()
        following = Task { [weak self] in
            while !Task.isCancelled {
                guard let self, self.recorder === recorder else { return }
                await followWindow(id, of: recorder)
                try? await Task.sleep(for: .milliseconds(250))
            }
        }
    }

    /// Moves the stream to the window's place (`ScreenSource.windowPlace`): a new crop on the recorded display, or the
    /// display its centre moved to with the crop there; the overlays follow onto that display. The place is kept only
    /// once the recorder applied it, so a failed display switch is tried again on the next read. Keeps the last place
    /// while the window is minimised, hidden, on another Space or on no display.
    private func followWindow(_ id: CGWindowID, of recorder: ScreenRecorder) async {
        guard let info = Self.windowInfo(id) else {
            end(.streamStopped)
            return
        }
        guard info.onScreen, let place = windowPlace else { return }
        let displays = Self.activeDisplays()
        guard let next = ScreenSource.windowPlace(
            windowBounds: info.bounds, currentDisplay: displays.firstIndex(of: place.display),
            displays: displays.map { CGDisplayBounds($0) }) else { return }
        let display = displays[next.display], crop = next.crop
        guard display != place.display || crop != place.crop,
              await recorder.updateWindowPlace(display: display, crop: crop),
              self.recorder === recorder, windowPlace != nil else { return }
        windowPlace = (display, crop)
        syncOverlays()
    }

    private func stopFollowing() {
        following?.cancel()
        following = nil
        windowPlace = nil
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
        stopFollowing()
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

    /// A window's display and its crop there, as `ScreenRecorder` picks them at the start; nil when the window is gone
    /// or on no display.
    nonisolated private static func windowPlace(_ id: CGWindowID) -> (display: CGDirectDisplayID, crop: CGRect)? {
        guard let info = windowInfo(id) else { return nil }
        let displays = activeDisplays()
        guard let place = ScreenSource.windowPlace(
            windowBounds: info.bounds, currentDisplay: nil, displays: displays.map { CGDisplayBounds($0) })
        else { return nil }
        return (displays[place.display], place.crop)
    }

    /// A window's bounds (CG global points, top-left origin) and whether it is on screen; nil when it is gone.
    nonisolated private static func windowInfo(_ id: CGWindowID) -> (bounds: CGRect, onScreen: Bool)? {
        guard let info = (CGWindowListCopyWindowInfo([.optionIncludingWindow], id) as? [[String: Any]])?.first,
              let bounds = info[kCGWindowBounds as String] as? NSDictionary,
              let frame = CGRect(dictionaryRepresentation: bounds) else { return nil }
        return (frame, info[kCGWindowIsOnscreen as String] as? Bool == true)
    }

    nonisolated private static func activeDisplays() -> [CGDirectDisplayID] {
        var count: UInt32 = 0
        guard CGGetActiveDisplayList(0, nil, &count) == .success, count > 0 else { return [] }
        var displays = [CGDirectDisplayID](repeating: 0, count: Int(count))
        guard CGGetActiveDisplayList(count, &displays, &count) == .success else { return [] }
        return Array(displays.prefix(Int(count)))
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
