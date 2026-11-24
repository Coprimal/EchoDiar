#if DEBUG
import AppKit
import AVFoundation
import EchoAudio
import EchoCore
import EchoScreen
import Foundation
import WebKit

// MARK: - Dispatch

extension AppModel {
    /// Runs the DEBUG `--measure-screen` harness (see `ScreenHarness`); false when it was not asked for. The settings,
    /// library and index already live in `--out` (`AppModel.init`); the switches that would compress, transcribe,
    /// prompt or delete anything are set off explicitly, so nothing but the harness touches the recording.
    func runScreenHarness(settingsDirectory: URL, transcriberStarted: @escaping @MainActor () -> Bool) -> Bool {
        guard ScreenHarness.isRequested else { return false }
        updateSettings {
            $0.autoTranscribe = false
            $0.transcribeWhileRecording = false
            $0.endPromptNotification = false
            $0.autoDeleteEnabled = false
            $0.deleteAudioAfterTranscription = false
        }
        let harness = ScreenHarness(
            model: self, settingsDirectory: settingsDirectory, transcriberStarted: transcriberStarted)
        ScreenHarness.current = harness
        Task { await harness.run() }
        return true
    }
}

// MARK: - Harness

/// DEBUG harness for screen recording, run by `scripts/measure-screen.sh` (always with `--background`):
///
/// `--measure-screen <static|slides|scroll|meeting|video> --target window|display|area:x,y,w,h --seconds N
/// --out <dir> --stage <screen-stage.html> --clip <mp4> [--fps 15] [--quality 0.75] [--pause-at S] [--resume-at S]
/// [--no-video] [--no-cursor] [--sync-marks] [--stage-only] [--mov] [--any-display] [--via-model] [--no-transcribe]
/// [--resize-at S]`
///
/// Copies the stage page and the clip into `<out>/stage/`, shows the page on the built-in display (a titled 1440x900
/// window for `window`; a borderless `.screenSaver` window covering the whole display for `display` and `area`, the
/// area in points, top-left, local to that display), then records `seconds` with the real pipeline:
/// `RecordingEngine.start(target: .global, root: <out>, probe: true)` with the microphone muted at once, plus a
/// `ScreenRecorder` on the engine's file clock. The session folder keeps `session.json`, `audio.caf` and the video;
/// `<out>/result.json` holds the numbers. `--seconds` counts from the moment the video started (the engine starts
/// just before it); `--pause-at` and `--resume-at` count from there too and pause or resume both.
/// `--stage-only` only shows the stage for `seconds` (printing `WINDOWID <n>`), for Screenshot baselines.
/// `--via-model` records through the app's own path instead: the source armed on `AppModel.screenRecording`, Record
/// (`toggleRecording`, not brought forward), `togglePause` and Stop, the engine on `<out>` as a probe session with no
/// live transcriber and nothing finished after Stop (`AppModel.harnessRecordingRoot`); `--fps`, `--quality` and
/// `--no-cursor` go through the settings, and `--no-transcribe` turns `screenTranscribes` off ("screen only").
/// `--resize-at S` (window target only) shrinks the stage window to `resizedSize`, top-left kept, on the same display,
/// S seconds after the video started; `result.json` reports the actual time as `resizedAt` and the new frame.
///
/// `--measure-screen preflight --out <dir> --stage <page> --clip <mp4>` checks the grants and the instruments and
/// deletes everything it recorded (see `preflight()`).
///
/// Exit codes (`Code`): 0 ok, 1 usage or pipeline error, 2 another EchoDiar runs, 3 no built-in 2x display (or the
/// stage is not on it), 4 the display/area stage does not cover the display, 5 a video run wrote no frame, 6 too few
/// frames on moving content, 7 the clip does not play, 8 `--out` missing or unusable (before any store is built).
/// Preflight only: 10 to 16, one per check, each with a `PREFLIGHT FAIL <name>` line.
@MainActor
final class ScreenHarness: NSObject, WKNavigationDelegate {
    nonisolated static let argument = "--measure-screen"

    enum Code {
        static let ok: Int32 = 0
        static let failed: Int32 = 1
        static let otherInstance: Int32 = 2
        static let noDisplay: Int32 = 3
        static let notCovered: Int32 = 4
        static let noFrames: Int32 = 5
        static let fewFrames: Int32 = 6
        static let clipNotPlaying: Int32 = 7
        static let badOut: Int32 = 8
        static let stageMissing: Int32 = 10
        static let screenAccess: Int32 = 11
        static let captureNoFrame: Int32 = 12
        static let engineStart: Int32 = 13
        static let noFileTime: Int32 = 14
        static let beepNotPlayed: Int32 = 15
        static let systemAudioSilent: Int32 = 16
    }

    enum Target: Equatable {
        case window, display
        case area(CGRect)
    }

    static let scenarios: Set<String> = ["static", "slides", "scroll", "meeting", "video"]
    /// Scenarios with moving content, where exit 6 applies.
    static let motionScenarios: Set<String> = ["scroll", "meeting", "video"]
    /// The stage window of `--target window`, frame size in points (title bar included).
    static let windowSize = NSSize(width: 1440, height: 900)
    /// The stage window's frame size after `--resize-at`, in points.
    static let resizedSize = NSSize(width: 1200, height: 750)
    /// The system channel must peak above this in preflight (a denied tap delivers silence).
    static let silenceThresholdDB = -40.0

    /// Keeps the running harness alive.
    static var current: ScreenHarness?

