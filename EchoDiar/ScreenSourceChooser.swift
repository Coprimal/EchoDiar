import AppKit
import EchoCore
import EchoScreen
import SwiftUI

/// Opens the screen source chooser in a popover on this control (the toolbar's Record for `.newRecording`, its
/// camera button for `.duringRecording`) when the model asks for it with `request`, once the main window shows; the
/// request is cleared as it is taken.
struct ScreenChooserHost: ViewModifier {
    let model: AppModel
    let request: ScreenRecordingController.ChooserRequest
    @State private var shows = false

    func body(content: Content) -> some View {
        content
            .popover(isPresented: $shows, arrowEdge: .bottom) {
                ScreenSourceChooser(model: model, request: request)
            }
            .onChange(of: pending, initial: true) { _, pending in
                guard pending else { return }
                model.screenRecording.chooserRequest = nil
                shows = true
            }
    }

    private var pending: Bool { model.screenRecording.chooserRequest == request && model.mainWindowVisible }
}

/// What to record: "Entire screen", "Window" or "Portion of the screen". Every recording with the screen is picked
/// here, never started from a remembered source. Entire screen starts at once with one display; with more, and for
/// Window, the chooser closes and the picking overlays take the screen (`ScreenPickerOverlay`: hover highlights the
/// display or window under the pointer, a click picks it); Portion closes the chooser and shows the selection overlays.
/// Esc in an overlay starts nothing. A pick starts the recording (or the running recording's video) at once; Esc or a
/// click outside the chooser cancels it. Without the screen permission a note points to System Settings instead (the
/// system prompt only comes from switching "Record the screen too" on, or from "Allow…" in Settings).
struct ScreenSourceChooser: View {
    let model: AppModel
    let request: ScreenRecordingController.ChooserRequest
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Record the screen").font(.headline)
            if CGPreflightScreenCaptureAccess() {
                kinds
            } else {
                permissionNote
            }
        }
        .padding(12)
        .frame(width: 300)
    }

    private var kinds: some View {
        VStack(alignment: .leading, spacing: 6) {
            KindRow(title: String(localized: "Entire screen"), systemImage: "display", action: chooseEntireScreen)
            KindRow(title: String(localized: "Window"), systemImage: "macwindow") { pickOnScreen(.window) }
            KindRow(title: String(localized: "Portion of the screen"), systemImage: "rectangle.dashed",
                    action: selectArea)
            Text(request == .duringRecording
                 ? "Records it in this recording from now on."
                 : "The recording starts as soon as you pick.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    /// No screen permission (it can be taken back after "Record the screen too" was switched on): points to System
    /// Settings, never prompts.
    private var permissionNote: some View {
        VStack(alignment: .leading, spacing: 8) {
            Label("EchoDiar needs Screen Recording access to record the screen.", systemImage: "lock")
                .font(.callout)
                .fixedSize(horizontal: false, vertical: true)
            Button("Open System Settings") { PrivacySettings.openScreenRecording() }
                .modifier(CapsuleButton())
            Text("Allow it in System Settings, then quit and reopen EchoDiar.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
        .padding(10)
        .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(Palette.raisedSurface))
    }

    // MARK: - Actions

    /// One display: starts at once; more: picks one on the screen.
    private func chooseEntireScreen() {
        let screens = NSScreen.screens
        if screens.count == 1, let id = Self.displayID(screens[0]) {
            pick(.display(id))
        } else {
            pickOnScreen(.display)
        }
    }

    /// Closes the chooser, then starts the recording (or the running recording's video) with `source`.
    private func pick(_ source: ScreenSource) {
        dismiss()
        model.startPickedScreen(source, for: request)
    }

    /// Closes the chooser, then the picking overlays take the screen; a clicked display or window starts like a pick.
    /// The chooser is gone by then, so the hand-over touches only the model.
    private func pickOnScreen(_ mode: ScreenPickerOverlay.Mode) {
        dismiss()
        let model = model, request = request
        model.screenRecording.pickSource(mode) { source in model.startPickedScreen(source, for: request) }
    }

    /// Closes the chooser, then the overlays take the screen; a confirmed area starts like a pick. The chooser is gone
    /// by then, so the hand-over touches only the model.
    private func selectArea() {
        dismiss()
        let model = model, request = request
        model.screenRecording.selectArea { source in model.startPickedScreen(source, for: request) }
    }

    private static func displayID(_ screen: NSScreen) -> CGDirectDisplayID? {
        (screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value
    }
}

/// One of the chooser's three choices: a big icon and its name.
private struct KindRow: View {
    let title: String
    let systemImage: String
    let action: () -> Void
    @State private var hovered = false

    var body: some View {
        Button(action: action) {
            HStack(spacing: 10) {
                Image(systemName: systemImage)
                    .font(.title3)
                    .foregroundStyle(.secondary)
                    .frame(width: 28)
                Text(title)
                    .font(.body.weight(.medium))
                    .lineLimit(1)
                Spacer(minLength: 6)
            }
            .padding(.horizontal, 10)
            .frame(height: 44)
            .background {
                RoundedRectangle(cornerRadius: 8, style: .continuous)
                    .fill(Palette.raisedSurface)
                    .overlay {
                        RoundedRectangle(cornerRadius: 8, style: .continuous).fill(hovered ? Palette.hoverFill : .clear)
                    }
                    .overlay {
                        RoundedRectangle(cornerRadius: 8, style: .continuous).strokeBorder(Palette.surfaceBorder)
                    }
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { hovered = $0 }
    }
}
