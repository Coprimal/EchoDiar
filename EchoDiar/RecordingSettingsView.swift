import AppKit
import AVFoundation
import CoreAudio
import EchoAI
import EchoCore
import EchoTranscribe
import EventKit
import SwiftUI
import UserNotifications

/// The Recording tab, one inner tab per section: the microphone, meeting detection and per-app behavior with the
/// floating pill's anchor, auto-stop, what happens after a recording stops, and screen recordings.
struct RecordingSettingsView: View {
    let model: AppModel
    @State private var microphones: [InputDevice] = []
    @State private var accessibilityTrusted = WindowAnchor.isTrusted
    @State private var cameraStatus = CameraBubbleController.authorizationStatus
    @State private var keyAccess = KeyStrokeHUDController.access
    @State private var screenAccess = CGPreflightScreenCaptureAccess()
    /// 60 fps was picked: the confirmation is up, the stored frame rate is unchanged until "Use 60 fps".
    @State private var confirming60FPS = false

    /// One row per app the user sees (FaceTime's daemon is listed as FaceTime).
    private static let meetingApps: [MeetingApp] = {
        var seen = Set<String>()
        return MeetingApps.known.filter { seen.insert($0.appBundleID).inserted }
    }()

    /// Ids in `SettingsTab.recording.subTabs`; picked in the toolbar.
    private enum Tab: String {
        case microphone, meetings, autoStop, afterRecording, screen
    }

    private var tab: Tab { model.settingsSubTab(.recording).flatMap(Tab.init) ?? .microphone }