    nonisolated static var isRequested: Bool { ProcessInfo.processInfo.arguments.contains(argument) }

    /// `--out` of a `--measure-screen` launch, created if needed; nil without `--measure-screen`. Prints the reason and
    /// exits 8 when `--out` is missing or is not a folder that exists or can be created. Called by `AppModel.init`
    /// before any store is built.
    nonisolated static func isolatedDirectory() -> URL? {
        guard isRequested else { return nil }
        guard let path = value(after: "--out"), !path.isEmpty, !path.hasPrefix("--") else {
            die(Code.badOut, "error: --measure-screen needs --out <dir>")
        }
        let url = URL(fileURLWithPath: (path as NSString).expandingTildeInPath, isDirectory: true).standardizedFileURL
        do {
            try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        } catch {
            die(Code.badOut, "error: --out \(url.path) cannot be created: \(error.localizedDescription)")
        }
        var isFolder: ObjCBool = false
        guard FileManager.default.fileExists(atPath: url.path, isDirectory: &isFolder), isFolder.boolValue,
              FileManager.default.isWritableFile(atPath: url.path) else {
            die(Code.badOut, "error: --out \(url.path) is not a writable folder")
        }
        return url
    }

    // MARK: Options

    private let model: AppModel
    private let engine: RecordingEngine
    private let settingsDirectory: URL
    private let transcriberStarted: @MainActor () -> Bool
    private let command: String
    private let out: URL
    private let target: Target
    private let targetName: String
    private let seconds: Double
    private let stagePage: URL?
    private let clip: URL?
    private let fps: Int
    private let quality: Double
    private let pauseAt: Double?
    private let resumeAt: Double?
    private let resizeAt: Double?
    private let noVideo: Bool
    private let noCursor: Bool
    private let syncMarks: Bool
    private let stageOnly: Bool
    private let mov: Bool
    private let anyDisplay: Bool
    private let viaModel: Bool
    private let noTranscribe: Bool

    // MARK: Run state

    private var fields: [String: Any] = [:]
    private var stageFolder: URL?
    private var window: NSWindow?
    private var web: WKWebView?
    private var loadContinuation: CheckedContinuation<Bool, Never>?
    private var recorder: ScreenRecorder?
    /// The engine's session folder while (and after) a run records.
    private var sessionFolder: URL?
    private var recording = false
    private var videoStartFileTime: Double?
    private var endEvent: ScreenRecorder.EndEvent?
    /// `--via-model`: what the snapshot consumer handed over once the video was finished.
    private var modelEnded: (folder: URL?, video: ScreenRecordingController.Outcome?)?
    private var modelEndReason: ScreenRecordingController.EndReason?
    private var maxFootprint: UInt64 = 0
    private var sampler: Task<Void, Never>?
    /// Preflight: folders to delete before exiting.
    private var scratch: [URL] = []
    private var activity: (any NSObjectProtocol)?

    private var videoFileName: String { mov ? "video.mov" : ScreenSource.videoFileName }

