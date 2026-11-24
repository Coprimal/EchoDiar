import AppKit
import CoreGraphics
import EchoScreen
import SwiftUI

/// The key stroke overlay of a display or area screen recording: a listen-only event tap for key-downs and a
/// bottom-centre floating panel that shows the latest shortcut (`KeyStrokeFormatter`), then fades out.
///
/// The tap exists only between `show(on:within:allKeys:)` and `hide()`, which the recording calls while its video runs
/// unpaused in display or area mode with the option on. The panel is left in screen captures (no `sharingType = .none`)
/// so it is in the picture. Key events are drawn and discarded: never logged, stored or sent.
@MainActor
final class KeyStrokeHUDController {
    enum State: Equatable {
        case off
        case listening
        /// No Input Monitoring access, or the tap could not be created: the controller shows nothing.
        case unavailable
    }

    /// Gap between the HUD and the bottom of the recorded display or area.
    private static let bottomInset: CGFloat = 48
    /// How long a stroke stays fully visible before it fades out.
    private static let holdDuration: Duration = .milliseconds(1500)
    private static let fadeDuration: TimeInterval = 0.3

    private(set) var state: State = .off

    private let panel: NSPanel
    private let hosting: NSHostingView<KeyStrokeHUDView>
    private var tap: CFMachPort?
    private var tapSource: CFRunLoopSource?
    /// The controller retained for the tap's callback while the tap exists; released in `hide()`.
    private var retainedSelf: Unmanaged<KeyStrokeHUDController>?
    private var allKeys = false
    /// The recorded display's area in AppKit global coordinates.
    private var target: CGRect = .zero
    private var fadeTask: Task<Void, Never>?

    init() {
        panel = NSPanel(
            contentRect: NSRect(x: 0, y: 0, width: 1, height: 1),
            styleMask: [.nonactivatingPanel, .borderless], backing: .buffered, defer: false)
        panel.level = .statusBar
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        panel.isFloatingPanel = true
        panel.hidesOnDeactivate = false
        panel.ignoresMouseEvents = true
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.isReleasedWhenClosed = false
        panel.alphaValue = 0
        hosting = NSHostingView(rootView: KeyStrokeHUDView(text: ""))
        hosting.sizingOptions = []
        panel.contentView = hosting
    }

    // MARK: - Permission

    /// Whether EchoDiar may listen to key events (Input Monitoring). Never prompts.
    static var hasAccess: Bool { CGPreflightListenEventAccess() }

    /// Asks for Input Monitoring (the system prompt shows once; later calls only return the current state). Called
    /// only when the key presses option is switched on.
    @discardableResult
    static func requestAccess() -> Bool { CGRequestListenEventAccess() }

    // MARK: - Show and hide

    /// Starts listening and places the HUD at the bottom centre of `display`, inside `area` when given (points local to
    /// the display, top-left origin, as `ScreenSource.area`). Calling it again only updates the place and `allKeys`.
    /// Without access it does nothing and `state` is `.unavailable`; when the display is not connected it hides.
    func show(on display: CGDirectDisplayID, within area: CGRect?, allKeys: Bool) {
        guard let screen = Self.screen(display) else {
            hide()
            return
        }
        self.allKeys = allKeys
        let frame = screen.frame
        target = area.map {
            CGRect(x: frame.minX + $0.minX, y: frame.maxY - $0.maxY, width: $0.width, height: $0.height)
        } ?? screen.visibleFrame
        if tap == nil {
            // Checked first: creating the tap without access could raise the system prompt.
            guard Self.hasAccess, startTap() else {
                state = .unavailable
                return
            }
        }
        state = .listening
        place()
    }

    /// Removes the tap and the panel. Safe to call in any state.
    func hide() {
        if let tap {
            CGEvent.tapEnable(tap: tap, enable: false)
            if let tapSource { CFRunLoopRemoveSource(CFRunLoopGetMain(), tapSource, .commonModes) }
            CFMachPortInvalidate(tap)
        }
        tap = nil
        tapSource = nil
        retainedSelf?.release()
        retainedSelf = nil
        fadeTask?.cancel()
        fadeTask = nil
        panel.orderOut(nil)
        panel.alphaValue = 0
        hosting.rootView = KeyStrokeHUDView(text: "")
        state = .off
    }

    // MARK: - Tap

    private func startTap() -> Bool {
        let retained = Unmanaged.passRetained(self)
        guard let tap = CGEvent.tapCreate(
            tap: .cgSessionEventTap, place: .tailAppendEventTap, options: .listenOnly,
            eventsOfInterest: CGEventMask(1 << CGEventType.keyDown.rawValue),
            callback: keyStrokeTapCallback, userInfo: retained.toOpaque()
        ), let source = CFMachPortCreateRunLoopSource(nil, tap, 0) else {
            retained.release()
            return false
        }
        // On the main run loop, so the callback runs on the main thread.
        CFRunLoopAddSource(CFRunLoopGetMain(), source, .commonModes)
        CGEvent.tapEnable(tap: tap, enable: true)
        self.tap = tap
        tapSource = source
        retainedSelf = retained
        return true
    }