    var body: some View {
        Form {
            switch tab {
            case .microphone: microphoneSection
            case .meetings: meetingsSection
            case .autoStop: autoStopSection
            case .afterRecording: afterRecordingSection
            case .screen: screenSection
            }
        }
        .formStyle(.grouped)
        .task {
            for await _ in InputDevice.changes() {
                microphones = await Task.detached(priority: .utility) { InputDevice.all() }.value
            }
        }
        .task { model.refreshInstalledModels() }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            // Back from System Settings.
            accessibilityTrusted = WindowAnchor.isTrusted
            model.refreshNotificationStatus()
            refreshScreenPermissions()
            // A permission granted during a running video brings its overlay up.
            model.screenRecording.setOverlays(model.settings)
        }
    }

    @ViewBuilder private var microphoneSection: some View {
        Section {
            Picker("Microphone", selection: model.setting(\.micDeviceUID)) {
                Text("System default").tag(String?.none)
                ForEach(microphones) { Text($0.name).tag(String?.some($0.id)) }
                if let uid = model.settings.micDeviceUID, !microphones.contains(where: { $0.id == uid }) {
                    Text("Unavailable device").tag(String?.some(uid))
                }
            }
            Picker(selection: model.setting(\.routeChangeBehavior)) {
                Text("Ask to stop").tag(RouteChangeBehavior.ask)
                Text("Keep recording").tag(RouteChangeBehavior.continue)
                Text("Stop recording").tag(RouteChangeBehavior.stop)
            } label: {
                Text("When the microphone disconnects")
                switch model.settings.routeChangeBehavior {
                case .ask: Text("Recording continues with the default input until then.")
                case .continue: Text("Recording switches to the default input.")
                case .stop: Text("Recording stops at once.")
                }
            }
        }
        Section {
            Picker("Menu bar while recording", selection: model.setting(\.menuBarRecordingInfo)) {
                Text("Icon only").tag(MenuBarRecordingInfo.iconOnly)
                Text("Elapsed time").tag(MenuBarRecordingInfo.elapsedTime)
                Text("File size").tag(MenuBarRecordingInfo.fileSize)
            }
        }
    }

    @ViewBuilder private var meetingsSection: some View {
        Section {
            Toggle(isOn: model.setting(\.attachPillToWindow)) {
                Text("Attach the indicator to the meeting window")
                Text("Keeps it at the window's top-right corner. Needs Accessibility access.")
            }
            if model.settings.attachPillToWindow && !accessibilityTrusted {
                LabeledContent("EchoDiar has no Accessibility access.") {
                    Button("Grant Accessibility…", action: WindowAnchor.openAccessibilitySettings)
                }
                .foregroundStyle(.secondary)
            }
        }
        Section {
            Toggle(isOn: model.setting(\.meetingDetection)) {
                Text("Detect meetings")
                Text("Offers to record when a meeting app starts using the microphone.")
            }
            ForEach(Self.meetingApps, id: \.appBundleID) { app in
                Picker(app.name, selection: behavior(app)) {
                    Text("Ask").tag(MeetingBehavior.ask)
                    if !app.isBrowser { Text("Always record").tag(MeetingBehavior.always) }
                    Text("Ignore").tag(MeetingBehavior.ignore)
                }
            }
            .disabled(!model.settings.meetingDetection)
        }
    }

    @ViewBuilder private var autoStopSection: some View {
        Section {
            NumberRow(
                title: "Stop when the app is quiet for", unit: "s",
                value: model.setting(\.endMonitor.sourceInactiveSeconds),
                range: MeetingEndMonitor.Configuration.sourceInactiveRange)
            NumberRow(
                title: "Stop when the call seems over for", unit: "s",
                value: model.setting(\.endMonitor.callEndedSeconds),
                range: MeetingEndMonitor.Configuration.callEndedRange)
            NumberRow(
                title: "Stop after silence of", unit: String(localized: "min"),
                value: model.setting(\.endMonitor.silenceMinutes),
                range: MeetingEndMonitor.Configuration.silenceRange)
            NumberRow(
                title: "Stop after a recording length of", unit: String(localized: "min"),
                value: model.setting(\.endMonitor.maxDurationMinutes),
                range: MeetingEndMonitor.Configuration.maxDurationRange)
        } header: {
            SectionHeader(
                description: "The indicator asks before stopping, with a 15 s countdown. Reaching the length limit stops at once.")
        }
        Section {
            Toggle(isOn: model.setting(\.endPromptNotification)) {
                Text("Notify when a meeting seems to end")
                Text("While EchoDiar is in the background, a notification asks too, with Stop now and Keep recording.")
            }
            if model.notificationStatus == .denied {
                LabeledContent("Notifications are off for EchoDiar in System Settings.") {
                    Button("Open Notification Settings") { PrivacySettings.openNotifications() }
                }
                .foregroundStyle(.secondary)
            }
        }
    }

    private var afterRecordingSection: some View {
        Section {
            Toggle("Transcribe automatically", isOn: model.setting(\.autoTranscribe))
            SpeakerSeparationToggle(model: model)
            if model.settings.diarization { SpeakerCountPicker(model: model) }
            Toggle(isOn: model.setting(\.deferTranscriptionWhenConstrained)) {
                Text("Wait when the Mac is hot or in Low Power Mode")
                Text("Transcription starts once the Mac is ready.")
            }
        }
    }

    @ViewBuilder private var screenSection: some View {
        Section {
            Picker("Frame rate", selection: frameRate) {
                ForEach(AppSettings.screenFPSOptions, id: \.self) { Text("\($0) fps").tag($0) }
            }
            .alert("Record at 60 frames per second?", isPresented: $confirming60FPS) {
                Button("Use 60 fps") { model.updateSettings { $0.screenFPS = 60 } }
                Button("Cancel", role: .cancel) {}
            } message: {
                Text("Files can be several times larger. In a meeting-like test, a window recording took about \(ScreenSize.fps60) MB per minute at 60 fps, against \(ScreenSize.fps15) MB per minute at 15 fps.")
            }
            Picker(selection: quality) {
                ForEach(ScreenQuality.presets, id: \.value) { preset in
                    Text("\(String(localized: preset.title)) (~\(preset.megabytesPerMinute) MB/min)").tag(preset.value)
                }
            } label: {
                Text("Quality")
                Text("Sizes measured in a meeting-like window recording at 15 fps. More motion or a higher frame rate makes larger files.")
            }
        }
        Section {
            Toggle(isOn: model.setting(\.screenTranscribes)) {
                Text("Transcribe screen recordings")
                Text("Off: screen and sound only. No transcript, summary or chat until you transcribe it later.")
            }
        }
        Section {
            Toggle("Show the cursor", isOn: model.setting(\.screenShowsCursor))
            Toggle("Highlight clicks", isOn: model.setting(\.screenShowsClicks))
        }
        Section {
            Toggle(isOn: Binding(get: { model.settings.screenCamera }, set: { setCamera($0) })) {
                Text("Camera bubble")
                Text("Shows your camera in a round bubble on screen.")
            }
            Toggle(isOn: Binding(
                get: { model.settings.screenCameraBlur },
                set: { on in
                    model.updateSettings { $0.screenCameraBlur = on }
                    model.screenRecording.setOverlays(model.settings)
                }
            )) {
                Text("Blur background")
                Text("Blurs everything behind you in the camera bubble.")
            }
            .disabled(!model.settings.screenCamera)
            // The row style does not dim a disabled row by itself; make it visibly inactive.
            .opacity(model.settings.screenCamera ? 1 : 0.45)
            Toggle(isOn: Binding(get: { model.settings.screenKeyStrokes }, set: { setKeyStrokes($0) })) {
                Text("Key presses")
                Text("Shows shortcuts such as ⌘K at the bottom of the screen.")
            }
            Toggle(isOn: Binding(
                get: { model.settings.screenAllKeys },
                set: { on in
                    model.updateSettings { $0.screenAllKeys = on }
                    model.screenRecording.setOverlays(model.settings)
                }
            )) {
                Text("Show all keys")
                Text("Everything you type becomes visible in the video")
            }
            .disabled(!model.settings.screenKeyStrokes)
            // The row style does not dim a disabled row by itself; make it visibly inactive.
            .opacity(model.settings.screenKeyStrokes ? 1 : 0.45)
        }
        permissionsSection
    }

    /// The permissions the screen features use, each with its state and the action that state needs: Screen Recording
    /// always, Camera with the camera bubble on, Input Monitoring with key presses on. "Allow…" shows the system prompt
    /// (only while not determined; Screen Recording's only the first time); a refused permission points to System
    /// Settings. Screen Recording and Input Monitoring apply once EchoDiar is reopened.
    private var permissionsSection: some View {
        Section("Permissions") {
            PermissionRow(title: "Screen Recording", granted: screenAccess) {
                Text("Records the screen.")
                if !screenAccess { Text("After allowing it, quit and reopen EchoDiar.") }
            } actions: {
                Button("Allow…") { requestScreen() }
                Button("Open System Settings") { PrivacySettings.openScreenRecording() }
            }
            if model.settings.screenCamera {
                PermissionRow(title: "Camera", granted: cameraStatus == .authorized) {
                    Text("Shows the camera bubble.")
                } actions: {
                    if cameraStatus == .notDetermined {
                        Button("Allow…") { requestCamera() }
                    } else {
                        Button("Open System Settings") { PrivacySettings.openCamera() }
                    }
                }
            }
            if model.settings.screenKeyStrokes {
                PermissionRow(title: "Input Monitoring", granted: keyAccess == .granted) {
                    Text("Shows key presses.")
                    if keyAccess != .granted { Text("After allowing it, quit and reopen EchoDiar.") }
                } actions: {
                    if keyAccess == .notDetermined {
                        Button("Allow…") { requestKeyAccess() }
                    } else {
                        Button("Open System Settings") { PrivacySettings.openInputMonitoring() }
                    }
                }
            }
        }
        // Also changed from the record capsule's menu while this page was not shown.
        .onAppear { refreshScreenPermissions() }
    }

    /// Picking 60 fps asks first (much larger files); the other rates apply at once.
    private var frameRate: Binding<Int> {
        Binding(
            get: { model.settings.screenFPS },
            set: { fps in
                if fps == 60 && model.settings.screenFPS != 60 {
                    confirming60FPS = true
                } else {
                    model.updateSettings { $0.screenFPS = fps }
                }
            })
    }

    /// The preset nearest the stored quality, which stays as it is until a preset is picked.
    private var quality: Binding<Double> {
        Binding(
            get: { ScreenQuality.nearest(to: model.settings.screenQuality).value },
            set: { value in model.updateSettings { $0.screenQuality = value } })
    }

    /// Switching on asks for Camera access when not determined yet; a grant during a running video shows the bubble.
    private func setCamera(_ on: Bool) {
        model.updateSettings { $0.screenCamera = on }
        model.screenRecording.setOverlays(model.settings)
        cameraStatus = CameraBubbleController.authorizationStatus
        guard on, cameraStatus == .notDetermined else { return }
        requestCamera()
    }

    /// Asks for Camera access, then refreshes its Permissions row; a grant during a running video shows the bubble.
    private func requestCamera() {
        Task {
            _ = await CameraBubbleController.requestAccess()
            cameraStatus = CameraBubbleController.authorizationStatus
            model.screenRecording.setOverlays(model.settings)
        }
    }

    /// Switching on asks for Input Monitoring when EchoDiar does not have it; the Permissions row shows the result.
    private func setKeyStrokes(_ on: Bool) {
        model.updateSettings { $0.screenKeyStrokes = on }
        keyAccess = on && !KeyStrokeHUDController.hasAccess
            ? KeyStrokeHUDController.requestAccess() : KeyStrokeHUDController.access
        model.screenRecording.setOverlays(model.settings)
    }

    /// "Allow…" of Input Monitoring.
    private func requestKeyAccess() {
        keyAccess = KeyStrokeHUDController.requestAccess()
        model.screenRecording.setOverlays(model.settings)
    }

    /// "Allow…" of Screen Recording: the system prompt shows the first time only.
    private func requestScreen() {
        screenAccess = CGRequestScreenCaptureAccess() || CGPreflightScreenCaptureAccess()
    }

    /// Reads the three permissions again (back from System Settings, or the page shown again). Never prompts.
    private func refreshScreenPermissions() {
        screenAccess = CGPreflightScreenCaptureAccess()
        cameraStatus = CameraBubbleController.authorizationStatus
        keyAccess = KeyStrokeHUDController.access
    }

    /// "Ask" is stored as no entry, the default.
    private func behavior(_ app: MeetingApp) -> Binding<MeetingBehavior> {
        Binding(
            get: { model.settings.perAppBehavior[app.appBundleID] ?? .ask },
            set: { value in model.updateSettings { $0.perAppBehavior[app.appBundleID] = value == .ask ? nil : value } })
    }
}