    init(model: AppModel, settingsDirectory: URL, transcriberStarted: @escaping @MainActor () -> Bool) {
        self.model = model
        engine = model.engine
        self.settingsDirectory = settingsDirectory
        self.transcriberStarted = transcriberStarted
        let arguments = ProcessInfo.processInfo.arguments
        let usage = "usage: --measure-screen <static|slides|scroll|meeting|video> --target window|display|area:x,y,w,h "
            + "--seconds N --out <dir> --stage <screen-stage.html> --clip <mp4> [--fps N] [--quality Q] "
            + "[--pause-at S] [--resume-at S] [--no-video] [--no-cursor] [--sync-marks] [--stage-only] [--mov] "
            + "[--any-display] [--via-model] [--no-transcribe] [--resize-at S] | --measure-screen preflight --out <dir> "
            + "--stage <page> --clip <mp4>"
        // `isolatedDirectory` already exited 8 unless `--out` is a usable folder.
        guard let out = Self.isolatedDirectory() else { Self.die(Code.badOut, "error: --out is missing") }
        self.out = out
        guard let command = Self.value(after: Self.argument), command == "preflight" || Self.scenarios.contains(command)
        else { Self.die(Code.failed, usage) }
        self.command = command
        stagePage = Self.value(after: "--stage").map { URL(fileURLWithPath: ($0 as NSString).expandingTildeInPath) }
        clip = Self.value(after: "--clip").map { URL(fileURLWithPath: ($0 as NSString).expandingTildeInPath) }
        noVideo = arguments.contains("--no-video")
        noCursor = arguments.contains("--no-cursor")
        syncMarks = arguments.contains("--sync-marks")
        stageOnly = arguments.contains("--stage-only")
        mov = arguments.contains("--mov")
        anyDisplay = arguments.contains("--any-display")
        viaModel = arguments.contains("--via-model")
        noTranscribe = arguments.contains("--no-transcribe")
        if viaModel, noVideo || mov { Self.die(Code.failed, "error: --via-model records video.mp4: no --no-video or --mov") }
        func number(_ flag: String) -> Double? {
            guard let text = Self.value(after: flag) else { return nil }
            guard let value = Double(text), value.isFinite, value >= 0 else { Self.die(Code.failed, "error: bad \(flag) \(text)") }
            return value
        }
        let fps = Int(number("--fps") ?? 15)
        let quality = number("--quality") ?? 0.75
        let pauseAt = number("--pause-at")
        let resumeAt = number("--resume-at")
        let resizeAt = number("--resize-at")
        self.fps = fps
        self.quality = quality
        self.pauseAt = pauseAt
        self.resumeAt = resumeAt
        self.resizeAt = resizeAt
        if command == "preflight" {
            target = .window
            targetName = "window"
            seconds = 3
        } else {
            guard let name = Self.value(after: "--target") else { Self.die(Code.failed, usage) }
            targetName = name
            if name == "window" {
                target = .window
            } else if name == "display" {
                target = .display
            } else if name.hasPrefix("area:") {
                let parts = name.dropFirst(5).split(separator: ",").compactMap { Double($0) }
                guard parts.count == 4, parts[2] > 0, parts[3] > 0 else { Self.die(Code.failed, "error: bad \(name)") }
                target = .area(CGRect(x: parts[0], y: parts[1], width: parts[2], height: parts[3]))
            } else {
                Self.die(Code.failed, usage)
            }
            guard let runSeconds = number("--seconds"), runSeconds > 0 else {
                Self.die(Code.failed, "error: --seconds N is required")
            }
            seconds = runSeconds
            guard (1...120).contains(fps), (0...1).contains(quality) else {
                Self.die(Code.failed, "error: --fps must be 1 to 120 and --quality 0 to 1")
            }
            if let resumeAt, pauseAt.map({ $0 >= resumeAt }) ?? true {
                Self.die(Code.failed, "error: --resume-at needs an earlier --pause-at")
            }
            if let end = resumeAt ?? pauseAt, end >= runSeconds {
                Self.die(Code.failed, "error: --pause-at and --resume-at must be before --seconds")
            }
            if let resizeAt, name != "window" || resizeAt >= runSeconds {
                Self.die(Code.failed, "error: --resize-at needs --target window and must be before --seconds")
            }
        }
        super.init()
        fields = [
            "harness": "measure-screen", "pid": Int(getpid()), "scenario": command, "target": targetName,
            "fps": fps, "quality": quality, "seconds": seconds, "pauseAt": pauseAt ?? NSNull(),
            "resumeAt": resumeAt ?? NSNull(), "noVideo": noVideo, "noCursor": noCursor, "syncMarks": syncMarks,
            "stageOnly": stageOnly, "videoFileName": noVideo ? NSNull() : videoFileName,
            "settingsDirectory": settingsDirectory.path, "clipPlaying": NSNull(), "viaModel": viaModel,
            "noTranscribe": noTranscribe, "resizeAt": resizeAt ?? NSNull(), "resizedAt": NSNull(),
        ]
    }

    // MARK: - Scenario run

    func run() async {
        // A windowless process gets App Napped; the stage page and the stream must keep their pace.
        activity = ProcessInfo.processInfo.beginActivity(options: .userInitiated, reason: Self.argument)
        if command == "preflight" { await preflight() }
        if let other = Self.otherInstances().first {
            await finish(Code.otherInstance, "error: another EchoDiar is running (pid \(other)); quit it first")
        }
        guard let screen = stageScreen() else {
            await finish(Code.noDisplay, anyDisplay ? "error: no display" : "error: no built-in display at 2x")
        }
        let displayID = Self.displayID(of: screen)
        fields["display"] = [
            "id": Int(displayID), "builtIn": CGDisplayIsBuiltin(displayID) != 0, "scale": screen.backingScaleFactor,
            "width": screen.frame.width, "height": screen.frame.height,
            "maximumFramesPerSecond": screen.maximumFramesPerSecond,
        ] as [String: Any]
        if case .area(let rect) = target, !CGRect(origin: .zero, size: screen.frame.size).contains(rect) {
            await finish(Code.failed, "error: \(targetName) is not inside the \(Int(screen.frame.width))x"
                + "\(Int(screen.frame.height)) display")
        }
        do {
            try copyStage()
        } catch {
            await finish(Code.failed, "error: cannot copy the stage: \(error.localizedDescription)")
        }
        let window = makeStage(on: screen, size: target == .window ? Self.windowSize : nil)
        guard await load(mode: command, marks: syncMarks) else { await finish(Code.failed, "error: the stage page did not load") }
        // Let the occlusion state and the page settle before checking.
        await Self.sleep(0.5)
        if target == .window {
            guard window.screen.map({ Self.displayID(of: $0) }) == displayID else {
                await finish(Code.noDisplay, "error: the stage window is not on the built-in display")
            }
            Self.say("WINDOWID \(window.windowNumber)")
        } else {
            guard window.frame == screen.frame, window.isVisible, window.occlusionState.contains(.visible) else {
                await finish(Code.notCovered, "error: the stage does not cover the display (frame \(window.frame), "
                    + "screen \(screen.frame))")
            }
        }
        fields["windowID"] = window.windowNumber
        if command == "meeting" || command == "video" {
            let playing = await clipPlaying()
            fields["clipPlaying"] = playing
            guard playing else { await finish(Code.clipNotPlaying, "error: the clip is not playing") }
        }
        if stageOnly {
            await Self.sleep(seconds)
            await finish(Code.ok, nil)
        }
        if viaModel {
            await recordViaModel(displayID: displayID, window: window)
        } else {
            await record(displayID: displayID, window: window)
        }
    }

