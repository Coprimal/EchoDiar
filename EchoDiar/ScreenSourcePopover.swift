import AVFoundation
import AppKit
import EchoCore
import EchoScreen
import ScreenCaptureKit
import SwiftUI

/// The screen control in the record capsule, beside Record: its icon shows the armed source (accent tinted), a click
/// opens `ScreenSourcePopover` at once. It reads only the controller's state, never the screen permission.
struct ScreenSourceButton: View {
    let model: AppModel
    @State private var showsPopover = false

    var body: some View {
        let screen = model.screenRecording
        Button { showsPopover.toggle() } label: {
            Label {
                Text("Screen")
            } icon: {
                if let source = screen.armedSource {
                    Image(systemName: Self.symbol(source)).foregroundStyle(Color.accentColor)
                } else {
                    Image(systemName: "rectangle.dashed.badge.record")
                }
            }
        }
        .disabled(model.isStarting)
        .help(help(screen))
        .popover(isPresented: $showsPopover, arrowEdge: .bottom) {
            ScreenSourcePopover(model: model)
        }
    }

    private func help(_ screen: ScreenRecordingController) -> Text {
        guard screen.armedSource != nil else { return Text("Also record a screen, a window or an area") }
        if let name = screen.armedName { return Text("The next recording also records \(name)") }
        return Text("The next recording also records the screen")
    }

    static func symbol(_ source: ScreenSource) -> String {
        switch source {
        case .display: "display"
        case .window: "macwindow"
        case .area: "rectangle.dashed"
        }
    }
}

/// What to record with the next recording started with Record: the displays (name and pixel size), the on-screen
/// titled windows by app, "Select area…" and Off, then "Transcribe this recording" while a source is armed. Opens at
/// once with placeholder rows; the list loads off the main thread, and only once the screen permission is granted
/// (else a note asks for it). Each recording is armed explicitly: the armed source is cleared once a recording starts
/// with it. A display or an area is also remembered in the settings and shown as "Last used" or "Last area" for a
/// one-click re-arm; a window is not remembered. "Select area…" closes the popover and shows the selection overlays.
/// Opened from the recording header while recording: a chosen source (or a selected area) also starts the video at
/// once and closes the popover, and the transcription toggle is not shown (the recording's mark is set at its start).
struct ScreenSourcePopover: View {
    let model: AppModel
    @State private var phase = Phase.loading
    /// Read on open and after a request; neither read prompts.
    @State private var cameraStatus = CameraBubbleController.authorizationStatus
    @State private var keyAccess = KeyStrokeHUDController.hasAccess
    @Environment(\.dismiss) private var dismiss

    private enum Phase {
        case loading, denied, failed
        case loaded(ScreenSourceList)
    }

