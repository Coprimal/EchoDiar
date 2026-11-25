import AppKit
import EchoScreen
import SwiftUI

/// "Entire screen" (with several displays) and "Window" in the chooser: picks on the screen itself, like macOS's own
/// screenshot tool. One transparent borderless window per screen, shown at once; the pointer is a camera. The display
/// or the window under the pointer is highlighted (a light accent-colour fill, a 2 pt border and a small
/// label); a click picks it, Esc or leaving EchoDiar cancels. Windows: the topmost normal window under the pointer (on
/// screen, layer 0, at least 40 × 40 points), read from the window server front to back; EchoDiar's own windows are
/// never picked and hide what lies behind them. The overlays are kept out of screen captures (`sharingType = .none`).
/// Nothing animates: the highlight only moves with the pointer.
@MainActor
final class ScreenPickerOverlay {
    enum Mode {
        case display, window
    }

    /// What the pointer is over: its source, its frame in AppKit global coordinates and its label.
    struct Target: Equatable {
        let source: ScreenSource
        let frame: CGRect
        let title: String
        let subtitle: String?
    }

    /// The smallest window that can be picked, in points.
    private static let minimumWindowSize: CGFloat = 40
    /// The window list is read at most this often while the pointer moves.
    private static let refreshInterval = Duration.milliseconds(50)

    private let mode: Mode
    private let completion: (ScreenSource?) -> Void
    private var windows: [ScreenPickerWindow] = []
    private var observer: (any NSObjectProtocol)?
    private var target: Target?
    /// The on-screen windows, front to back, from the last read.
    private var windowList: [WindowInfo] = []
    private var lastRead: ContinuousClock.Instant?

    /// `completion` runs once, with the picked source or nil when cancelled.
    init(mode: Mode, completion: @escaping (ScreenSource?) -> Void) {
        self.mode = mode
        self.completion = completion
    }

    /// The overlays' window IDs while they are shown.
    var windowIDs: [CGWindowID] {
        windows.compactMap { $0.windowNumber > 0 ? CGWindowID($0.windowNumber) : nil }
    }