    /// Starts the engine (muted) and the video, runs the timeline, stops both and checks the frames.
    private func record(displayID: CGDirectDisplayID, window: NSWindow) async {
        let engine = engine
        let root = out
        do {
            sessionFolder = try await Task.detached(priority: .userInitiated) {
                try engine.start(target: .global, language: "auto", root: root, probe: true)
            }.value
        } catch {
            await finish(Code.failed, "error: the engine did not start: \(error)")
        }
        recording = true
        // The engine cannot start muted: mute at once, so no microphone audio is written after this.
        await Task.detached(priority: .userInitiated) { engine.toggleMicMuted() }.value
        startSampler()
        if !noVideo, let folder = sessionFolder {
            let source: ScreenSource = switch target {
            case .window: .window(CGWindowID(window.windowNumber))
            case .display: .display(displayID)
            case .area(let rect): .area(display: displayID, rect: rect)
            }
            let recorder = ScreenRecorder()
            self.recorder = recorder
            Task { [weak self] in
                for await event in recorder.events { self?.endEvent = event }
            }
            var failure: (any Error)?
            // A window just ordered front may take a moment to appear in the shareable content.
            for attempt in 1...3 {
                do {
                    try await recorder.start(
                        source: source, fps: fps, quality: quality, showsCursor: !noCursor, showsClicks: false,
                        url: folder.appendingPathComponent(videoFileName), excludingWindowIDs: [],
                        minimumFreeBytes: RecordingEngine.minimumFreeBytes,
                        clock: { engine.fileTime(atHostTime: $0) })
                    fields["videoStartAttempts"] = attempt
                    failure = nil
                    break
                } catch ScreenRecorderError.sourceNotFound where attempt < 3 {
                    failure = ScreenRecorderError.sourceNotFound
                    await Self.sleep(0.5)
                } catch {
                    failure = error
                    break
                }
            }
            if let failure { await finish(Code.failed, "error: the screen recorder did not start: \(failure)") }
        }
        let start = ContinuousClock.now
        videoStartFileTime = engine.fileTime(atHostTime: mach_absolute_time())
        scheduleResize(window, from: start)
        if syncMarks {
            // From recording start, not page load: the page runs no mark before this call.
            fields["marksAudioState"] = await js("return await startMarks();") as? String ?? NSNull()
        }
        if let pauseAt {
            try? await Task.sleep(until: start + .seconds(pauseAt))
            await recorder?.pause()
            await Task.detached(priority: .userInitiated) { engine.pause() }.value
        }
        if let resumeAt {
            try? await Task.sleep(until: start + .seconds(resumeAt))
            do {
                try await Task.detached(priority: .userInitiated) { try engine.resume() }.value
                try await recorder?.resume()
            } catch {
                await finish(Code.failed, "error: resume failed: \(error)")
            }
        }
        try? await Task.sleep(until: start + .seconds(seconds))
        let videoError = await stopAndCollect()

        var code = Code.ok
        var message: String?
        if !noVideo {
            let frames = recorder?.stats.frames ?? 0
            let expected = fields["expectedFrames"] as? Double ?? 0
            let gap = (fields["audioDuration"] as? Double ?? 0) - (recorder?.stats.lastFileTime ?? 0)
            if frames == 0 {
                (code, message) = (Code.noFrames, "error: no video frame was written")
            } else if Self.motionScenarios.contains(command), Double(frames) < 0.9 * expected || gap > 1 {
                (code, message) = (Code.fewFrames, String(
                    format: "error: %d frames of %.0f expected, last frame %.2f s before the end", frames, expected, gap))
            } else if let videoError {
                (code, message) = (Code.failed, "error: the video did not finish: \(videoError)")
            } else if let endEvent {
                (code, message) = (Code.failed, "error: the video ended early: \(endEvent)")
            }
        }
        await finish(code, message)
    }

    /// `--via-model`: the same run through `AppModel`. The source is armed, Record starts the engine (muted as soon
    /// as it runs) and then the video; the timeline counts from the video running; pause and resume go through
    /// `togglePause`.
    private func recordViaModel(displayID: CGDirectDisplayID, window: NSWindow) async {
        let engine = engine
        let source: ScreenSource = switch target {
        case .window: .window(CGWindowID(window.windowNumber))
        case .display: .display(displayID)
        case .area(let rect): .area(display: displayID, rect: rect)
        }
        model.updateSettings {
            $0.screenFPS = fps
            $0.screenQuality = quality
            $0.screenShowsCursor = !noCursor
            $0.screenShowsClicks = false
            $0.screenTranscribes = !noTranscribe
        }
        model.harnessRecordingRoot = out
        model.harnessRecordingEnded = { [weak self] folder, video in self?.modelEnded = (folder, video) }
        model.screenRecording.armedSource = source
        model.toggleRecording(bringForward: false)
        guard await Self.wait(seconds: 15, until: { [model] in model.isRecording || model.recordingError != nil }),
              model.isRecording else {
            await finish(Code.failed, "error: the engine did not start: \(model.recordingError ?? "timeout")")
        }
        recording = true
        // The engine cannot start muted: mute at once, so no microphone audio is written after this.
        await Task.detached(priority: .userInitiated) { engine.toggleMicMuted() }.value
        startSampler()
        let screen = model.screenRecording
        guard await Self.wait(seconds: 15, until: { screen.state != .starting && screen.state != .off }) else {
            await finish(Code.failed, "error: the screen recorder did not start in 15 s")
        }
        if case .ended(let reason) = screen.state {
            await finish(Code.failed, "error: the screen recorder did not start: \(reason) (\(screen.notice ?? ""))")
        }
        fields["videoStartAttempts"] = 1
        let start = ContinuousClock.now
        videoStartFileTime = engine.fileTime(atHostTime: mach_absolute_time())
        scheduleResize(window, from: start)
        if syncMarks {
            fields["marksAudioState"] = await js("return await startMarks();") as? String ?? NSNull()
        }
        if let pauseAt {
            try? await Task.sleep(until: start + .seconds(pauseAt))
            model.togglePause()
        }
        if let resumeAt {
            try? await Task.sleep(until: start + .seconds(resumeAt))
            model.togglePause()
        }
        try? await Task.sleep(until: start + .seconds(seconds))
        let videoError = await stopAndCollect()

        var code = Code.ok
        var message: String?
        let frames = fields["frames"] as? Int ?? 0
        let expected = fields["expectedFrames"] as? Double ?? 0
        let gap = (fields["audioDuration"] as? Double ?? 0) - (fields["lastFrameFileTime"] as? Double ?? 0)
        if frames == 0 {
            (code, message) = (Code.noFrames, "error: no video frame was written")
        } else if Self.motionScenarios.contains(command), Double(frames) < 0.9 * expected || gap > 1 {
            (code, message) = (Code.fewFrames, String(
                format: "error: %d frames of %.0f expected, last frame %.2f s before the end", frames, expected, gap))
        } else if let videoError {
            (code, message) = (Code.failed, "error: the video did not finish: \(videoError)")
        } else if let modelEndReason {
            (code, message) = (Code.failed, "error: the video ended early: \(modelEndReason)")
        } else if let error = model.recordingError {
            (code, message) = (Code.failed, "error: \(error)")
        }
        await finish(code, message)
    }

