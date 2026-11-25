import AppKit
import CoreGraphics
import EchoScreen
import IOKit.hid
import SwiftUI

/// The key stroke overlay of a screen recording: a listen-only event tap for key-downs and a floating panel at the
/// bottom left of the recorded display, area or window that shows the latest shortcut (`KeyStrokeFormatter`), then
/// fades out. The camera bubble sits at the bottom right.
///
/// The tap exists only between `show(on:within:allKeys:)` and `hide()`, which the recording calls while its video runs
/// unpaused with the option on. The panel is left in screen captures (no `sharingType = .none`; a window recording
/// lets its `windowID` into the picture) so it is in the picture. Key events are drawn and discarded: never logged,
/// stored or sent. DEBUG builds write counts only (`debugLog`), never a key.
@MainActor
final class KeyStrokeHUDController {
    enum State: Equatable {
        case off
        case listening
        /// No Input Monitoring access, or the tap could not be created: the controller shows nothing.
        case unavailable
    }

    /// Gap between the HUD and the bottom and left edges of the recorded display, area or window.
    private static let inset: CGFloat = 48
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
    #if DEBUG
    /// Key-down callbacks received (autorepeats included), those that passed `mayShow`, those that produced text.
    fileprivate var debugCounts = (keyDowns: 0, mayShow: 0, shown: 0)
    /// Logs the tap's state 2 s after it started, then the counts every 5 s while listening.
    private var debugTask: Task<Void, Never>?
    #endif

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
        // Its intrinsic size, so `fittingSize` measures the text (with no sizing options it is zero and the panel is
        // set to 0 x 0, never visible). The panel's frame stays what `place()` sets.
        hosting.sizingOptions = .intrinsicContentSize
        panel.contentView = hosting
    }

    /// The panel's window ID while listening, for a window recording to let into its picture.
    var windowID: CGWindowID? {
        state == .listening && panel.windowNumber > 0 ? CGWindowID(panel.windowNumber) : nil
    }

    // MARK: - Permission

    /// Input Monitoring, as the system has it for EchoDiar.
    enum Access: Equatable {
        case granted
        case denied
        case notDetermined
    }

    /// Whether EchoDiar may listen to key events (Input Monitoring), from IOKit's HID check. Never prompts.
    static var access: Access {
        switch IOHIDCheckAccess(kIOHIDRequestTypeListenEvent) {
        case kIOHIDAccessTypeGranted: .granted
        case kIOHIDAccessTypeDenied: .denied
        default: .notDetermined
        }
    }

    static var hasAccess: Bool { access == .granted }

    /// Asks for Input Monitoring: the system prompt shows while not determined and adds EchoDiar to System Settings'
    /// list; later calls only return the current state. Called only when the user switches the key presses option on
    /// or clicks "Allow…" in Settings, never at launch or when a recording starts. Returns the state read after the
    /// request (the prompt does not wait for the answer). macOS applies a grant once EchoDiar is reopened.
    @discardableResult
    static func requestAccess() -> Access {
        _ = IOHIDRequestAccess(kIOHIDRequestTypeListenEvent)
        return access
    }

    // MARK: - Show and hide

    /// Starts listening and places the HUD at the bottom left of `display`, inside `area` when given (points local to
    /// the display, top-left origin, as `ScreenSource.area` or a window's crop). The panel is ordered in at once,
    /// transparent until a stroke, so a window recording finds it in the shareable content. Calling it again only
    /// updates the place and `allKeys`.
    /// Without access it does nothing and `state` is `.unavailable`; when the display is not connected it hides.
    func show(on display: CGDirectDisplayID, within area: CGRect?, allKeys: Bool) {
        #if DEBUG
        Self.debugLog("""
            show display=\(display) area=\(area == nil ? "no" : "yes") allKeys=\(allKeys) \
            tap=\(tap == nil ? "none" : "running")
            """)
        #endif
        guard let screen = Self.screen(display) else {
            #if DEBUG
            Self.debugLog("show: display \(display) not connected, hiding")
            #endif
            hide()
            return
        }
        self.allKeys = allKeys
        let frame = screen.frame
        target = area.map {
            CGRect(x: frame.minX + $0.minX, y: frame.maxY - $0.maxY, width: $0.width, height: $0.height)
        } ?? screen.visibleFrame
        if tap == nil {
            #if DEBUG
            let raw = IOHIDCheckAccess(kIOHIDRequestTypeListenEvent).rawValue
            Self.debugLog("hasAccess=\(Self.hasAccess) IOHIDCheckAccess=\(raw)")
            Self.debugLog("CGPreflightListenEventAccess=\(CGPreflightListenEventAccess())")
            Self.debugLog("AXIsProcessTrusted=\(AXIsProcessTrusted())")
            #endif
            // Checked first: creating the tap without access could raise the system prompt.
            guard Self.hasAccess, startTap() else {
                state = .unavailable
                return
            }
            #if DEBUG
            debugCounts = (0, 0, 0)
            startDebugLog()
            #endif
        }
        state = .listening
        place()
        if !panel.isVisible { panel.orderFrontRegardless() }
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
        #if DEBUG
        if debugTask != nil { Self.debugLog("hide: tap removed") }
        debugTask?.cancel()
        debugTask = nil
        #endif
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
            #if DEBUG
            Self.debugLog("tapCreate failed")
            #endif
            retained.release()
            return false
        }
        // On the main run loop, so the callback runs on the main thread.
        CFRunLoopAddSource(CFRunLoopGetMain(), source, .commonModes)
        CGEvent.tapEnable(tap: tap, enable: true)
        #if DEBUG
        Self.debugLog("tapCreate ok, enabled=\(CGEvent.tapIsEnabled(tap: tap))")
        #endif
        self.tap = tap
        tapSource = source
        retainedSelf = retained
        return true
    }

    /// The system disabled the tap (timeout or user input): turn it back on while listening.
    fileprivate func tapDisabled(byTimeout: Bool) {
        #if DEBUG
        Self.debugLog("tapDisabled by \(byTimeout ? "timeout" : "user input"), state=\(state)")
        #endif
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
        #if DEBUG
        debugCounts.shown += 1
        #endif
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

    /// Bottom left of `target`, `inset` from its left and bottom edges (less in a small target), kept inside it.
    private func place() {
        let size = hosting.fittingSize
        let x = target.minX + min(Self.inset, max(target.width - size.width, 0) / 2)
        let y = target.minY + min(Self.inset, max(target.height - size.height, 0) / 2)
        panel.setFrame(NSRect(x: x, y: y, width: size.width, height: size.height), display: true)
    }

    private static func screen(_ display: CGDirectDisplayID) -> NSScreen? {
        let key = NSDeviceDescriptionKey("NSScreenNumber")
        return NSScreen.screens.first { ($0.deviceDescription[key] as? NSNumber)?.uint32Value == display }
    }

    // MARK: - DEBUG diagnostics

    #if DEBUG
    /// One "EchoDiar HUD: …" line on standard error (not NSLog or os_log, whose values show as <private>). Never pass
    /// a key code, flag or character.
    nonisolated static func debugLog(_ message: String) {
        FileHandle.standardError.write(Data("EchoDiar HUD: \(message)\n".utf8))
    }

    /// The tap's enabled state 2 s after it started, then the counts and the panel every 5 s, until `hide()`.
    private func startDebugLog() {
        debugTask?.cancel()
        debugTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(2))
            guard !Task.isCancelled, let started = self?.tap else { return }
            Self.debugLog("tap enabled=\(CGEvent.tapIsEnabled(tap: started)) (2 s after enabling)")
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(5))
                guard !Task.isCancelled, let self else { return }
                let counts = debugCounts
                let frame = panel.frame
                Self.debugLog("""
                    listening=\(state == .listening) keyDowns=\(counts.keyDowns) mayShow=\(counts.mayShow) \
                    shown=\(counts.shown) tapEnabled=\(tap.map { CGEvent.tapIsEnabled(tap: $0) } ?? false) \
                    panel=\(Int(frame.width))x\(Int(frame.height)) alpha=\(panel.alphaValue) visible=\(panel.isVisible)
                    """)
            }
        }
    }
    #endif
}

