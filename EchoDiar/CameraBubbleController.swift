import AVFoundation
import AppKit
import CoreGraphics

/// The camera bubble of a display or area screen recording: a round floating panel with a mirrored live preview of
/// the camera, draggable by its body inside the recorded display or area.
///
/// The recording calls `show(on:within:)` while its video runs unpaused in display or area mode with the option on,
/// and `hide()` otherwise. The panel is left in screen captures (no `sharingType = .none`), so the picture ends up in
/// the screen video only because the bubble is on screen: the session has an input and the preview layer, no output,
/// and this controller stores no frame.
@MainActor
final class CameraBubbleController {
    enum State: Equatable {
        case off
        /// The bubble shows its placeholder while the session starts.
        case starting
        case showing
        /// No camera access, or no camera could be started: the controller shows nothing.
        case unavailable
    }

    private static let diameter: CGFloat = 180
    /// Gap between the bubble and the bottom-right corner of the recorded display or area.
    private static let margin: CGFloat = 24

    private(set) var state: State = .off

    private let panel: NSPanel
    private let bubble: CameraBubbleView
    /// Made on the first authorized `show`, so nothing touches the camera before access is granted.
    private var camera: CameraSession?
    private var previewLayer: AVCaptureVideoPreviewLayer?
    /// The recorded display's area in AppKit global coordinates.
    private var target: CGRect = .zero
    /// Bumped by every start and `hide()`, so a start that finishes after a `hide()` is ignored.
    private var generation = 0

    init() {
        panel = NSPanel(
            contentRect: NSRect(x: 0, y: 0, width: 1, height: 1),
            styleMask: [.nonactivatingPanel, .borderless], backing: .buffered, defer: false)
        panel.level = .statusBar
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        panel.isFloatingPanel = true
        panel.becomesKeyOnlyIfNeeded = true
        panel.hidesOnDeactivate = false
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.isReleasedWhenClosed = false
        bubble = CameraBubbleView(frame: NSRect(x: 0, y: 0, width: 1, height: 1))
        panel.contentView = bubble
        bubble.onDrag = { [weak self] origin in self?.move(to: origin) }
    }

    // MARK: - Permission

    /// Camera access for EchoDiar. Never prompts.
    static var authorizationStatus: AVAuthorizationStatus { AVCaptureDevice.authorizationStatus(for: .video) }

    /// Asks for camera access (the system prompt shows once; later calls only return the current state). Called only
    /// when the camera bubble option is switched on.
    static func requestAccess() async -> Bool { await AVCaptureDevice.requestAccess(for: .video) }

    // MARK: - Show and hide

    /// Shows the bubble at the bottom right of `display`, inside `area` when given (points local to the display,
    /// top-left origin, as `ScreenSource.area`): the placeholder at once, the camera once its session has started off
    /// the main thread. Calling it again only keeps the bubble inside the new place (a dragged bubble stays where it
    /// is while the place is unchanged). Without camera access it does nothing and `state` is `.unavailable`; when the
    /// display is not connected it hides.
    func show(on display: CGDirectDisplayID, within area: CGRect?) {
        guard let screen = Self.screen(display) else {
            hide()
            return
        }
        let frame = screen.frame
        let newTarget = area.map {
            CGRect(x: frame.minX + $0.minX, y: frame.maxY - $0.maxY, width: $0.width, height: $0.height)
        } ?? screen.visibleFrame
        let targetChanged = newTarget != target
        target = newTarget
        switch state {
        case .starting, .showing:
            place(toCorner: targetChanged)
        case .off, .unavailable:
            // Checked first: `show` must never raise the camera prompt.
            guard Self.authorizationStatus == .authorized else {
                state = .unavailable
                return
            }
            state = .starting
            place(toCorner: targetChanged)
            panel.orderFrontRegardless()
            start()
        }
    }

    /// Hides the bubble, stops the session and releases the camera. Safe to call in any state.
    func hide() {
        generation += 1
        camera?.stop()
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        previewLayer?.isHidden = true
        CATransaction.commit()
        panel.orderOut(nil)
        state = .off
    }

    // MARK: - Session

    private func start() {
        generation += 1
        let current = generation
        let camera = camera ?? makeCamera()
        camera.start { [weak self] running in self?.started(running, generation: current) }
    }

    private func makeCamera() -> CameraSession {
        let camera = CameraSession()
        let layer = AVCaptureVideoPreviewLayer(session: camera.session)
        layer.videoGravity = .resizeAspectFill
        layer.isHidden = true
        layer.frame = bubble.bounds
        layer.autoresizingMask = [.layerWidthSizable, .layerHeightSizable]
        bubble.layer?.addSublayer(layer)
        self.camera = camera
        previewLayer = layer
        return camera
    }