    func show() {
        guard windows.isEmpty else { return }
        for screen in NSScreen.screens {
            let window = ScreenPickerWindow(
                contentRect: screen.frame, styleMask: [.borderless], backing: .buffered, defer: false)
            window.level = .screenSaver
            window.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
            window.isOpaque = false
            window.backgroundColor = .clear
            window.hasShadow = false
            window.isReleasedWhenClosed = false
            window.sharingType = .none
            window.animationBehavior = .none
            window.acceptsMouseMovedEvents = true
            // Set explicitly: the whole window takes the mouse, even where its light dim is nearly clear.
            window.ignoresMouseEvents = false
            window.setFrame(screen.frame, display: false)
            let view = ScreenPickerView(frame: CGRect(origin: .zero, size: screen.frame.size), mode: mode)
            view.onMove = { [weak self] in self?.update(at: NSEvent.mouseLocation, fresh: false) }
            view.onClick = { [weak self] in self?.pick(at: NSEvent.mouseLocation) }
            view.onCancel = { [weak self] in self?.finish(with: nil) }
            window.contentView = view
            windows.append(window)
        }
        NSApp.activate()
        let mouse = NSEvent.mouseLocation
        let key = windows.first { NSMouseInRect(mouse, $0.frame, false) } ?? windows.first
        for window in windows { window.orderFrontRegardless() }
        key?.makeKeyAndOrderFront(nil)
        if let key { key.makeFirstResponder(key.contentView) }
        CameraCursor.shared.set()
        update(at: mouse, fresh: true)
        // Leaving EchoDiar (⌘⇥, a click in another app's window) cancels: no screen-wide overlay stays behind.
        observer = NotificationCenter.default.addObserver(
            forName: NSApplication.didResignActiveNotification, object: nil, queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.finish(with: nil) }
        }
    }

    /// Closes the overlays without a result (and without calling `completion`).
    func close() {
        if let observer { NotificationCenter.default.removeObserver(observer) }
        observer = nil
        for window in windows {
            window.orderOut(nil)
            window.close()
        }
        windows = []
        NSCursor.arrow.set()
    }

    // MARK: - Private

    private func finish(with source: ScreenSource?) {
        guard !windows.isEmpty else { return }
        close()
        completion(source)
    }

    /// A click picks what is under the pointer, from a fresh read of the windows; a click on nothing does nothing.
    private func pick(at point: CGPoint) {
        update(at: point, fresh: true)
        guard let target else { return }
        finish(with: target.source)
    }

    /// Highlights what is under `point` (AppKit global); only the screens whose highlight changes are redrawn.
    private func update(at point: CGPoint, fresh: Bool) {
        let next: Target? = switch mode {
        case .display: displayTarget(at: point) ?? target
        case .window: windowTarget(at: point, fresh: fresh)
        }
        guard next != target else { return }
        let old = target?.frame
        target = next
        for window in windows {
            guard let view = window.contentView as? ScreenPickerView else { continue }
            let touched = [old, next?.frame].contains { $0?.intersects(window.frame) == true }
            if mode == .display || touched { view.setHighlight(next, windowFrame: window.frame) }
        }
    }

    /// The display whose frame holds `point` (its max edges too, so the last row and column count).
    private func displayTarget(at point: CGPoint) -> Target? {
        guard let screen = NSScreen.screens.first(where: { NSMouseInRect(point, $0.frame, false) }),
              let id = Self.displayID(screen) else { return nil }
        return Target(source: .display(id), frame: screen.frame, title: screen.localizedName,
                      subtitle: String(localized: "Click to record this display"))
    }

    /// The topmost normal window under `point`; nil over the desktop or over EchoDiar's own windows.
    private func windowTarget(at point: CGPoint, fresh: Bool) -> Target? {
        let now = ContinuousClock.now
        if fresh || lastRead.map({ now - $0 >= Self.refreshInterval }) ?? true {
            windowList = Self.readWindows()
            lastRead = now
        }
        guard let primary = NSScreen.screens.first else { return nil }
        // AppKit global (bottom-left origin) to CG global (top-left origin on the primary screen, the one at the
        // origin, first in `NSScreen.screens`; never `NSScreen.main`, the screen with the key window).
        let height = primary.frame.height
        let cgPoint = CGPoint(x: point.x, y: height - point.y)
        guard let hit = windowList.first(where: { $0.bounds.contains(cgPoint) }), !hit.isOwn else { return nil }
        let frame = CGRect(x: hit.bounds.minX, y: height - hit.bounds.maxY,
                           width: hit.bounds.width, height: hit.bounds.height)
        let title = hit.title.isEmpty ? hit.app : "\(hit.app) — \(hit.title)"
        return Target(source: .window(hit.id), frame: frame, title: title, subtitle: nil)
    }

    /// An on-screen window, in CG global points (top-left origin).
    private struct WindowInfo {
        let id: CGWindowID
        let bounds: CGRect
        let app: String
        let title: String
        /// EchoDiar's own: never picked, yet it hides the windows behind it.
        let isOwn: Bool
    }

    /// The pickable windows and EchoDiar's own, front to back: on screen, layer 0, visible, not tiny.
    private static func readWindows() -> [WindowInfo] {
        guard let list = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID)
            as? [[String: Any]] else { return [] }
        let ownPID = ProcessInfo.processInfo.processIdentifier
        return list.compactMap { info in
            guard info[kCGWindowLayer as String] as? Int == 0,
                  let number = info[kCGWindowNumber as String] as? NSNumber,
                  let boundsInfo = info[kCGWindowBounds as String] as? NSDictionary,
                  let bounds = CGRect(dictionaryRepresentation: boundsInfo),
                  bounds.width >= minimumWindowSize, bounds.height >= minimumWindowSize,
                  (info[kCGWindowAlpha as String] as? Double ?? 1) > 0 else { return nil }
            let pid = (info[kCGWindowOwnerPID as String] as? NSNumber)?.int32Value
            return WindowInfo(
                id: number.uint32Value, bounds: bounds,
                app: info[kCGWindowOwnerName as String] as? String ?? "",
                title: info[kCGWindowName as String] as? String ?? "",
                isOwn: pid == ownPID)
        }
    }

    private static func displayID(_ screen: NSScreen) -> CGDirectDisplayID? {
        (screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value
    }
}

// MARK: - Window and view

/// Borderless, yet takes the keyboard (Esc).
private final class ScreenPickerWindow: NSWindow {
    override var canBecomeKey: Bool { true }
}

/// One screen's layer: a light dim, the highlight over the part of the target on this screen, its label centred in
/// that part. Every pixel has some alpha, so clicks never pass through to the window being picked.
private final class ScreenPickerView: NSView {
    var onMove: (() -> Void)?
    var onClick: (() -> Void)?
    var onCancel: (() -> Void)?
    private let mode: ScreenPickerOverlay.Mode
    /// The target's frame in this view, and its label; nil when the pointer is over nothing here.
    private var highlight: (rect: CGRect, title: String, subtitle: String?)?

    init(frame: CGRect, mode: ScreenPickerOverlay.Mode) {
        self.mode = mode
        super.init(frame: frame)
    }

    required init?(coder: NSCoder) { nil }

    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    /// `target` (AppKit global) in this view, which fills the window at `windowFrame`.
    func setHighlight(_ target: ScreenPickerOverlay.Target?, windowFrame: CGRect) {
        let next = target.flatMap { target -> (CGRect, String, String?)? in
            let rect = target.frame.offsetBy(dx: -windowFrame.minX, dy: -windowFrame.minY).intersection(bounds)
            return rect.isNull || rect.isEmpty ? nil : (rect, target.title, target.subtitle)
        }
        highlight = next
        needsDisplay = true
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        for area in trackingAreas { removeTrackingArea(area) }
        // Always active: only one overlay is key, yet every screen follows the pointer. Cursor updates are not
        // supported with `.activeAlways`, so they have their own area.
        addTrackingArea(NSTrackingArea(
            rect: .zero, options: [.mouseMoved, .mouseEnteredAndExited, .activeAlways, .inVisibleRect], owner: self))
        addTrackingArea(NSTrackingArea(
            rect: .zero, options: [.cursorUpdate, .activeInActiveApp, .inVisibleRect], owner: self))
    }