    private static let rowHeight: CGFloat = 26
    private static let headerHeight: CGFloat = 22
    private static let rowSpacing: CGFloat = 2
    private static let maxListHeight: CGFloat = 320

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            VStack(alignment: .leading, spacing: 2) {
                HStack {
                    Text("Record the screen").font(.headline)
                    Spacer(minLength: 8)
                    if case .loading = phase { DelayedSpinner() }
                }
                Text(model.isRecording
                     ? "Records it in this recording from now on."
                     : "Applies to recordings you start with Record or its shortcuts, not from the calendar or a meeting prompt.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            content
            Divider()
            overlayToggles
            if model.screenRecording.armedSource != nil && !model.isRecording {
                Divider()
                transcribeToggle
            }
        }
        .padding(12)
        .frame(width: 340)
        .task { await load() }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            // Back from System Settings: a permission granted during a running video brings its overlay up.
            cameraStatus = CameraBubbleController.authorizationStatus
            keyAccess = KeyStrokeHUDController.hasAccess
            model.screenRecording.setOverlays(model.settings)
        }
    }

    @ViewBuilder private var content: some View {
        switch phase {
        case .loading:
            VStack(alignment: .leading, spacing: Self.rowSpacing) {
                ForEach(0..<4, id: \.self) { index in PlaceholderRow(width: [140, 110, 170, 90][index]) }
                offRow
            }
        case .denied:
            permissionNote
            offRow
        case .failed:
            Text("Could not list the screens and windows.")
                .font(.callout)
                .foregroundStyle(.secondary)
            offRow
        case .loaded(let list):
            ScrollView {
                VStack(alignment: .leading, spacing: Self.rowSpacing) { rows(list) }
            }
            .scrollBounceBehavior(.basedOnSize)
            .frame(height: min(listHeight(list), Self.maxListHeight))
        }
    }

    // MARK: - Rows

    @ViewBuilder private func rows(_ list: ScreenSourceList) -> some View {
        let armed = model.screenRecording.armedSource
        let remembered = model.settings.screenSource
        ForEach(list.displays) { display in
            let source = ScreenSource.display(display.id)
            SourceRow(title: display.name, detail: "\(display.pixelWidth) × \(display.pixelHeight)",
                      systemImage: "display", selected: armed == source,
                      lastUsed: armed == nil && remembered == RememberedScreenSource(displayID: display.id)) {
                choose(source, name: display.name)
            }
        }
        ForEach(list.apps) { app in
            AppWindowsHeader(title: app.name)
            ForEach(app.windows) { window in
                let source = ScreenSource.window(window.id)
                SourceRow(title: window.title, detail: nil, systemImage: "macwindow", selected: armed == source,
                          lastUsed: false) {
                    choose(source, name: window.title)
                }
            }
        }
        if let area = rememberedArea(list) {
            SourceRow(title: String(localized: "Last area"), detail: area.detail, systemImage: "rectangle.dashed",
                      selected: armed == area.source, lastUsed: false) {
                choose(area.source, name: String(localized: "the last area"))
            }
        }
        SourceRow(title: String(localized: "Select area…"), detail: nil, systemImage: "rectangle.dashed",
                  selected: false, lastUsed: false) {
            selectArea()
        }
        offRow
    }

    private var offRow: some View {
        SourceRow(title: String(localized: "Off"), detail: nil, systemImage: "speaker.wave.2",
                  selected: model.screenRecording.armedSource == nil, lastUsed: false) {
            choose(nil, name: nil)
        }
    }

    /// The remembered area while its display is still connected, with its size in points.
    private func rememberedArea(_ list: ScreenSourceList) -> (source: ScreenSource, detail: String)? {
        guard let remembered = model.settings.screenSource, let rect = remembered.area,
              let display = list.displays.first(where: { $0.id == remembered.displayID }) else { return nil }
        return (ScreenRecordingController.source(remembered),
                "\(Int(rect.width)) × \(Int(rect.height)), \(display.name)")
    }

    /// The rows' and headers' heights: the list takes its content's height up to `maxListHeight`, then scrolls.
    private func listHeight(_ list: ScreenSourceList) -> CGFloat {
        let windows = list.apps.reduce(0) { $0 + $1.windows.count }
        let area = rememberedArea(list) != nil ? 1 : 0
        let rows = list.displays.count + windows + area + 2
        let items = rows + list.apps.count
        return CGFloat(rows) * Self.rowHeight + CGFloat(list.apps.count) * Self.headerHeight
            + CGFloat(max(items - 1, 0)) * Self.rowSpacing
    }

    // MARK: - Permission and transcription

    /// No screen permission yet: the system prompt only on the button, never on open.
    private var permissionNote: some View {
        VStack(alignment: .leading, spacing: 8) {
            Label("EchoDiar needs Screen Recording permission to record the screen.", systemImage: "lock")
                .font(.callout)
                .fixedSize(horizontal: false, vertical: true)
            HStack(spacing: 12) {
                Button("Allow Screen Recording") { _ = CGRequestScreenCaptureAccess() }
                    .modifier(CapsuleButton())
                Button("Open System Settings") { PrivacySettings.openScreenRecording() }
                    .buttonStyle(.link)
                    .font(.caption)
            }
            Text("macOS applies the permission once EchoDiar is quit and opened again.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(Palette.raisedSurface))
    }

    private var transcribeToggle: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                Text("Transcribe this recording")
                    .accessibilityHidden(true)
                Spacer(minLength: 8)
                Toggle(isOn: Binding(
                    get: { model.settings.screenTranscribes },
                    set: { on in model.updateSettings { $0.screenTranscribes = on } }
                )) {
                    Text("Transcribe this recording")
                }
                .toggleStyle(CompactSwitch())
            }
            if !model.settings.screenTranscribes {
                Text("Screen and sound only. No transcript, summary or chat until you transcribe it later.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    // MARK: - Overlays

    /// Camera and Key presses: on in display and area recordings only (a window recording would not show them).
    /// Switching one on asks for its permission when not determined; a denied permission shows a note.
    private var overlayToggles: some View {
        let screen = model.screenRecording
        let window = [screen.armedSource, screen.isOn ? screen.videoSource : nil].contains { source in
            if case .window = source { true } else { false }
        }
        return VStack(alignment: .leading, spacing: 6) {
            overlaySwitch(String(localized: "Camera"), isOn: Binding(
                get: { model.settings.screenCamera },
                set: { on in setCamera(on) }
            ), disabled: window)
            if !window, model.settings.screenCamera, cameraStatus == .denied || cameraStatus == .restricted {
                deniedNote(String(localized: "EchoDiar needs Camera access to show the camera bubble.")) {
                    PrivacySettings.openCamera()
                }
            }
            if !window, model.settings.screenCamera, cameraStatus == .notDetermined {
                deniedNote(
                    String(localized: "EchoDiar needs Camera access to show the camera bubble."),
                    button: String(localized: "Allow Camera"), action: requestCamera)
            }
            overlaySwitch(String(localized: "Key presses"), isOn: Binding(
                get: { model.settings.screenKeyStrokes },
                set: { on in setKeyStrokes(on) }
            ), disabled: window)
            if !window, model.settings.screenKeyStrokes, !keyAccess {
                deniedNote(String(localized: "EchoDiar needs Input Monitoring access to show key presses.")) {
                    PrivacySettings.openInputMonitoring()
                }
            }
            if !window, model.settings.screenKeyStrokes, model.settings.screenAllKeys {
                Text("All keys are shown")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            if window {
                Text("Available when recording a display or an area")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    private func overlaySwitch(_ title: String, isOn: Binding<Bool>, disabled: Bool) -> some View {
        HStack {
            Text(title)
                .accessibilityHidden(true)
            Spacer(minLength: 8)
            Toggle(isOn: isOn) { Text(title) }
                .toggleStyle(CompactSwitch())
        }
        .disabled(disabled)
        .allowsHitTesting(!disabled)
        .opacity(disabled ? 0.5 : 1)
    }

    private func deniedNote(
        _ text: String, button: String = String(localized: "Open System Settings"), action: @escaping () -> Void
    ) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(text)
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Button(button, action: action)
                .buttonStyle(.link)
                .font(.caption)
        }
    }

    private func setCamera(_ on: Bool) {
        model.updateSettings { $0.screenCamera = on }
        model.screenRecording.setOverlays(model.settings)
        cameraStatus = CameraBubbleController.authorizationStatus
        guard on, cameraStatus == .notDetermined else { return }
        requestCamera()
    }

    /// Asks for Camera access, then refreshes the note; a grant during a running video shows the bubble at once.
    private func requestCamera() {
        Task {
            _ = await CameraBubbleController.requestAccess()
            cameraStatus = CameraBubbleController.authorizationStatus
            model.screenRecording.setOverlays(model.settings)
        }
    }

    private func setKeyStrokes(_ on: Bool) {
        model.updateSettings { $0.screenKeyStrokes = on }
        if on, !KeyStrokeHUDController.hasAccess { KeyStrokeHUDController.requestAccess() }
        keyAccess = KeyStrokeHUDController.hasAccess
        model.screenRecording.setOverlays(model.settings)
    }

    // MARK: - Actions

    /// Arms `source` (nil = sound only) and remembers a display or an area in the settings. While recording, starts
    /// it at once and closes the popover.
    private func choose(_ source: ScreenSource?, name: String?) {
        if let remembered = model.screenRecording.arm(source, name: name) { Self.remember(remembered, in: model) }
        guard model.isRecording else { return }
        if source != nil, !model.screenRecording.isOn { model.toggleScreenRecording() }
        Self.disarmUnlessStarted(model)
        dismiss()
    }

    /// A source picked during a recording is meant for that recording only: when it could not start (paused, the
    /// recording ended, its one video span used), it must not stay armed for the next Record. The remembered display or
    /// area stays in the settings for a one-click re-arm.
    private static func disarmUnlessStarted(_ model: AppModel) {
        if !model.screenRecording.isOn { model.screenRecording.arm(nil) }
    }

    /// Closes the popover, then the overlays take the screen; a confirmed area is armed and remembered (and started
    /// while recording). The popover is gone by then, so the hand-over touches only the model.
    private func selectArea() {
        dismiss()
        let duringRecording = model.isRecording
        model.screenRecording.selectArea { [model] remembered in
            Self.remember(remembered, in: model)
            guard duringRecording else { return }
            if model.isRecording, !model.screenRecording.isOn { model.toggleScreenRecording() }
            Self.disarmUnlessStarted(model)
        }
    }

    private static func remember(_ remembered: RememberedScreenSource, in model: AppModel) {
        if remembered != model.settings.screenSource { model.updateSettings { $0.screenSource = remembered } }
    }

    private func load() async {
        let loaded = await Task.detached(priority: .userInitiated) { await ScreenSourceList.load() }.value
        switch loaded {
        case .denied: phase = .denied
        case .failed: phase = .failed
        case .list(var list):
            for index in list.displays.indices {
                list.displays[index].name = Self.displayName(list.displays[index].id, index: index)
            }
            phase = .loaded(list)
        }
    }

    /// The screen's name as System Settings shows it ("Built-in Retina Display"), else "Display 2".
    private static func displayName(_ id: CGDirectDisplayID, index: Int) -> String {
        let key = NSDeviceDescriptionKey("NSScreenNumber")
        let screen = NSScreen.screens.first { ($0.deviceDescription[key] as? NSNumber)?.uint32Value == id }
        return screen?.localizedName ?? String(localized: "Display \(index + 1)")
    }
}

/// The displays and the on-screen titled windows by app, read from `SCShareableContent` off the main thread.
struct ScreenSourceList: Sendable {
    struct Display: Sendable, Identifiable {
        let id: CGDirectDisplayID
        let pixelWidth: Int
        let pixelHeight: Int
        /// Set on the main thread from `NSScreen`.
        var name = ""
    }

    struct Window: Sendable, Identifiable {
        let id: CGWindowID
        let title: String
    }

    struct App: Sendable, Identifiable {
        let id: String
        let name: String
        let windows: [Window]
    }

    enum Loaded: Sendable {
        case denied, failed
        case list(ScreenSourceList)
    }

    var displays: [Display]
    var apps: [App]

    /// Without the screen permission returns `.denied` before touching `SCShareableContent`, which would prompt.
    /// Windows: on screen, at the normal level (layer 0: not the pill, menus or overlays), titled and not tiny.
    static func load() async -> Loaded {
        guard CGPreflightScreenCaptureAccess() else { return .denied }
        let content: SCShareableContent
        do {
            content = try await SCShareableContent.excludingDesktopWindows(true, onScreenWindowsOnly: true)
        } catch {
            NSLog("EchoDiar: could not list the screen sources: \(error)")
            return .failed
        }
        let displays = content.displays
            .sorted { CGDisplayIsMain($0.displayID) != 0 && CGDisplayIsMain($1.displayID) == 0 }
            .map { display in
                let mode = CGDisplayCopyDisplayMode(display.displayID)
                return Display(id: display.displayID, pixelWidth: mode?.pixelWidth ?? display.width,
                               pixelHeight: mode?.pixelHeight ?? display.height)
            }
        var groups: [String: (name: String, windows: [Window])] = [:]
        for window in content.windows {
            guard window.isOnScreen, window.windowLayer == 0, let title = window.title, !title.isEmpty,
                  window.frame.width >= 40, window.frame.height >= 40,
                  let app = window.owningApplication else { continue }
            let key = app.bundleIdentifier.isEmpty ? String(app.processID) : app.bundleIdentifier
            groups[key, default: (app.applicationName, [])].windows.append(Window(id: window.windowID, title: title))
        }
        let apps = groups.map { App(id: $0.key, name: $0.value.name, windows: $0.value.windows) }
            .sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
        return .list(ScreenSourceList(displays: displays, apps: apps))
    }
}

/// A source: its icon, name and size; the armed one has the accent fill and a checkmark, the remembered one says
/// "Last used". A lighter fill on hover.
private struct SourceRow: View {
    let title: String
    let detail: String?
    let systemImage: String
    let selected: Bool
    let lastUsed: Bool
    let action: () -> Void
    @State private var hovered = false

    var body: some View {
        Button(action: action) {
            HStack(spacing: 8) {
                Image(systemName: systemImage)
                    .foregroundStyle(.secondary)
                    .frame(width: 18)
                Text(title)
                    .lineLimit(1)
                    .truncationMode(.middle)
                if let detail {
                    Text(detail)
                        .foregroundStyle(.secondary)
                        .monospacedDigit()
                        .lineLimit(1)
                }
                Spacer(minLength: 6)
                if selected {
                    Image(systemName: "checkmark").foregroundStyle(Color.accentColor)
                } else if lastUsed {
                    Text("Last used").font(.caption).foregroundStyle(.secondary)
                }
            }
            .padding(.horizontal, 8)
            .frame(height: 26)
            .background(RoundedRectangle(cornerRadius: 6, style: .continuous).fill(fill))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hovered = $0 }
        .help(title)
    }

    private var fill: Color {
        if selected { return Color.accentColor.opacity(0.18) }
        return hovered ? Palette.hoverFill : .clear
    }
}

/// An app's name above its windows.
private struct AppWindowsHeader: View {
    let title: String

    var body: some View {
        Text(title)
            .font(.caption.weight(.semibold))
            .foregroundStyle(.secondary)
            .lineLimit(1)
            .padding(.horizontal, 8)
            .frame(height: 22, alignment: .bottom)
    }
}

/// A row's shape while the sources load: an icon and a name bar, static.
private struct PlaceholderRow: View {
    let width: CGFloat

    var body: some View {
        HStack(spacing: 8) {
            RoundedRectangle(cornerRadius: 3, style: .continuous)
                .frame(width: 18, height: 14)
            Capsule()
                .frame(width: width, height: 10)
            Spacer(minLength: 0)
        }
        .foregroundStyle(Palette.hoverFill)
        .padding(.horizontal, 8)
        .frame(height: 26)
        .accessibilityHidden(true)
    }
}

/// A small spinner that fades in only after 150 ms, so a fast load does not flash it.
private struct DelayedSpinner: View {
    @State private var shown = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        ProgressView()
            .controlSize(.small)
            .opacity(shown ? 1 : 0)
            .task {
                try? await Task.sleep(for: .milliseconds(150))
                guard !Task.isCancelled else { return }
                withAnimation(reduceMotion ? nil : Motion.standard) { shown = true }
            }
    }
}