    /// The system disabled the tap (timeout or user input): turn it back on while listening.
    fileprivate func tapDisabled() {
        guard state == .listening, let tap else { return }
        CGEvent.tapEnable(tap: tap, enable: true)
    }

    /// Whether a key-down with `flags` can show at all, checked before its characters are read, so plain typing is
    /// never turned into a string.
    fileprivate func mayShow(_ flags: CGEventFlags) -> Bool {
        state == .listening && (allKeys || !flags.isDisjoint(with: [.maskCommand, .maskControl, .maskAlternate]))
    }

    /// Whether the formatter needs the key's characters: only with `allKeys` or with Command or Control held. With
    /// Option alone it shows special keys only (`KeyStrokeFormatter`), which need no characters, so Option-typed text
    /// ("@" as ⌥L) is never read either.
    fileprivate func needsCharacters(_ flags: CGEventFlags) -> Bool {
        allKeys || !flags.isDisjoint(with: [.maskCommand, .maskControl])
    }

    fileprivate func keyDown(keyCode: UInt16, flags: CGEventFlags, characters: String?) {
        guard state == .listening,
              let text = KeyStrokeFormatter.text(keyCode: keyCode, flags: flags, characters: characters, allKeys: allKeys)
        else { return }
        // Set before `place()`, so its fitting size is the new text's.
        hosting.rootView = KeyStrokeHUDView(text: text)
        place()
        // Zero-duration animation: replaces a fade in progress.
        NSAnimationContext.runAnimationGroup { context in
            context.duration = 0
            panel.animator().alphaValue = 1
        }
        panel.orderFrontRegardless()
        fadeTask?.cancel()
        fadeTask = Task { [weak self] in
            try? await Task.sleep(for: Self.holdDuration)
            guard !Task.isCancelled else { return }
            self?.fadeOut()
            try? await Task.sleep(for: .seconds(Self.fadeDuration))
            guard !Task.isCancelled else { return }
            self?.hosting.rootView = KeyStrokeHUDView(text: "")
        }
    }

    private func fadeOut() {
        NSAnimationContext.runAnimationGroup { context in
            context.duration = Self.fadeDuration
            panel.animator().alphaValue = 0
        }
    }

    // MARK: - Placement

    /// Bottom centre of `target`, kept inside it.
    private func place() {
        let size = hosting.fittingSize
        let x = min(max(target.midX - size.width / 2, target.minX), max(target.maxX - size.width, target.minX))
        let y = target.minY + min(Self.bottomInset, max(target.height - size.height, 0) / 2)
        panel.setFrame(NSRect(x: x, y: y, width: size.width, height: size.height), display: true)
    }

    private static func screen(_ display: CGDirectDisplayID) -> NSScreen? {
        let key = NSDeviceDescriptionKey("NSScreenNumber")
        return NSScreen.screens.first { ($0.deviceDescription[key] as? NSNumber)?.uint32Value == display }
    }
}

/// The tap's C callback: key-downs go to the controller in `userInfo`; nothing else is kept.
private func keyStrokeTapCallback(
    proxy: CGEventTapProxy, type: CGEventType, event: CGEvent, userInfo: UnsafeMutableRawPointer?
) -> Unmanaged<CGEvent>? {
    guard let userInfo else { return Unmanaged.passUnretained(event) }
    let hud = Unmanaged<KeyStrokeHUDController>.fromOpaque(userInfo).takeUnretainedValue()
    switch type {
    case .tapDisabledByTimeout, .tapDisabledByUserInput:
        MainActor.assumeIsolated { hud.tapDisabled() }
    case .keyDown where event.getIntegerValueField(.keyboardEventAutorepeat) == 0:
        let keyCode = UInt16(truncatingIfNeeded: event.getIntegerValueField(.keyboardEventKeycode))
        let flags = event.flags
        let (mayShow, needsCharacters) = MainActor.assumeIsolated { (hud.mayShow(flags), hud.needsCharacters(flags)) }
        guard mayShow else { break }
        // The unshifted base character ("1" for ⇧⌘1, "k" for ⇧⌘K), so the formatter adds ⇧ before the real key.
        let characters = needsCharacters ? NSEvent(cgEvent: event)?.characters(byApplyingModifiers: []) : nil
        MainActor.assumeIsolated { hud.keyDown(keyCode: keyCode, flags: flags, characters: characters) }
    default:
        break
    }
    return Unmanaged.passUnretained(event)
}

private struct KeyStrokeHUDView: View {
    let text: String

    var body: some View {
        Text(verbatim: text)
            .font(.system(size: 30, weight: .semibold, design: .rounded))
            .foregroundStyle(.white)
            .lineLimit(1)
            .fixedSize()
            .padding(.horizontal, 22)
            .padding(.vertical, 12)
            .background(Color.black.opacity(0.75), in: RoundedRectangle(cornerRadius: 14, style: .continuous))
    }
}
