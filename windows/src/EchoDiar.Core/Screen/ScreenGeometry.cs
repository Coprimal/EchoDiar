namespace EchoDiar.Core.Screen;

/// <summary>
/// Pixel geometry of a screen recording: the captured area, the encoder's output size and where a source sits in it.
/// Sizes handed to the encoder are even (macOS <c>ScreenRecorder.swift</c> rounds down with <c>/ 2 * 2</c>).
/// </summary>
public static class ScreenGeometry
{
    public readonly record struct Size(int Width, int Height);

    public readonly record struct Rect(int X, int Y, int Width, int Height);

    /// <summary>The largest H.264 frame the recorder encodes (keeping aspect, the output stays under it).</summary>
    public static readonly Size H264Limit = new(4096, 2304);

    /// <summary>
    /// <paramref name="area"/> (monitor pixels, top-left origin of the monitor) clipped to the monitor, with its width
    /// and height rounded down to even. Null when less than 2x2 pixels are left.
    /// </summary>
    public static Rect? AreaPixels(Rect area, Size monitor)
    {
        int left = Math.Clamp(area.X, 0, monitor.Width), top = Math.Clamp(area.Y, 0, monitor.Height);
        int right = Math.Clamp(area.X + area.Width, 0, monitor.Width);
        int bottom = Math.Clamp(area.Y + area.Height, 0, monitor.Height);
        int width = (right - left) / 2 * 2, height = (bottom - top) / 2 * 2;
        return width < 2 || height < 2 ? null : new Rect(left, top, width, height);
    }

    /// <summary>
    /// The encoder's output size for <paramref name="source"/>: the source itself when it fits in
    /// <paramref name="limit"/>, else scaled down to fit keeping its aspect; never upscaled, rounded down to even.
    /// </summary>
    public static Size OutputSize(Size source, Size limit)
    {
        long width = source.Width, height = source.Height;
        if (width > limit.Width || height > limit.Height)
        {
            // Integer maths: 5120x2880 gives exactly 4096x2304.
            if (width * limit.Height >= height * limit.Width)
            {
                (width, height) = (limit.Width, height * limit.Width / width);
            }
            else
            {
                (width, height) = (width * limit.Height / height, limit.Height);
            }
        }
        return new Size((int)width / 2 * 2, (int)height / 2 * 2);
    }

    /// <summary>
    /// Where <paramref name="source"/> is drawn in <paramref name="output"/>: as large as fits keeping its aspect,
    /// centred, the rest left black.
    /// </summary>
    public static Rect AspectFit(Size source, Size output)
    {
        if ((long)source.Width * output.Height >= (long)source.Height * output.Width)
        {
            int height = (int)((long)source.Height * output.Width / source.Width);
            return new Rect(0, (output.Height - height) / 2, output.Width, height);
        }
        int width = (int)((long)source.Width * output.Height / source.Height);
        return new Rect((output.Width - width) / 2, 0, width, output.Height);
    }

    /// <summary>
    /// The free space is too low to record: true only when <paramref name="availableBytes"/> is known and below
    /// <paramref name="minimumBytes"/> (macOS <c>ScreenRecorder.swift</c> <c>if let available, available &lt; minimumFreeBytes</c>).
    /// </summary>
    public static bool IsLowDisk(long? availableBytes, long minimumBytes) =>
        availableBytes is { } available && available < minimumBytes;
}