    /// `--resize-at`: shrinks the stage window to `resizedSize` (top-left kept, same display) at `start + resizeAt`
    /// while the recording runs, and records when it happened and the frame it got.
    private func scheduleResize(_ window: NSWindow, from start: ContinuousClock.Instant) {
        guard let resizeAt else { return }
        Task { [weak self] in
            try? await Task.sleep(until: start + .seconds(resizeAt))
            guard let self, self.recording else { return }
            let old = window.frame
            let display = window.screen.map { Self.displayID(of: $0) }
            let frame = NSRect(
                x: old.minX, y: old.maxY - Self.resizedSize.height,
                width: Self.resizedSize.width, height: Self.resizedSize.height)
            window.setFrame(frame, display: true)
            let elapsed = ContinuousClock.now - start
            self.fields["resizedAt"] = Double(elapsed.components.seconds) + Double(elapsed.components.attoseconds) / 1e18
            self.fields["resizedFrame"] = [
                "width": window.frame.width, "height": window.frame.height,
                "sameDisplay": display != nil && window.screen.map { Self.displayID(of: $0) } == display,
            ] as [String: Any]
        }
    }

    /// `--via-model`: Stop, then the snapshot consumer's hand-over (the video finished at the audio file's length).
    private func stopModelRecording() async {
        if modelEnded == nil { model.stopRecording() }
        guard await Self.wait(seconds: 30, until: { [weak self] in self?.modelEnded != nil }) else {
            Self.warn("error: the recording did not end in 30 s")
            return
        }
        sessionFolder = modelEnded?.folder
        // The end snapshot's file time, which the video was finished at.
        fields["videoStopFileTime"] = model.snapshot.elapsed
    }

    /// Stops the video stream, then the engine, then ends the video at the audio file's length; fills the numbers.
    /// Returns the video's finish error, if any.
    private func stopAndCollect() async -> String? {
        guard recording else { return nil }
        recording = false
        let engine = engine
        if viaModel {
            await stopModelRecording()
        } else {
            await recorder?.pause()
            await Task.detached(priority: .userInitiated) { _ = engine.stop() }.value
        }
        guard let folder = sessionFolder else { return nil }
        sampler?.cancel()
        sampleFootprint()
        fields["sessionFolder"] = folder.path

        let audioName = (try? RecordingSession.read(from: folder))?.audioFileName ?? "audio.caf"
        let audioURL = folder.appendingPathComponent(audioName)
        let audioDuration = (try? AVAudioFile(forReading: audioURL)).map {
            Double($0.length) / $0.fileFormat.sampleRate
        } ?? 0
        fields["audioDuration"] = audioDuration
        fields["audioBytes"] = Self.size(of: audioURL)
        // Wall seconds recorded, from the video start: `--seconds` minus the pause (to the end without `--resume-at`).
        let unpaused = pauseAt.map { pause in resumeAt.map { seconds - ($0 - pause) } ?? pause } ?? seconds
        fields["videoStartFileTime"] = videoStartFileTime ?? NSNull()
        fields["unpausedSeconds"] = unpaused
        // The same span on the audio file's clock; shorter when the audio file lost time.
        fields["fileTimeSpan"] = max(0, audioDuration - (videoStartFileTime ?? 0))
        let stats: ScreenRecorder.Stats
        var videoError: String?
        if viaModel {
            guard let video = modelEnded?.video else { return nil }
            stats = video.stats
            videoError = video.finishError
            modelEndReason = video.endReason
        } else {
            guard let recorder else { return nil }
            do {
                try await recorder.stop(at: audioDuration)
            } catch {
                videoError = String(describing: error)
            }
            stats = recorder.stats
            fields["videoStopFileTime"] = audioDuration
        }
        let videoURL = folder.appendingPathComponent(videoFileName)
        let asset = AVURLAsset(url: videoURL)
        let videoDuration = try? await asset.load(.duration).seconds
        let track = try? await asset.loadTracks(withMediaType: .video).first
        let trackDuration = try? await track?.load(.timeRange).duration.seconds
        let bytes = Self.size(of: videoURL)
        let expected = Double(fps) * unpaused
        fields["bytes"] = bytes
        fields["mbPerMin"] = videoDuration.map { $0 > 0 ? Double(bytes) / 1_000_000 / ($0 / 60) : 0 } ?? NSNull()
        fields["frames"] = stats.frames
        fields["dropped"] = stats.dropped
        fields["expectedFrames"] = expected
        fields["onHostClock"] = stats.onHostClock ?? NSNull()
        fields["maxHostTimeOffset"] = stats.maxHostTimeOffset ?? NSNull()
        fields["firstFrameFileTime"] = stats.firstFileTime ?? NSNull()
        fields["lastFrameFileTime"] = stats.lastFileTime ?? NSNull()
        fields["videoDuration"] = videoDuration ?? NSNull()
        fields["videoTrackDuration"] = trackDuration ?? NSNull()
        fields["videoEndEvent"] = (viaModel ? modelEndReason.map { String(describing: $0) }
            : endEvent.map { String(describing: $0) }) ?? NSNull()
        fields["videoError"] = videoError ?? NSNull()
        return videoError
    }

