import AVFoundation
import AppKit
import EchoAudio
import EchoCore
import SwiftUI

/// The popover of the record capsule's chevron: the screen options, the system audio choice and the language, in one
/// small form. It stays open while its controls are used and closes on a click outside it or on Esc. The screen
/// items are always listed; they are disabled and muted while "Record the screen too" is off.
struct RecordingOptionsPopover: View {
    let model: AppModel
    @Binding var isPresented: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            section("Screen") { screenItems }
                .toggleStyle(.checkbox)
            section("System audio") { systemAudioItems }
                // Apps that started playing since the last look show up.
                .onAppear { model.refreshTargets() }
            section("Language") {
                LanguagePicker(title: "Language", selection: languageBinding, model: model)
                    .labelsHidden()
            }
        }
        .padding(14)
        // As wide as its widest row.
        .fixedSize()
    }

    private func section(_ title: LocalizedStringKey, @ViewBuilder content: () -> some View) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title).font(.caption).foregroundStyle(.secondary)
            content()
        }
    }

    @ViewBuilder private var screenItems: some View {
        let screenOn = model.settings.screenRecordingEnabled
        let closeFirst = { isPresented = false }
        Toggle("Record the screen too", isOn: Binding(
            get: { screenOn },
            set: { ScreenOptionActions.setEnabled($0, model: model, beforeAlert: closeFirst) }))
        VStack(alignment: .leading, spacing: 6) {
            Toggle(isOn: model.setting(\.screenTranscribes)) { muted(Text("Transcribe this recording"), !screenOn) }
            Toggle(isOn: Binding(
                get: { model.settings.screenCamera },
                set: { ScreenOptionActions.setCamera($0, model: model, beforeAlert: closeFirst) })) {
                muted(Text("Camera bubble"), !screenOn)
            }
            Toggle(isOn: Binding(
                get: { model.settings.screenKeyStrokes },
                set: { ScreenOptionActions.setKeyStrokes($0, model: model, beforeAlert: closeFirst) })) {
                muted(Text("Key presses"), !screenOn)
            }
        }
        .padding(.leading, 20)
        .disabled(!screenOn)
    }

    @ViewBuilder private var systemAudioItems: some View {
        let screenOn = model.settings.screenRecordingEnabled
        RadioRow(isOn: targetBinding(.global)) { Text("All system audio") }
        RadioRow(isOn: noSystemAudioBinding) { Text("No system audio") }
            .disabled(!screenOn)
        ForEach(model.targetGroups, id: \.bundleID) { group in
            RadioRow(isOn: targetBinding(.app(group))) {
                if group.needsGlobalTap {
                    Text("Only \(group.name) (records all system audio)")
                } else {
                    Text("Only \(group.name)")
                }
            }
        }
    }

    private func muted(_ text: Text, _ isMuted: Bool) -> some View {
        text.foregroundStyle(isMuted ? .secondary : .primary)
    }

    // MARK: - Bindings

    /// Checked when `target` is the selected one (and "No system audio" is not); matched by app, since process lists
    /// change.
    private func targetBinding(_ target: CaptureTarget) -> Binding<Bool> {
        Binding(
            get: { !model.systemAudioOff && Self.targetID(model.selectedTarget) == Self.targetID(target) },
            set: {
                guard $0 else { return }
                model.selectedTarget = target
                model.systemAudioOff = false
            }
        )
    }

    /// "No system audio", with "Record the screen too" on: the screen recording captures the microphone only. The
    /// target goes back to all system audio, which is what remains once the screen is switched off.
    private var noSystemAudioBinding: Binding<Bool> {
        Binding(
            get: { model.systemAudioOff },
            set: {
                guard $0 else { return }
                model.selectedTarget = .global
                model.systemAudioOff = true
            }
        )
    }

    private var languageBinding: Binding<String> {
        Binding(get: { model.recordingLanguage }, set: { model.setRecordingLanguage($0) })
    }

    private static func targetID(_ target: CaptureTarget) -> String? {
        if case .app(let group) = target { return group.bundleID }
        return nil
    }
}

/// One choice of a single-choice list: a radio dot and its label, muted while disabled. Clicking it selects it.
private struct RadioRow<Content: View>: View {
    @Binding var isOn: Bool
    @ViewBuilder let label: Content
    @Environment(\.isEnabled) private var isEnabled

