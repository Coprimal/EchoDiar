import AppKit
import EchoScreen
import SwiftUI

/// A thin, calm outline around what a screen video records, in the system accent colour: along the inside edges of a
/// display, just outside an area, or just outside a window, which it follows. Its line is never in the picture: a
/// display's outline window is left out of the capture filter by its ID (`windowID`), an area's lies outside the area,
/// and a window capture holds only that window and EchoDiar's camera bubble and key stroke HUD (never the outline). It
/// ignores the mouse and is kept out of other screen captures (`sharingType = .none`). One window from the video's
/// start to its end, only hidden and shown again, so its ID stays the one the filter leaves out. Nothing animates.
@MainActor
final class RecordingOutline {
    private static let lineWidth: CGFloat = 2
    /// How often a window's outline reads the window's place.
    private static let followInterval = Duration.milliseconds(250)
    private let window: NSWindow
    /// The recorded window, followed while the outline shows; nil for a display or an area.
    private let followedWindow: CGWindowID?
    private var following: Task<Void, Never>?
    private var visible = false

    /// Hidden until `setVisible(true)`. Nil when the display (a display's, an area's) is not connected.
    init?(source: ScreenSource) {
        let frame: CGRect
        switch source {
        case .display(let id):
            guard let screen = Self.screen(id) else { return nil }
            frame = screen.frame
            followedWindow = nil
        case .area(let id, let rect):
            guard let screen = Self.screen(id) else { return nil }
            // Display-local top-left points back to AppKit global coordinates, through this display's own frame.
            let display = screen.frame
            let area = CGRect(
                x: display.minX + rect.minX, y: display.maxY - rect.maxY, width: rect.width, height: rect.height)
            frame = area.insetBy(dx: -Self.lineWidth, dy: -Self.lineWidth)
            followedWindow = nil
        case .window(let id):
            // Placed on the first read once shown.
            frame = CGRect(x: 0, y: 0, width: 2 * Self.lineWidth + 1, height: 2 * Self.lineWidth + 1)
            followedWindow = id
        }
        window = NSWindow(contentRect: frame, styleMask: [.borderless], backing: .buffered, defer: false)
        // A window's outline sits just above the recorded window (`follow`), so windows in front of it cover both.
        window.level = followedWindow == nil ? .statusBar : .normal
        window.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        window.isOpaque = false
        window.backgroundColor = .clear
        window.hasShadow = false
        window.ignoresMouseEvents = true
        window.isReleasedWhenClosed = false
        window.sharingType = .none
        window.animationBehavior = .none
        let view = OutlineView(frame: CGRect(origin: .zero, size: frame.size), lineWidth: Self.lineWidth)
        view.autoresizingMask = [.width, .height]
        window.contentView = view
    }

    /// The outline's window ID, for the capture filter to leave out.
    var windowID: CGWindowID? {
        window.windowNumber > 0 ? CGWindowID(window.windowNumber) : nil
    }

    /// Shows the outline (a window's follows it from now on) or hides it.
    func setVisible(_ visible: Bool) {
        guard visible != self.visible else { return }
        self.visible = visible
        following?.cancel()
        following = nil
        guard visible else {
            window.orderOut(nil)
            return
        }
        guard let id = followedWindow else {
            window.orderFrontRegardless()
            return
        }
        following = Task { [weak self] in
            while !Task.isCancelled {
                guard let self else { return }
                follow(id)
                try? await Task.sleep(for: Self.followInterval)
            }
        }
    }

    func close() {
        following?.cancel()
        following = nil
        visible = false
        window.orderOut(nil)
        window.close()
    }

    // MARK: - Private

    /// Moves the outline to the window's place, just above it; hides it while the window is minimised, on another
    /// Space or gone.
    private func follow(_ id: CGWindowID) {
        guard let frame = Self.outlineFrame(ofWindow: id) else {
            if window.isVisible { window.orderOut(nil) }
            return
        }
        if window.frame != frame { window.setFrame(frame, display: true) }
        window.order(.above, relativeTo: Int(id))
        if !window.isVisible { window.orderFrontRegardless() }
    }

    /// The window's frame outset by the line, in AppKit global coordinates; nil when it is not on screen.
    private static func outlineFrame(ofWindow id: CGWindowID) -> CGRect? {
        guard let info = (CGWindowListCopyWindowInfo([.optionIncludingWindow], id) as? [[String: Any]])?.first,
              info[kCGWindowIsOnscreen as String] as? Bool == true,
              let bounds = info[kCGWindowBounds as String] as? NSDictionary,
              let rect = CGRect(dictionaryRepresentation: bounds), rect.width > 0, rect.height > 0,
              let primary = NSScreen.screens.first else { return nil }
        // CG global points have a top-left origin on the primary screen (the one at the origin, first in
        // `NSScreen.screens`; never `NSScreen.main`, the screen with the key window): flipped on its height.
        let flipped = CGRect(
            x: rect.minX, y: primary.frame.height - rect.maxY, width: rect.width, height: rect.height)
        return flipped.insetBy(dx: -lineWidth, dy: -lineWidth)
    }

    private static func screen(_ display: CGDirectDisplayID) -> NSScreen? {
        let key = NSDeviceDescriptionKey("NSScreenNumber")
        return NSScreen.screens.first { ($0.deviceDescription[key] as? NSNumber)?.uint32Value == display }
    }
}

/// The outline's line along the window's inside edge: around an area or a window, the band outside it; on a display,
/// its edges.
private final class OutlineView: NSView {
    private let lineWidth: CGFloat

    init(frame: CGRect, lineWidth: CGFloat) {
        self.lineWidth = lineWidth
        super.init(frame: frame)
    }

    required init?(coder: NSCoder) { nil }

    override func draw(_ dirtyRect: NSRect) {
        let path = NSBezierPath(rect: bounds.insetBy(dx: lineWidth / 2, dy: lineWidth / 2))
        path.lineWidth = lineWidth
        NSColor.controlAccentColor.withAlphaComponent(0.7).setStroke()
        path.stroke()
    }
}