    /// Every exit after the options are read: stops a running recording, writes `result.json`, removes the stage copy.
    private func finish(_ code: Int32, _ message: String?) async -> Never {
        _ = await stopAndCollect()
        // Instrument, after the recording (its 0.5 s rAF probe must not change what was captured): the scroll loop's
        // rAF and scroll counts since the page loaded, the rAF rate now, visibility and focus. Null without a page.
        fields["stageStats"] = await js("return await stageStats();") ?? NSNull()
        sampleFootprint()
        fields["exitCode"] = Int(code)
        fields["error"] = message ?? NSNull()
        fields["transcriberStarted"] = transcriberStarted()
        fields["footprintMB"] = Self.megabytes(Self.physFootprint())
        fields["maxFootprintMB"] = Self.megabytes(maxFootprint)
        let result = out.appendingPathComponent("result.json")
        do {
            let data = try JSONSerialization.data(withJSONObject: fields, options: [.prettyPrinted, .sortedKeys])
            try data.write(to: result, options: .atomic)
        } catch {
            Self.warn("error: cannot write result.json: \(error)")
        }
        if let stageFolder { try? FileManager.default.removeItem(at: stageFolder) }
        if let message { Self.warn(message) }
        Self.say(result.path)
        exit(code)
    }

    // MARK: - Preflight

    /// Exits 0 only when every check holds, else with the first failed check's code and a `PREFLIGHT FAIL` line.
    /// The probe session, the capture file and the stage copy are deleted on every exit.
    private func preflight() async -> Never {
        if let other = Self.otherInstances().first {
            preflightExit(Code.otherInstance, "other-echodiar-running", "pid \(other)")
        }
        guard CGPreflightScreenCaptureAccess() else {
            preflightExit(Code.screenAccess, "screen-recording-access", "CGPreflightScreenCaptureAccess() is false")
        }
        guard let screen = stageScreen() else {
            preflightExit(Code.noDisplay, "no-built-in-retina-display", anyDisplay ? "no display" : "no built-in 2x display")
        }
        guard let stagePage, let clip, FileManager.default.fileExists(atPath: stagePage.path),
              FileManager.default.fileExists(atPath: clip.path) else {
            preflightExit(Code.stageMissing, "stage-or-clip-missing", "--stage and --clip must name existing files")
        }
        do {
            try copyStage()
        } catch {
            preflightExit(Code.stageMissing, "stage-or-clip-missing", "cannot copy: \(error.localizedDescription)")
        }
        let window = makeStage(on: screen, size: NSSize(width: 640, height: 400))
        guard await load(mode: "static", marks: false) else {
            preflightExit(Code.stageMissing, "stage-or-clip-missing", "the stage page did not load")
        }
        await Self.sleep(0.5)
        guard window.screen.map({ Self.displayID(of: $0) }) == Self.displayID(of: screen) else {
            preflightExit(Code.noDisplay, "no-built-in-retina-display", "the stage window is not on the built-in display")
        }

        // 2 s of real capture of the stage window (any consent prompt shows here).
        let captureFolder = out.appendingPathComponent("preflight-capture", isDirectory: true)
        scratch.append(captureFolder)
        let frames: Int
        do {
            try FileManager.default.createDirectory(at: captureFolder, withIntermediateDirectories: true)
            let recorder = ScreenRecorder()
            let base = mach_absolute_time()
            let tick = Self.secondsPerTick
            try await recorder.start(
                source: .window(CGWindowID(window.windowNumber)), fps: 15, quality: 0.75, showsCursor: false,
                showsClicks: false, url: captureFolder.appendingPathComponent(ScreenSource.videoFileName),
                minimumFreeBytes: RecordingEngine.minimumFreeBytes,
                clock: { $0 >= base ? Double($0 - base) * tick : nil })
            await Self.sleep(2)
            try? await recorder.stop(at: Double(mach_absolute_time() - base) * tick)
            frames = recorder.stats.frames
        } catch {
            preflightExit(Code.captureNoFrame, "capture-no-frame", "the stream did not start: \(error)")
        }
        guard frames > 0 else { preflightExit(Code.captureNoFrame, "capture-no-frame", "0 complete frames in 2 s") }

        // 3 s engine probe: the page beeps once; the system channel must hear it.
        let engine = engine
        let root = out
        let folder: URL
        do {
            folder = try await Task.detached(priority: .userInitiated) {
                try engine.start(target: .global, language: "auto", root: root, probe: true)
            }.value
        } catch {
            preflightExit(Code.engineStart, "engine-start", "\(error)")
        }
        scratch.append(folder)
        await Task.detached(priority: .userInitiated) { engine.toggleMicMuted() }.value
        await Self.sleep(0.5)
        let audioState = await js("return await beep();") as? String
        let fileTime = engine.fileTime(atHostTime: mach_absolute_time())
        await Self.sleep(2.5)
        await Task.detached(priority: .userInitiated) { _ = engine.stop() }.value
        guard fileTime != nil else { preflightExit(Code.noFileTime, "file-time", "engine.fileTime(atHostTime:) is nil") }
        guard audioState == "running" else {
            preflightExit(Code.beepNotPlayed, "beep-not-played", "AudioContext state \(audioState ?? "nil")")
        }
        let audioName = (try? RecordingSession.read(from: folder))?.audioFileName ?? "audio.caf"
        let peak = Self.peakDB(folder.appendingPathComponent(audioName), channel: 1)
        guard let peak, peak > Self.silenceThresholdDB else {
            preflightExit(Code.systemAudioSilent, "system-audio-silent", String(
                format: "system channel max %@ dB (needs > %.0f): check the system-audio grant",
                peak.map { String(format: "%.1f", $0) } ?? "unreadable", Self.silenceThresholdDB))
        }
        preflightExit(Code.ok, nil, String(
            format: "frames %d, fileTime %.2f s, system max %.1f dB", frames, fileTime ?? 0, peak))
    }