/// A permission of Recording › Screen: its name and what it is for (`note`) on the left, then "Allowed" in green, or
/// "Not allowed" in orange followed by `actions`.
private struct PermissionRow<Note: View, Actions: View>: View {
    let title: LocalizedStringKey
    let granted: Bool
    @ViewBuilder let note: Note
    @ViewBuilder let actions: Actions

    var body: some View {
        LabeledContent {
            HStack(spacing: 8) {
                if granted {
                    Label("Allowed", systemImage: "checkmark.circle.fill").foregroundStyle(.green)
                } else {
                    Label("Not allowed", systemImage: "exclamationmark.circle.fill").foregroundStyle(.orange)
                    actions
                }
            }
        } label: {
            Text(title)
            note
        }
    }
}

/// Measured screen recording sizes, MB per minute: the Phase 1 gate's meeting-like scenario, window mode, MacBook
/// Retina display (docs/qa/2026-11-23-screen-recording.md, Phase 1 gate run 1).
private enum ScreenSize {
    /// 15 fps at quality 0.60, 0.75 (the default) and 0.85.
    static let quality060 = 2.35
    static let quality075 = 4.87
    static let quality085 = 8.42
    /// 60 fps at quality 0.75.
    static let fps60At075 = 13.24

    /// "13.24" and "4.87", for the 60 fps confirmation.
    static var fps60: String { fps60At075.formatted(.number.precision(.fractionLength(2))) }
    static var fps15: String { quality075.formatted(.number.precision(.fractionLength(2))) }
}

