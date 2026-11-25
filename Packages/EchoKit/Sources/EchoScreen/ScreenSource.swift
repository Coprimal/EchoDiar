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

    /// The index in `displays` of the display a window is recorded on: the one holding the centre of `windowBounds`,
    /// else the one with most of the window; nil when the window is on none. All rects are CG global points, top-left
    /// origin (`kCGWindowBounds`, `SCWindow.frame`, `CGDisplayBounds`, `SCDisplay.frame`).
    public static func windowDisplayIndex(windowBounds: CGRect, displays: [CGRect]) -> Int? {
        let centre = CGPoint(x: windowBounds.midX, y: windowBounds.midY)
        if let index = displays.firstIndex(where: { $0.contains(centre) }) { return index }
        let areas = displays.map { display in
            let part = display.intersection(windowBounds)
            return part.isNull ? 0 : part.width * part.height
        }
        guard let best = areas.indices.max(by: { areas[$0] < areas[$1] }), areas[best] > 0 else { return nil }
        return best
    }

    /// The crop of a window recorded on `display`, in points local to it, top-left origin (as
    /// `SCStreamConfiguration.sourceRect`): the part of `windowBounds` on `display`. Nil when the window's centre is on
    /// another of `displays` (it moved there: `windowPlace` switches to it) or no part of it is on `display`. All inputs
    /// are CG global points, top-left origin.
    public static func windowCrop(windowBounds: CGRect, on display: CGRect, displays: [CGRect]) -> CGRect? {
        let centre = CGPoint(x: windowBounds.midX, y: windowBounds.midY)
        if !display.contains(centre), displays.contains(where: { $0.contains(centre) }) { return nil }
        let part = windowBounds.intersection(display)
        guard !part.isNull, part.width > 0, part.height > 0 else { return nil }
        return part.offsetBy(dx: -display.minX, dy: -display.minY)
    }

    /// Where a window video records the window now: the index in `displays` of its display and the crop there (as
    /// `windowCrop`). Stays on `currentDisplay` (an index in `displays`; nil at the start or when that display is gone)
    /// while the window's centre is on it or on no display and part of the window is on it; switches to the display
    /// holding the centre once the window moved there, else to the one `windowDisplayIndex` picks. Nil when the window
    /// is on no display: the video keeps its last place. All rects are CG global points, top-left origin.
    public static func windowPlace(windowBounds: CGRect, currentDisplay: Int?, displays: [CGRect])
        -> (display: Int, crop: CGRect)?
    {
        if let current = currentDisplay, displays.indices.contains(current),
           let crop = windowCrop(windowBounds: windowBounds, on: displays[current], displays: displays) {
            return (current, crop)
        }
        guard let index = windowDisplayIndex(windowBounds: windowBounds, displays: displays),
              let crop = windowCrop(windowBounds: windowBounds, on: displays[index], displays: displays)
        else { return nil }
        return (index, crop)
    }
}