    /// Deletes the preflight's recordings and stage copy, prints the verdict line, exits.
    private func preflightExit(_ code: Int32, _ check: String?, _ detail: String) -> Never {
        for url in scratch + [stageFolder].compactMap({ $0 }) { try? FileManager.default.removeItem(at: url) }
        if let check {
            Self.say("PREFLIGHT FAIL \(check): \(detail)")
        } else {
            Self.say("PREFLIGHT OK (\(detail))")
        }
        exit(code)
    }

    // MARK: - Stage

    /// The built-in display at 2x (by `CGDisplayIsBuiltin`, never `NSScreen.main`); with `--any-display` the built-in
    /// display or else the main one, at any scale.
    private func stageScreen() -> NSScreen? {
        let builtIn = NSScreen.screens.first { CGDisplayIsBuiltin(Self.displayID(of: $0)) != 0 }
        if anyDisplay { return builtIn ?? NSScreen.main }
        guard let builtIn, builtIn.backingScaleFactor == 2 else { return nil }
        return builtIn
    }

    /// Copies the page and the clip (as `clip.mp4`, the name the page loads) into `<out>/stage/`.
    private func copyStage() throws {
        guard let stagePage, let clip else { throw CocoaError(.fileNoSuchFile) }
        let folder = out.appendingPathComponent("stage", isDirectory: true)
        let fm = FileManager.default
        try? fm.removeItem(at: folder)
        try fm.createDirectory(at: folder, withIntermediateDirectories: true)
        stageFolder = folder
        try fm.copyItem(at: stagePage, to: folder.appendingPathComponent("screen-stage.html"))
        try fm.copyItem(at: clip, to: folder.appendingPathComponent("clip.mp4"))
    }

    /// A titled window of `size` at the top of `screen`'s visible frame, or (`size` nil) a borderless window at
    /// `.screenSaver` level covering the whole screen. The frame is set again after ordering front: a window created
    /// without this landed on the main display in the spike.
    private func makeStage(on screen: NSScreen, size: NSSize?) -> NSWindow {
        let frame: NSRect
        let window: NSWindow
        if let size {
            let visible = screen.visibleFrame
            let width = min(size.width, visible.width)
            let height = min(size.height, visible.height)
            frame = NSRect(x: (visible.midX - width / 2).rounded(.down), y: visible.maxY - height, width: width, height: height)
            window = NSWindow(contentRect: frame, styleMask: [.titled], backing: .buffered, defer: false)
            window.title = "EchoDiar Screen Stage"
            window.level = .floating
        } else {
            frame = screen.frame
            window = NSWindow(contentRect: frame, styleMask: [.borderless], backing: .buffered, defer: false)
            window.level = .screenSaver
            window.backgroundColor = .white
        }
        window.isReleasedWhenClosed = false
        let configuration = WKWebViewConfiguration()
        configuration.mediaTypesRequiringUserActionForPlayback = []
        let web = WKWebView(frame: .zero, configuration: configuration)
        web.navigationDelegate = self
        window.contentView = web
        window.orderFrontRegardless()
        window.setFrame(frame, display: true)
        self.window = window
        self.web = web
        return window
    }

    /// Loads the stage copy with `?mode=<mode>` (and `&marks=1`); false when it failed or took over 15 s.
    private func load(mode: String, marks: Bool) async -> Bool {
        guard let web, let stageFolder else { return false }
        var components = URLComponents(
            url: stageFolder.appendingPathComponent("screen-stage.html"), resolvingAgainstBaseURL: false)
        components?.queryItems = [URLQueryItem(name: "mode", value: mode)] + (marks ? [URLQueryItem(name: "marks", value: "1")] : [])
        guard let url = components?.url else { return false }
        web.loadFileURL(url, allowingReadAccessTo: stageFolder)
        Task { [weak self] in
            await Self.sleep(15)
            self?.loaded(false)
        }
        return await withCheckedContinuation { loadContinuation = $0 }
    }