    override func resetCursorRects() {
        addCursorRect(bounds, cursor: CameraCursor.shared)
    }

    override func cursorUpdate(with event: NSEvent) {
        CameraCursor.shared.set()
    }

    override func mouseEntered(with event: NSEvent) {
        CameraCursor.shared.set()
        onMove?()
    }

    override func mouseMoved(with event: NSEvent) {
        CameraCursor.shared.set()
        onMove?()
    }

    override func mouseDown(with event: NSEvent) {
        window?.makeKey()
    }

    override func mouseUp(with event: NSEvent) {
        onClick?()
    }

    override func keyDown(with event: NSEvent) {
        if event.keyCode == 53 {  // kVK_Escape
            onCancel?()
        } else {
            super.keyDown(with: event)
        }
    }

    override func cancelOperation(_ sender: Any?) {
        onCancel?()
    }

    override func draw(_ dirtyRect: NSRect) {
        let accent = NSColor.controlAccentColor
        guard let highlight else {
            // Not fully clear: a window passes clicks through where it draws nothing.
            NSColor.black.withAlphaComponent(mode == .display ? 0.2 : 0.06).setFill()
            bounds.fill()
            return
        }
        NSColor.black.withAlphaComponent(0.06).setFill()
        bounds.fill()
        NSColor.clear.setFill()
        highlight.rect.fill(using: .copy)
        accent.withAlphaComponent(0.15).setFill()
        highlight.rect.fill()
        let border = NSBezierPath(rect: highlight.rect.insetBy(dx: 1, dy: 1))
        border.lineWidth = 2
        accent.setStroke()
        border.stroke()
        drawLabel(highlight.title, highlight.subtitle, in: highlight.rect)
    }

    /// The title (and the subtitle below it) on a dark rounded box, centred in `rect`.
    private func drawLabel(_ title: String, _ subtitle: String?, in rect: CGRect) {
        let paragraph = NSMutableParagraphStyle()
        paragraph.alignment = .center
        paragraph.lineBreakMode = .byTruncatingMiddle
        let text = NSMutableAttributedString(string: title, attributes: [
            .font: NSFont.systemFont(ofSize: 13, weight: .semibold),
            .foregroundColor: NSColor.white,
            .paragraphStyle: paragraph,
        ])
        if let subtitle {
            text.append(NSAttributedString(string: "\n" + subtitle, attributes: [
                .font: NSFont.systemFont(ofSize: 11),
                .foregroundColor: NSColor.white.withAlphaComponent(0.8),
                .paragraphStyle: paragraph,
            ]))
        }
        let padding = CGSize(width: 10, height: 6)
        let maxWidth = max(min(rect.width - 2 * padding.width - 16, 420), 40)
        let size = text.boundingRect(
            with: CGSize(width: maxWidth, height: .greatestFiniteMagnitude),
            options: [.usesLineFragmentOrigin, .usesFontLeading]).size
        let box = CGRect(
            x: rect.midX - size.width / 2 - padding.width, y: rect.midY - size.height / 2 - padding.height,
            width: ceil(size.width) + 2 * padding.width, height: ceil(size.height) + 2 * padding.height)
        NSColor.black.withAlphaComponent(0.7).setFill()
        NSBezierPath(roundedRect: box, xRadius: 6, yRadius: 6).fill()
        text.draw(with: box.insetBy(dx: padding.width, dy: padding.height),
                  options: [.usesLineFragmentOrigin, .usesFontLeading])
    }
}

/// The picker's pointer: a white `camera.fill` with a dark outline, so it shows on any background; the hotspot is its
/// centre.
@MainActor
private enum CameraCursor {
    static let shared: NSCursor = {
        let size = CGSize(width: 32, height: 32)
        let pointSize: CGFloat = 20
        let base = NSImage(systemSymbolName: "camera.fill", accessibilityDescription: nil)
        let sizing = NSImage.SymbolConfiguration(pointSize: pointSize, weight: .regular)
        let white = base?.withSymbolConfiguration(sizing.applying(.init(paletteColors: [.white])))
        let dark = base?.withSymbolConfiguration(sizing.applying(.init(paletteColors: [.black.withAlphaComponent(0.85)])))
        let image = NSImage(size: size, flipped: false) { bounds in
            guard let white, let dark else { return false }
            let symbol = CGRect(
                x: bounds.midX - white.size.width / 2, y: bounds.midY - white.size.height / 2,
                width: white.size.width, height: white.size.height)
            // The outline: the dark symbol a little off in every direction, then the white one on top.
            for dx in [-1.5, 0, 1.5] {
                for dy in [-1.5, 0, 1.5] where dx != 0 || dy != 0 {
                    dark.draw(in: symbol.offsetBy(dx: dx, dy: dy))
                }
            }
            white.draw(in: symbol)
            return true
        }
        return NSCursor(image: image, hotSpot: CGPoint(x: size.width / 2, y: size.height / 2))
    }()
}