    var body: some View {
        Button { isOn = true } label: {
            HStack(alignment: .firstTextBaseline, spacing: 6) {
                Image(systemName: isOn ? "largecircle.fill.circle" : "circle")
                    .foregroundStyle(isOn && isEnabled ? AnyShapeStyle(.tint) : AnyShapeStyle(.secondary))
                label
                    .foregroundStyle(isEnabled ? .primary : .secondary)
                    .fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: 0)
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(isOn ? .isSelected : [])
    }
}

/// The screen options' setters, shared by the recording options popover and the record capsule's camera bubble
/// button. Switching "Record the screen too" on without the screen permission asks for it (the system prompt comes
/// only from this click or from "Allow…" in Settings) and stays off, with an alert pointing to System Settings.
/// Switching the camera bubble or key presses on asks for its permission; without it, an alert points to System
/// Settings (the option stays on and works once allowed, as in Settings; key presses only after EchoDiar is reopened).
/// `beforeAlert` runs just before an alert shows, so a popover can close first instead of covering it.
@MainActor
enum ScreenOptionActions {
    static func setEnabled(_ on: Bool, model: AppModel, beforeAlert: @escaping @MainActor () -> Void = {}) {
        guard on, !CGPreflightScreenCaptureAccess() else {
            model.updateSettings { $0.screenRecordingEnabled = on }
            return
        }
        _ = CGRequestScreenCaptureAccess()
        alert(String(localized: """
            EchoDiar needs Screen Recording access. Allow it in System Settings, then quit and reopen EchoDiar.
            """), open: PrivacySettings.openScreenRecording, before: beforeAlert)
    }

    /// Also applies the change to the running video at once.
    static func setCamera(_ on: Bool, model: AppModel, beforeAlert: @escaping @MainActor () -> Void = {}) {
        model.updateSettings { $0.screenCamera = on }
        model.screenRecording.setOverlays(model.settings)
        guard on else { return }
        let message = String(localized: """
            EchoDiar needs Camera access to show the camera bubble. Allow it in System Settings.
            """)
        switch CameraBubbleController.authorizationStatus {
        case .notDetermined:
            Task { [model] in
                let granted = await CameraBubbleController.requestAccess()
                // A grant during a running video shows the bubble at once.
                model.screenRecording.setOverlays(model.settings)
                if !granted { alert(message, open: PrivacySettings.openCamera, before: beforeAlert) }
            }
        case .denied, .restricted:
            alert(message, open: PrivacySettings.openCamera, before: beforeAlert)
        default:
            break
        }
    }

    static func setKeyStrokes(_ on: Bool, model: AppModel, beforeAlert: @escaping @MainActor () -> Void = {}) {
        model.updateSettings { $0.screenKeyStrokes = on }
        if on, !KeyStrokeHUDController.hasAccess, KeyStrokeHUDController.requestAccess() != .granted {
            alert(String(localized: """
                EchoDiar needs Input Monitoring access to show key presses. Allow it in System Settings, then quit and \
                reopen EchoDiar.
                """), open: PrivacySettings.openInputMonitoring, before: beforeAlert)
        }
        model.screenRecording.setOverlays(model.settings)
    }

    /// A sheet on the main window, on the next turn, after `before`: "Open System Settings" runs `open`, OK only
    /// closes. A sheet, not `runModal`, so the main actor's other work goes on while it shows.
    private static func alert(_ message: String, open: @escaping @MainActor () -> Void,
                              before: @escaping @MainActor () -> Void) {
        Task { @MainActor in
            before()
            // The main window is the only one whose delegate is the AppDelegate; the controls are in its toolbar.
            guard let delegate = NSApp.delegate as? AppDelegate,
                  let window = NSApp.windows.first(where: { $0.delegate === delegate && $0.isVisible }) else { return }
            let alert = NSAlert()
            alert.messageText = message
            alert.addButton(withTitle: String(localized: "Open System Settings"))
            alert.addButton(withTitle: String(localized: "OK"))
            if await alert.beginSheetModal(for: window) == .alertFirstButtonReturn { open() }
        }
    }
}