    private func loaded(_ ok: Bool) {
        loadContinuation?.resume(returning: ok)
        loadContinuation = nil
    }

    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) { loaded(true) }

    func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: any Error) { loaded(false) }

    func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: any Error) {
        loaded(false)
    }

    /// Runs `body` as an async function in the page; nil on error.
    private func js(_ body: String) async -> Any? {
        guard let web else { return nil }
        return try? await web.callAsyncJavaScript(body, arguments: [:], in: nil, contentWorld: .page)
    }

    /// The clip has a picture and its time moved over 3 s.
    private func clipPlaying() async -> Bool {
        func state() async -> (width: Double, time: Double)? {
            guard let state = await js("return clipState();") as? [String: Any],
                  let width = (state["width"] as? NSNumber)?.doubleValue,
                  let time = (state["time"] as? NSNumber)?.doubleValue else { return nil }
            return (width, time)
        }
        let before = await state()
        await Self.sleep(3)
        guard let before, let after = await state() else { return false }
        return after.width > 0 && after.time != before.time
    }

    // MARK: - Helpers

    private func startSampler() {
        sampler = Task { [weak self] in
            while !Task.isCancelled {
                self?.sampleFootprint()
                await Self.sleep(1)
            }
        }
    }

    private func sampleFootprint() {
        maxFootprint = max(maxFootprint, Self.physFootprint())
    }

    private static func sleep(_ seconds: Double) async {
        try? await Task.sleep(for: .seconds(seconds))
    }

    /// Checks `condition` every 20 ms; false when it still does not hold after `seconds`.
    private static func wait(seconds: Double, until condition: () -> Bool) async -> Bool {
        let deadline = ContinuousClock.now + .seconds(seconds)
        while !condition() {
            guard ContinuousClock.now < deadline else { return false }
            try? await Task.sleep(for: .milliseconds(20))
        }
        return true
    }

    nonisolated private static func value(after flag: String) -> String? {
        let arguments = ProcessInfo.processInfo.arguments
        guard let index = arguments.firstIndex(of: flag), index + 1 < arguments.count else { return nil }
        return arguments[index + 1]
    }

    nonisolated private static func displayID(of screen: NSScreen) -> CGDirectDisplayID {
        (screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value ?? 0
    }

    /// Other running EchoDiar processes (any build).
    private static func otherInstances() -> [pid_t] {
        let ids = Set([SessionRecovery.appBundleID, Bundle.main.bundleIdentifier].compactMap { $0 })
        return ids.flatMap { NSRunningApplication.runningApplications(withBundleIdentifier: $0) }
            .filter { $0.processIdentifier != getpid() && !$0.isTerminated }
            .map(\.processIdentifier)
    }

    private static var secondsPerTick: Double {
        var info = mach_timebase_info_data_t()
        mach_timebase_info(&info)
        return Double(info.numer) / Double(info.denom) / 1_000_000_000
    }

    /// Peak of `channel` in dBFS (as ffmpeg's `volumedetect` `max_volume`); nil when unreadable, -inf when silent.
    private static func peakDB(_ url: URL, channel: Int) -> Double? {
        guard let file = try? AVAudioFile(forReading: url), channel < Int(file.processingFormat.channelCount),
              let buffer = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: 16_384) else { return nil }
        var peak: Float = 0
        while file.framePosition < file.length {
            guard (try? file.read(into: buffer)) != nil, buffer.frameLength > 0,
                  let samples = buffer.floatChannelData?[channel] else { break }
            for i in 0..<Int(buffer.frameLength) { peak = max(peak, abs(samples[i])) }
        }
        return 20 * log10(Double(peak))
    }

    private static func size(of url: URL) -> Int64 {
        ((try? FileManager.default.attributesOfItem(atPath: url.path))?[.size] as? NSNumber)?.int64Value ?? 0
    }

    private static func megabytes(_ bytes: UInt64) -> Double { (Double(bytes) / 1_048_576 * 10).rounded() / 10 }

    /// `phys_footprint` (Activity Monitor's Memory), 0 when `task_info` fails.
    private static func physFootprint() -> UInt64 {
        var info = task_vm_info_data_t()
        var count = mach_msg_type_number_t(MemoryLayout<task_vm_info_data_t>.size / MemoryLayout<integer_t>.size)
        let status = withUnsafeMutablePointer(to: &info) {
            $0.withMemoryRebound(to: integer_t.self, capacity: Int(count)) {
                task_info(mach_task_self_, task_flavor_t(TASK_VM_INFO), $0, &count)
            }
        }
        return status == KERN_SUCCESS ? info.phys_footprint : 0
    }

    nonisolated private static func say(_ line: String) {
        FileHandle.standardOutput.write(Data((line + "\n").utf8))
    }

    nonisolated private static func warn(_ line: String) {
        FileHandle.standardError.write(Data((line + "\n").utf8))
    }

    /// Prints `message` on stderr and exits with `code`.
    nonisolated private static func die(_ code: Int32, _ message: String) -> Never {
        warn(message)
        exit(code)
    }
}
#endif