/// The tap's C callback: key-downs go to the controller in `userInfo`; nothing else is kept.
private func keyStrokeTapCallback(
    proxy: CGEventTapProxy, type: CGEventType, event: CGEvent, userInfo: UnsafeMutableRawPointer?
) -> Unmanaged<CGEvent>? {
    guard let userInfo else { return Unmanaged.passUnretained(event) }
    let hud = Unmanaged<KeyStrokeHUDController>.fromOpaque(userInfo).takeUnretainedValue()
    switch type {
    case .tapDisabledByTimeout, .tapDisabledByUserInput:
        MainActor.assumeIsolated { hud.tapDisabled(byTimeout: type == .tapDisabledByTimeout) }
    case .keyDown:
        #if DEBUG
        MainActor.assumeIsolated { hud.debugCounts.keyDowns += 1 }
        #endif
        guard event.getIntegerValueField(.keyboardEventAutorepeat) == 0 else { break }
        let keyCode = UInt16(truncatingIfNeeded: event.getIntegerValueField(.keyboardEventKeycode))
        let flags = event.flags
        let (mayShow, needsCharacters) = MainActor.assumeIsolated { (hud.mayShow(flags), hud.needsCharacters(flags)) }
        guard mayShow else { break }
        #if DEBUG
        MainActor.assumeIsolated { hud.debugCounts.mayShow += 1 }
        #endif
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