/// The quality presets of Recording › Screen.
private struct ScreenQuality {
    let value: Double
    let title: LocalizedStringResource
    /// The measured size at 15 fps, rounded to one decimal ("4.9").
    let megabytesPerMinute: String

    static let presets = [
        ScreenQuality(value: 0.60, title: "Smaller", size: ScreenSize.quality060),
        ScreenQuality(value: 0.75, title: "Balanced", size: ScreenSize.quality075),
        ScreenQuality(value: 0.85, title: "Sharper", size: ScreenSize.quality085),
    ]

    init(value: Double, title: LocalizedStringResource, size: Double) {
        self.value = value
        self.title = title
        megabytesPerMinute = size.formatted(.number.precision(.fractionLength(1)))
    }

    static func nearest(to quality: Double) -> ScreenQuality {
        presets.min { abs($0.value - quality) < abs($1.value - quality) }!
    }
}

/// Whether transcription tells the meeting audio's speakers apart, shown in Recording › After recording and in
/// Transcription › Diarization model.
struct SpeakerSeparationToggle: View {
    let model: AppModel

    var body: some View {
        Toggle(isOn: model.setting(\.diarization)) {
            Text("Tell speakers apart")
            Text("Labels each voice in the meeting audio: Speaker 1, 2, … Off: the meeting audio is one speaker.")
        }
    }
}

