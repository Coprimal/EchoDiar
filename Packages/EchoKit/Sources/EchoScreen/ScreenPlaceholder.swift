import CoreGraphics
import CoreMedia
import CoreText
import CoreVideo

/// The frame that stands for the spans where the screen was off: black, with a short text in large white letters in
/// the middle, so the one video file still covers the whole recording. The caller passes the text already localized.
public enum ScreenPlaceholder {
    /// A ready sample buffer of exactly `width` x `height` pixels (32BGRA, IOSurface-backed) showing `text` in a bold
    /// system font of about `height / 12` points, centred and wrapped to 80 % of the width. Its timing is invalid:
    /// the writer re-times every copy it appends. Nil when the buffer cannot be made.
    public static func frame(width: Int, height: Int, text: String) -> CMSampleBuffer? {
        var pixelBuffer: CVPixelBuffer?
        let attributes = [kCVPixelBufferIOSurfacePropertiesKey: [:]] as CFDictionary
        guard CVPixelBufferCreate(kCFAllocatorDefault, width, height, kCVPixelFormatType_32BGRA, attributes,
                                  &pixelBuffer) == kCVReturnSuccess, let pixelBuffer else { return nil }
        CVPixelBufferLockBaseAddress(pixelBuffer, [])
        let drawn = draw(text, into: pixelBuffer, width: width, height: height)
        CVPixelBufferUnlockBaseAddress(pixelBuffer, [])
        guard drawn, let format = try? CMVideoFormatDescription(imageBuffer: pixelBuffer) else { return nil }
        let timing = CMSampleTimingInfo(duration: .invalid, presentationTimeStamp: .invalid, decodeTimeStamp: .invalid)
        return try? CMSampleBuffer(imageBuffer: pixelBuffer, formatDescription: format, sampleTiming: timing)
    }

    private static func draw(_ text: String, into pixelBuffer: CVPixelBuffer, width: Int, height: Int) -> Bool {
        let bitmapInfo = CGBitmapInfo.byteOrder32Little.rawValue | CGImageAlphaInfo.premultipliedFirst.rawValue
        guard let context = CGContext(data: CVPixelBufferGetBaseAddress(pixelBuffer), width: width, height: height,
                                      bitsPerComponent: 8, bytesPerRow: CVPixelBufferGetBytesPerRow(pixelBuffer),
                                      space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: bitmapInfo) else { return false }
        context.setFillColor(CGColor(red: 0, green: 0, blue: 0, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: width, height: height))

        let font = CTFontCreateUIFontForLanguage(.emphasizedSystem, CGFloat(height) / 12, nil)
            ?? CTFontCreateWithName("Helvetica-Bold" as CFString, CGFloat(height) / 12, nil)
        var alignment = CTTextAlignment.center
        let paragraph = withUnsafeBytes(of: &alignment) { bytes in
            var setting = CTParagraphStyleSetting(spec: .alignment, valueSize: bytes.count, value: bytes.baseAddress!)
            return CTParagraphStyleCreate(&setting, 1)
        }
        let string = NSAttributedString(string: text, attributes: [
            NSAttributedString.Key(kCTFontAttributeName as String): font,
            NSAttributedString.Key(kCTForegroundColorAttributeName as String): CGColor(red: 1, green: 1, blue: 1, alpha: 1),
            NSAttributedString.Key(kCTParagraphStyleAttributeName as String): paragraph,
        ])
        let framesetter = CTFramesetterCreateWithAttributedString(string)
        let textWidth = CGFloat(width) * 0.8
        let size = CTFramesetterSuggestFrameSizeWithConstraints(framesetter, CFRange(location: 0, length: 0), nil,
                                                                CGSize(width: textWidth, height: CGFloat(height)), nil)
        let box = CGRect(x: (CGFloat(width) - textWidth) / 2, y: (CGFloat(height) - size.height) / 2,
                         width: textWidth, height: size.height)
        let frame = CTFramesetterCreateFrame(framesetter, CFRange(location: 0, length: 0), CGPath(rect: box, transform: nil), nil)
        CTFrameDraw(frame, context)
        return true
    }
}
