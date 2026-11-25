import AVFoundation
import AppKit
import CoreGraphics
import CoreImage
import Metal
import Vision

/// The camera bubble of a screen recording: a round floating panel with a mirrored live preview of the camera,
/// draggable by its body inside the recorded display, area or window.
///
/// The recording calls `show(on:within:blur:)` while its video runs unpaused with the option on, and `hide()`
/// otherwise. The panel is left in screen captures (no `sharingType = .none`; a window recording lets its `windowID`
/// into the picture), so the picture ends up in the screen video only because the bubble is on screen: with blur off
/// the session has an input and the preview layer, no output; with "Blur background" on, a video data output feeds `BlurRenderer`, which blurs everything but
/// the person in memory for a layer shown instead of the preview. This controller stores no frame.
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
    /// Shows `BlurRenderer`'s frames while `blur` is on; the preview layer stays hidden meanwhile, so the sharp
    /// background never shows (the placeholder does until the first blurred frame).
    private var blurLayer: CALayer?
    /// Made on the first `show` with blur on.
    private var renderer: BlurRenderer?
    private var blur = false
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

    /// The panel's window ID while the bubble shows (or starts), for a window recording to let into its picture.
    var windowID: CGWindowID? {
        (state == .starting || state == .showing) && panel.windowNumber > 0 ? CGWindowID(panel.windowNumber) : nil
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
    /// is while the place is unchanged, and moves with a place that only moved, as a followed window's) and switching
    /// `blur` on the running session. Without camera access it does nothing and `state` is `.unavailable`; when the
    /// display is not connected it hides.
    func show(on display: CGDirectDisplayID, within area: CGRect?, blur: Bool) {
        guard let screen = Self.screen(display) else {
            hide()
            return
        }
        let frame = screen.frame
        let newTarget = area.map {
            CGRect(x: frame.minX + $0.minX, y: frame.maxY - $0.maxY, width: $0.width, height: $0.height)
        } ?? screen.visibleFrame
        let targetChanged = newTarget != target
        let oldTarget = target
        target = newTarget
        switch state {
        case .starting, .showing:
            if targetChanged, newTarget.size == oldTarget.size {
                let origin = panel.frame.origin
                move(to: CGPoint(x: origin.x + newTarget.minX - oldTarget.minX,
                                 y: origin.y + newTarget.minY - oldTarget.minY))
            } else {
                place(toCorner: targetChanged)
            }
            if blur != self.blur { setBlur(blur) }
        case .off, .unavailable:
            // Checked first: `show` must never raise the camera prompt.
            guard Self.authorizationStatus == .authorized else {
                state = .unavailable
                return
            }
            self.blur = blur
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
        blurLayer?.isHidden = true
        blurLayer?.contents = nil
        CATransaction.commit()
        panel.orderOut(nil)
        state = .off
    }

    // MARK: - Session

    private func start() {
        generation += 1
        let current = generation
        let camera = camera ?? makeCamera()
        camera.start(blur: blur ? blurRenderer() : nil) { [weak self] running in
            self?.started(running, generation: current)
        }
    }

    /// Switches the running (or starting) session between the preview and the blurred frames. The preview hides at
    /// once, before the session changes, so turning blur on never shows a sharp frame.
    private func setBlur(_ on: Bool) {
        blur = on
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        if on {
            previewLayer?.isHidden = true
            blurLayer?.isHidden = state != .showing
        } else {
            blurLayer?.isHidden = true
            blurLayer?.contents = nil
            previewLayer?.isHidden = state != .showing
        }
        CATransaction.commit()
        camera?.setBlur(on ? blurRenderer() : nil)
    }

    private func blurRenderer() -> BlurRenderer {
        if let renderer { return renderer }
        let renderer = BlurRenderer { [weak self] image in
            let controller = self
            DispatchQueue.main.async { MainActor.assumeIsolated { controller?.showBlurred(image) } }
        }
        self.renderer = renderer
        return renderer
    }

    /// A blurred frame from `BlurRenderer`; dropped once blur is off or the bubble is hidden.
    private func showBlurred(_ image: CGImage) {
        guard blur, state == .showing, let blurLayer else { return }
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        blurLayer.contents = image
        CATransaction.commit()
    }

    private func makeCamera() -> CameraSession {
        let camera = CameraSession()
        let layer = AVCaptureVideoPreviewLayer(session: camera.session)
        layer.videoGravity = .resizeAspectFill
        layer.isHidden = true
        layer.frame = bubble.bounds
        layer.autoresizingMask = [.layerWidthSizable, .layerHeightSizable]
        bubble.layer?.addSublayer(layer)
        let blurLayer = CALayer()
        blurLayer.contentsGravity = .resizeAspectFill
        blurLayer.isHidden = true
        blurLayer.frame = bubble.bounds
        blurLayer.autoresizingMask = [.layerWidthSizable, .layerHeightSizable]
        bubble.layer?.addSublayer(blurLayer)
        self.camera = camera
        previewLayer = layer
        self.blurLayer = blurLayer
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
        if blur {
            CATransaction.begin()
            CATransaction.setDisableActions(true)
            blurLayer?.isHidden = false
            CATransaction.commit()
        } else {
            previewLayer?.isHidden = false
        }
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
/// its frames go to the preview layer and, with blur on, to a video data output whose delegate is `BlurRenderer`.
private final class CameraSession: @unchecked Sendable {
    /// Read on the main actor only to attach the preview layer; changed only on `queue`.
    let session = AVCaptureSession()
    private let queue = DispatchQueue(label: "dinh.thi.EchoDiar.CameraBubble")

    /// Adds the camera, and the blur output when `blur` is given, and starts the session, then calls `done` on the
    /// main actor with whether it runs.
    func start(blur: BlurRenderer?, _ done: @escaping @MainActor @Sendable (Bool) -> Void) {
        queue.async { [self] in
            setOutputOnQueue(blur)
            let running = startOnQueue()
            if !running { releaseOnQueue() }
            Task { @MainActor in done(running) }
        }
    }

    /// Adds the blur output for `blur`, or removes it for nil, on the running session.
    func setBlur(_ blur: BlurRenderer?) {
        queue.async { [self] in
            guard !session.inputs.isEmpty else { return }
            setOutputOnQueue(blur)
        }
    }

    /// Stops the session and removes the input and the blur output, which releases the camera.
    func stop() {
        queue.async { [self] in releaseOnQueue() }
    }

    /// Adds a BGRA video data output delivering to `renderer` on its queue, or removes it for nil; nothing to do when
    /// the session already matches.
    private func setOutputOnQueue(_ renderer: BlurRenderer?) {
        let current = session.outputs.first { $0 is AVCaptureVideoDataOutput }
        guard (current != nil) != (renderer != nil) else { return }
        session.beginConfiguration()
        if let current { session.removeOutput(current) }
        if let renderer {
            let output = AVCaptureVideoDataOutput()
            output.videoSettings = [kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA]
            output.alwaysDiscardsLateVideoFrames = true
            output.setSampleBufferDelegate(renderer, queue: renderer.queue)
            renderer.reset()
            if session.canAddOutput(output) { session.addOutput(output) }
        }
        session.commitConfiguration()
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
        for output in session.outputs { session.removeOutput(output) }
        session.commitConfiguration()
    }
}

/// Blurs the background of the camera bubble's frames, in memory only: per frame, at most `maxRate` frames a second,
/// Vision's person segmentation gives a mask, and Core Image keeps the person sharp over a Gaussian blur of the same
/// frame, mirrored like the preview. Each result goes to `deliver` as a `CGImage`; no frame or mask is kept (only
/// Vision's smoothing state between frames), and nothing is logged or written. All its state is touched on `queue` only, where the frames arrive.
private final class BlurRenderer: NSObject, AVCaptureVideoDataOutputSampleBufferDelegate, @unchecked Sendable {
    /// About the screen recording's default 15 fps; the camera runs at up to 30.
    private static let maxRate = 15.0
    /// CIGaussianBlur's radius, in pixels of the 640×480 frame.
    private static let blurRadius = 20.0

    let queue = DispatchQueue(label: "dinh.thi.EchoDiar.CameraBubble.blur", qos: .userInitiated)
    private let deliver: @Sendable (CGImage) -> Void
    /// Metal-backed when a GPU is there; intermediates are not cached, as every frame is new.
    private let context: CIContext = {
        let options: [CIContextOption: Any] = [.cacheIntermediates: false]
        guard let device = MTLCreateSystemDefaultDevice() else { return CIContext(options: options) }
        return CIContext(mtlDevice: device, options: options)
    }()
    private let segmentation: VNGeneratePersonSegmentationRequest = {
        let request = VNGeneratePersonSegmentationRequest()
        request.qualityLevel = .balanced
        request.outputPixelFormat = kCVPixelFormatType_OneComponent8
        return request
    }()
    /// Vision smooths the mask over the frames of one sequence.
    private var sequence = VNSequenceRequestHandler()
    private var lastFrame: CMTime = .invalid

    init(deliver: @escaping @Sendable (CGImage) -> Void) {
        self.deliver = deliver
    }

    /// Starts a new sequence: called when the output is added, so a mask of an earlier session is not reused.
    func reset() {
        queue.async { [self] in
            sequence = VNSequenceRequestHandler()
            lastFrame = .invalid
        }
    }

    func captureOutput(_ output: AVCaptureOutput, didOutput sampleBuffer: CMSampleBuffer,
                       from connection: AVCaptureConnection) {
        let time = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
        // 10 % slack, so 30 fps camera frames are kept one in two.
        if lastFrame.isValid, (time - lastFrame).seconds < 0.9 / Self.maxRate { return }
        guard let pixels = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
        lastFrame = time
        if let image = blurred(pixels, mirror: !connection.isVideoMirrored) { deliver(image) }
    }

    /// The frame with its background blurred, at most 640 pixels wide, mirrored when `mirror` (once, as the preview);
    /// all blurred when no person mask could be made.
    private func blurred(_ pixels: CVPixelBuffer, mirror: Bool) -> CGImage? {
        var frame = CIImage(cvPixelBuffer: pixels)
        // The session asks for 640×480; a camera that refuses it is scaled down here, so the cost and radius hold.
        if frame.extent.width > 640 {
            let scale = 640 / frame.extent.width
            frame = frame.transformed(by: CGAffineTransform(scaleX: scale, y: scale))
        }
        let extent = frame.extent
        let background = frame.clampedToExtent().applyingGaussianBlur(sigma: Self.blurRadius).cropped(to: extent)
        var composed = background
        if (try? sequence.perform([segmentation], on: pixels, orientation: .up)) != nil,
           let mask = segmentation.results?.first?.pixelBuffer {
            let maskImage = CIImage(cvPixelBuffer: mask)
            let scaled = maskImage.transformed(by: CGAffineTransform(
                scaleX: extent.width / maskImage.extent.width, y: extent.height / maskImage.extent.height))
            composed = frame.applyingFilter("CIBlendWithMask", parameters: [
                kCIInputBackgroundImageKey: background, kCIInputMaskImageKey: scaled,
            ])
        }
        if mirror {
            composed = composed.transformed(
                by: CGAffineTransform(scaleX: -1, y: 1).translatedBy(x: -extent.width, y: 0))
        }
        return context.createCGImage(composed, from: extent)
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
