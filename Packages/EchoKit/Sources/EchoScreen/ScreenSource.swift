import CoreGraphics

/// What a screen recording captures.
public enum ScreenSource: Sendable, Equatable {
    case display(CGDirectDisplayID)
    case window(CGWindowID)
    /// `rect` is in points, local to the display, top-left origin (as `SCStreamConfiguration.sourceRect`).
    case area(display: CGDirectDisplayID, rect: CGRect)

    /// The video file in the conversation folder.
    public static let videoFileName = "video.mp4"

    /// Converts `area`, a rect in AppKit global coordinates (bottom-left origin of the primary screen), into points
    /// local to the display whose AppKit frame (`NSScreen.frame`, not `CGDisplayBounds`) is `displayFrame`, with a
    /// top-left origin, plus its pixel size at `scale` rounded down to even numbers. Works for displays left of or
    /// above the primary (negative or offset origins).
    public static func displayLocal(area: CGRect, displayFrame: CGRect, scale: CGFloat)
        -> (rect: CGRect, pixelWidth: Int, pixelHeight: Int)
    {
        let rect = CGRect(x: area.minX - displayFrame.minX, y: displayFrame.maxY - area.maxY,
                          width: area.width, height: area.height)
        return (rect, Int(area.width * scale) / 2 * 2, Int(area.height * scale) / 2 * 2)
    }
}
