import AppKit
import EchoScreen

/// "Select area…": one transparent borderless window per screen, shown at once. Dragging on a screen draws the
/// rectangle with its size in points; releasing confirms (drags under 64 × 64 points are ignored and the selection
/// goes on), Esc or leaving EchoDiar cancels. The result is an `.area` source local to the display it was drawn on
/// (`ScreenSource.displayLocal`). The windows are kept out of screen captures (`sharingType = .none`; a recording
/// starting meanwhile also excludes them by window ID, `windowIDs`).
@MainActor
final class AreaSelectionOverlay {
    /// The smallest area in points; a smaller drag is ignored.
    static let minimumSize = CGSize(width: 64, height: 64)

    private var windows: [AreaSelectionWindow] = []
    private var observer: (any NSObjectProtocol)?
    private let completion: (ScreenSource?) -> Void

    /// `completion` runs once, with the area or nil when cancelled.
    init(completion: @escaping (ScreenSource?) -> Void) {
        self.completion = completion
    }

    /// The overlays' window IDs while they are shown.
    var windowIDs: [CGWindowID] {
        windows.compactMap { $0.windowNumber > 0 ? CGWindowID($0.windowNumber) : nil }
    }

    func show() {
        guard windows.isEmpty else { return }
        for screen in NSScreen.screens {
            let window = AreaSelectionWindow(
                contentRect: screen.frame, styleMask: [.borderless], backing: .buffered, defer: false)
            window.level = .screenSaver
            window.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
            window.isOpaque = false
            window.backgroundColor = .clear
            window.hasShadow = false
            window.isReleasedWhenClosed = false
            window.sharingType = .none
            window.animationBehavior = .none
            window.setFrame(screen.frame, display: false)
            let view = AreaSelectionView(frame: CGRect(origin: .zero, size: screen.frame.size))
            view.onConfirm = { [weak self, weak window] rect in
                guard let self, let window else { return }
                finish(with: Self.source(rect, in: window, screen: screen))
            }
            view.onCancel = { [weak self] in self?.finish(with: nil) }
            view.onDragStart = { [weak self, weak view] in
                for other in self?.windows ?? [] where other.contentView !== view {
                    (other.contentView as? AreaSelectionView)?.clearSelection()
                }
            }
            window.contentView = view
            windows.append(window)
        }
        NSApp.activate()
        let mouse = NSEvent.mouseLocation
        let key = windows.first { $0.frame.contains(mouse) } ?? windows.first
        for window in windows { window.orderFrontRegardless() }
        key?.makeKeyAndOrderFront(nil)
        if let key { key.makeFirstResponder(key.contentView) }
        NSCursor.crosshair.set()
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

    private func finish(with source: ScreenSource?) {
        guard !windows.isEmpty else { return }
        close()
        completion(source)
    }

    /// `rect` (in the view, which fills `window`) as an area local to `screen`'s display.
    private static func source(_ rect: CGRect, in window: NSWindow, screen: NSScreen) -> ScreenSource? {
        let key = NSDeviceDescriptionKey("NSScreenNumber")
        guard let displayID = (screen.deviceDescription[key] as? NSNumber)?.uint32Value else { return nil }
        let global = window.convertToScreen(rect)
        let local = ScreenSource.displayLocal(area: global, displayFrame: screen.frame, scale: screen.backingScaleFactor)
        return .area(display: displayID, rect: local.rect)
    }
}

/// A thin outline just outside a recorded area, so the user sees what is captured. Its line lies outside the area
/// (`sourceRect`), so it is never in the picture; it ignores the mouse and is kept out of screen captures.
@MainActor
final class AreaOutlineWindow {
    private static let lineWidth: CGFloat = 2
    private let window: NSWindow

    /// Nil when the area's display is not connected.
    init?(display: CGDirectDisplayID, rect: CGRect) {
        let key = NSDeviceDescriptionKey("NSScreenNumber")
        guard let screen = NSScreen.screens.first(where: {
            ($0.deviceDescription[key] as? NSNumber)?.uint32Value == display
        }) else { return nil }
        // Display-local top-left points back to AppKit global coordinates, through this display's own frame.
        let frame = screen.frame
        let area = CGRect(x: frame.minX + rect.minX, y: frame.maxY - rect.maxY, width: rect.width, height: rect.height)
        let outer = area.insetBy(dx: -Self.lineWidth, dy: -Self.lineWidth)
        window = NSWindow(contentRect: outer, styleMask: [.borderless], backing: .buffered, defer: false)
        window.level = .statusBar
        window.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        window.isOpaque = false
        window.backgroundColor = .clear
        window.hasShadow = false
        window.ignoresMouseEvents = true
        window.isReleasedWhenClosed = false
        window.sharingType = .none
        window.contentView = AreaOutlineView(frame: CGRect(origin: .zero, size: outer.size), lineWidth: Self.lineWidth)
        window.orderFrontRegardless()
    }