/// The speaker count hint, shown in Recording › After recording and in Transcription › Diarization model.
struct SpeakerCountPicker: View {
    let model: AppModel

    var body: some View {
        Picker(selection: model.setting(\.speakerCountHint)) {
            Text("Auto").tag(Int?.none)
            ForEach(AppSettings.speakerCountHintRange, id: \.self) { Text("\($0)").tag(Int?.some($0)) }
        } label: {
            Text("Number of other speakers")
            if model.diarizerAvailable {
                Text("A hint for telling the system audio's speakers apart.")
                if SpeakerDiarizer.nemotronPresets[model.defaultDiarizerModelID] != nil {
                    Text("Nemotron tells up to 8 speakers apart and ignores this number; above 8, pyannote is used when installed.")
                } else if let providerID = CloudTranscription.providerID(engineID: model.defaultDiarizerModelID),
                          let config = model.cloudDiarizerConfigs.first(where: { $0.id == providerID }),
                          CloudTranscription.diarizes(config, model: CloudTranscription.diarizationModel(for: config)) {
                    Text("Cloud diarization does not take this number.")
                }
            } else {
                Text("Needs the speaker model: download it in Settings › AI Providers › Local AI.")
            }
        }
    }
}

/// A whole number typed or stepped within `range`.
struct NumberRow: View {
    let title: LocalizedStringKey
    let unit: String
    @Binding var value: Int
    let range: ClosedRange<Int>

    var body: some View {
        LabeledContent(title) {
            HStack(spacing: 6) {
                TextField(title, value: clamped, format: .number)
                    .labelsHidden()
                    .multilineTextAlignment(.trailing)
                    .frame(width: 60)
                Stepper(title, value: clamped, in: range)
                    .labelsHidden()
                Text(unit)
                    .foregroundStyle(.secondary)
                    .frame(minWidth: 26, alignment: .leading)
            }
        }
        .help("\(range.lowerBound)–\(range.upperBound) \(unit)")
    }

    private var clamped: Binding<Int> {
        Binding(get: { value }, set: { value = min(max($0, range.lowerBound), range.upperBound) })
    }
}

/// A Core Audio device with input channels.
private struct InputDevice: Identifiable, Sendable {
    /// The device UID, as stored in `AppSettings.micDeviceUID`.
    let id: String
    let name: String