    private func started(_ running: Bool, generation: Int) {
        guard generation == self.generation, state == .starting else { return }
        guard running else {
            state = .unavailable
            panel.orderOut(nil)
            return
        }
        // Mirrored like a mirror. Automatic mirroring is turned off first: setting `isVideoMirrored` while it is on
        // raises an exception.
        if let connection = previewLayer?.connection, connection.isVideoMirroringSupported {
            connection.automaticallyAdjustsVideoMirroring = false
            connection.isVideoMirrored = true
        }
        previewLayer?.isHidden = false
        state = .showing
    }

    // MARK: - Placement

    /// Sizes the bubble to fit `target` and keeps it inside; at the bottom-right corner when `toCorner` or its size
    /// changed.
    private func place(toCorner: Bool) {
        let size = max(min(Self.diameter, target.width, target.height), 1)
        let origin: CGPoint
        if toCorner || panel.frame.width != size {
            let inset = min(Self.margin, (target.width - size) / 2, (target.height - size) / 2)
            origin = CGPoint(x: target.maxX - size - inset, y: target.minY + inset)
        } else {
            origin = panel.frame.origin
        }
        panel.setFrame(NSRect(origin: clamped(origin, size: size), size: NSSize(width: size, height: size)),
                       display: true)
        bubble.layer?.cornerRadius = size / 2
    }

    private func move(to origin: CGPoint) {
        panel.setFrameOrigin(clamped(origin, size: panel.frame.width))
    }

    private func clamped(_ origin: CGPoint, size: CGFloat) -> CGPoint {
        CGPoint(x: min(max(origin.x, target.minX), max(target.maxX - size, target.minX)),
                y: min(max(origin.y, target.minY), max(target.maxY - size, target.minY)))
    }

    private static func screen(_ display: CGDirectDisplayID) -> NSScreen? {
        let key = NSDeviceDescriptionKey("NSScreenNumber")
        return NSScreen.screens.first { ($0.deviceDescription[key] as? NSNumber)?.uint32Value == display }
    }
}

/// The capture session and the serial queue it is configured, started and stopped on. It gets only a camera input:
/// its frames go to the preview layer and nowhere else.
private final class CameraSession: @unchecked Sendable {
    /// Read on the main actor only to attach the preview layer; changed only on `queue`.
    let session = AVCaptureSession()
    private let queue = DispatchQueue(label: "dinh.thi.EchoDiar.CameraBubble")

    /// Adds the camera and starts the session, then calls `done` on the main actor with whether it runs.
    func start(_ done: @escaping @MainActor @Sendable (Bool) -> Void) {
        queue.async { [self] in
            let running = startOnQueue()
            if !running { releaseOnQueue() }
            Task { @MainActor in done(running) }
        }
    }

    /// Stops the session and removes the input, which releases the camera.
    func stop() {
        queue.async { [self] in releaseOnQueue() }
    }

    private func startOnQueue() -> Bool {
        if session.isRunning { return true }
        guard session.inputs.isEmpty else {
            session.startRunning()
            return session.isRunning
        }
        guard let device = AVCaptureDevice.systemPreferredCamera ?? AVCaptureDevice.default(for: .video),
              let input = try? AVCaptureDeviceInput(device: device)
        else { return false }
        session.beginConfiguration()
        // Enough for a 180 pt bubble on a Retina display.
        if session.canSetSessionPreset(.vga640x480) { session.sessionPreset = .vga640x480 }
        guard session.canAddInput(input) else {
            session.commitConfiguration()
            return false
        }
        session.addInput(input)
        session.commitConfiguration()
        session.startRunning()
        return session.isRunning
    }

    private func releaseOnQueue() {
        if session.isRunning { session.stopRunning() }
        session.beginConfiguration()
        for input in session.inputs { session.removeInput(input) }
        session.commitConfiguration()
    }
}

/// The round body of the bubble: a neutral placeholder fill under the preview layer, dragged with the mouse.
private final class CameraBubbleView: NSView {
    /// The panel's new origin (AppKit global coordinates) while the bubble is dragged.
    var onDrag: ((CGPoint) -> Void)?
    private var dragStart: (mouse: CGPoint, origin: CGPoint)?

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        wantsLayer = true
        layer?.backgroundColor = NSColor(white: 0.23, alpha: 1).cgColor
        layer?.masksToBounds = true
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) is not supported") }

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override var mouseDownCanMoveWindow: Bool { false }

    override func mouseDown(with event: NSEvent) {
        dragStart = (NSEvent.mouseLocation, window?.frame.origin ?? .zero)
    }

    override func mouseDragged(with event: NSEvent) {
        guard let dragStart else { return }
        let mouse = NSEvent.mouseLocation
        onDrag?(CGPoint(x: dragStart.origin.x + mouse.x - dragStart.mouse.x,
                        y: dragStart.origin.y + mouse.y - dragStart.mouse.y))
    }

    override func mouseUp(with event: NSEvent) {
        dragStart = nil
    }
}