    /// The outline's window ID, for the capture filter to leave out.
    var windowID: CGWindowID? {
        window.windowNumber > 0 ? CGWindowID(window.windowNumber) : nil
    }

    func close() {
        window.orderOut(nil)
        window.close()
    }
}

// MARK: - Private

/// Borderless, yet takes the keyboard (Esc).
private final class AreaSelectionWindow: NSWindow {
    override var canBecomeKey: Bool { true }
}

/// The dim layer of one screen: the drag's rectangle is clear, outlined, with its size in points beside it. Drawn
/// only on mouse events; nothing animates.
private final class AreaSelectionView: NSView {
    var onConfirm: ((CGRect) -> Void)?
    var onCancel: (() -> Void)?
    var onDragStart: (() -> Void)?
    private var origin: CGPoint?
    private var selection: CGRect?

    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func resetCursorRects() {
        addCursorRect(bounds, cursor: .crosshair)
    }

    func clearSelection() {
        guard origin != nil || selection != nil else { return }
        origin = nil
        selection = nil
        needsDisplay = true
    }

    override func mouseDown(with event: NSEvent) {
        window?.makeKey()
        onDragStart?()
        origin = clamped(convert(event.locationInWindow, from: nil))
        selection = nil
        needsDisplay = true
    }

    override func mouseDragged(with event: NSEvent) {
        guard let origin else { return }
        let point = clamped(convert(event.locationInWindow, from: nil))
        // Whole points: the corners are rounded, so the rect never ends past the screen's edge.
        let minX = min(origin.x, point.x).rounded(), maxX = max(origin.x, point.x).rounded()
        let minY = min(origin.y, point.y).rounded(), maxY = max(origin.y, point.y).rounded()
        selection = CGRect(x: minX, y: minY, width: maxX - minX, height: maxY - minY)
        needsDisplay = true
    }

    override func mouseUp(with event: NSEvent) {
        let rect = selection
        clearSelection()
        guard let rect, rect.width >= AreaSelectionOverlay.minimumSize.width,
              rect.height >= AreaSelectionOverlay.minimumSize.height else { return }
        onConfirm?(rect)
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
        // Not fully clear: a window passes clicks through where it draws nothing.
        NSColor.black.withAlphaComponent(0.2).setFill()
        bounds.fill()
        guard let selection, selection.width > 0, selection.height > 0 else { return }
        NSColor.clear.setFill()
        selection.fill(using: .copy)
        NSColor.white.setStroke()
        let border = NSBezierPath(rect: selection.insetBy(dx: 0.5, dy: 0.5))
        border.lineWidth = 1
        border.stroke()
        drawSizeLabel(for: selection)
    }

    /// "800 × 600" below the rectangle, or inside its bottom edge when there is no room below.
    private func drawSizeLabel(for rect: CGRect) {
        let text = NSAttributedString(
            string: "\(Int(rect.width)) × \(Int(rect.height))",
            attributes: [
                .font: NSFont.monospacedDigitSystemFont(ofSize: 12, weight: .medium),
                .foregroundColor: NSColor.white,
            ])
        let size = text.size()
        let padding = CGSize(width: 6, height: 3)
        let box = CGSize(width: size.width + 2 * padding.width, height: size.height + 2 * padding.height)
        var origin = CGPoint(x: rect.minX, y: rect.minY - box.height - 4)
        if origin.y < bounds.minY { origin.y = rect.minY + 4 }
        origin.x = min(max(origin.x, bounds.minX), bounds.maxX - box.width)
        let frame = CGRect(origin: origin, size: box)
        NSColor.black.withAlphaComponent(0.7).setFill()
        NSBezierPath(roundedRect: frame, xRadius: 4, yRadius: 4).fill()
        text.draw(at: CGPoint(x: frame.minX + padding.width, y: frame.minY + padding.height))
    }

    private func clamped(_ point: CGPoint) -> CGPoint {
        CGPoint(x: min(max(point.x, bounds.minX), bounds.maxX), y: min(max(point.y, bounds.minY), bounds.maxY))
    }
}

/// The outline's line, drawn in the band around the area (the window's inset edge), never over the area itself.
private final class AreaOutlineView: NSView {
    private let lineWidth: CGFloat

    init(frame: CGRect, lineWidth: CGFloat) {
        self.lineWidth = lineWidth
        super.init(frame: frame)
    }

    required init?(coder: NSCoder) { nil }

    override func draw(_ dirtyRect: NSRect) {
        let path = NSBezierPath(rect: bounds.insetBy(dx: lineWidth / 2, dy: lineWidth / 2))
        path.lineWidth = lineWidth
        NSColor.controlAccentColor.setStroke()
        path.stroke()
    }
}