    private static let system = AudioObjectID(kAudioObjectSystemObject)
    private static let devicesAddress = AudioObjectPropertyAddress(
        mSelector: kAudioHardwarePropertyDevices, mScope: kAudioObjectPropertyScopeGlobal,
        mElement: kAudioObjectPropertyElementMain)
    /// Core Audio's per-process aggregate for the default devices ("CADefaultDeviceAggregate-<pid>-<n>").
    private static let defaultAggregatePrefix = "CADefaultDeviceAggregate-"

    /// Read again on each `changes()` value. Private aggregates (another app's or EchoDiar's own tap) are skipped;
    /// aggregates the user made in Audio MIDI Setup stay.
    static func all() -> [InputDevice] {
        var address = devicesAddress
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(system, &address, 0, nil, &size) == noErr else { return [] }
        var ids = [AudioDeviceID](repeating: 0, count: Int(size) / MemoryLayout<AudioDeviceID>.size)
        guard AudioObjectGetPropertyData(system, &address, 0, nil, &size, &ids) == noErr else { return [] }
        return ids.compactMap { id in
            guard hasInput(id), !isPrivateAggregate(id), let uid = string(id, kAudioDevicePropertyDeviceUID),
                  !uid.hasPrefix(defaultAggregatePrefix),
                  let name = string(id, kAudioObjectPropertyName) else { return nil }
            return InputDevice(id: uid, name: name)
        }
        .sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
    }

    /// Yields once at start, then each time the device list changes (plugged, unplugged, aggregate made or removed).
    /// The listener is removed when the stream ends (the consuming task is cancelled).
    static func changes() -> AsyncStream<Void> {
        AsyncStream(bufferingPolicy: .bufferingNewest(1)) { continuation in
            continuation.yield()
            // `@Sendable` so `onTermination` can capture it; the same block must be passed to remove it.
            let listener: @convention(block) @Sendable (UInt32, UnsafePointer<AudioObjectPropertyAddress>) -> Void = {
                _, _ in continuation.yield()
            }
            var address = devicesAddress
            guard AudioObjectAddPropertyListenerBlock(system, &address, nil, listener) == noErr else {
                continuation.finish()
                return
            }
            continuation.onTermination = { _ in
                var address = devicesAddress
                AudioObjectRemovePropertyListenerBlock(system, &address, nil, listener)
            }
        }
    }

    /// `kAudioAggregateDeviceIsPrivateKey` in the composition, which only aggregate devices have.
    private static func isPrivateAggregate(_ device: AudioDeviceID) -> Bool {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioAggregateDevicePropertyComposition, mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain)
        guard AudioObjectHasProperty(device, &address) else { return false }
        var value: Unmanaged<CFDictionary>?
        var size = UInt32(MemoryLayout<Unmanaged<CFDictionary>?>.size)
        guard AudioObjectGetPropertyData(device, &address, 0, nil, &size, &value) == noErr,
              let composition = value?.takeRetainedValue() as? [String: Any] else { return false }
        return (composition[kAudioAggregateDeviceIsPrivateKey] as? NSNumber)?.boolValue == true
    }

    private static func hasInput(_ device: AudioDeviceID) -> Bool {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioDevicePropertyStreams, mScope: kAudioObjectPropertyScopeInput,
            mElement: kAudioObjectPropertyElementMain)
        var size: UInt32 = 0
        return AudioObjectGetPropertyDataSize(device, &address, 0, nil, &size) == noErr && size > 0
    }

    private static func string(_ device: AudioDeviceID, _ selector: AudioObjectPropertySelector) -> String? {
        var address = AudioObjectPropertyAddress(
            mSelector: selector, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var value: Unmanaged<CFString>?
        var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        guard AudioObjectGetPropertyData(device, &address, 0, nil, &size, &value) == noErr,
              let value else { return nil }
        return value.takeRetainedValue() as String
    }
}
