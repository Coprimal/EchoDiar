using static EchoDiar.Core.Screen.ScreenGeometry;

namespace EchoDiar.Tests.Core;

// Guards the sizes handed to the encoder (odd or oversized ones fail it) and the low-disk refusal that protects data.
public sealed class ScreenGeometryTests
{
    private static readonly Size Monitor = new(2560, 1440);

    [Theory]
    [InlineData(100, 200, 641, 361, 100, 200, 640, 360)]
    [InlineData(-10, 100, 641, 361, 0, 100, 630, 360)]
    [InlineData(2400, 1300, 400, 400, 2400, 1300, 160, 140)]
    public void AreaIsClampedToTheMonitorWithEvenSize(int x, int y, int width, int height,
        int ex, int ey, int ewidth, int eheight)
    {
        Assert.Equal(new Rect(ex, ey, ewidth, eheight), AreaPixels(new Rect(x, y, width, height), Monitor));
    }

    [Theory]
    [InlineData(5120, 2880, 4096, 2304)]
    [InlineData(1921, 1081, 1920, 1080)]
    public void OutputSizeStaysUnderTheH264LimitKeepingAspect(int width, int height, int ewidth, int eheight)
    {
        Assert.Equal(new Size(ewidth, eheight), OutputSize(new Size(width, height), H264Limit));
    }

    [Theory]
    [InlineData(1000, 1000, 420, 0, 1080, 1080)]
    [InlineData(2560, 1080, 0, 135, 1920, 810)]
    public void AspectFitCentresTheSourceInTheOutput(int width, int height, int ex, int ey, int ewidth, int eheight)
    {
        Assert.Equal(new Rect(ex, ey, ewidth, eheight), AspectFit(new Size(width, height), new Size(1920, 1080)));
    }

    [Theory]
    [InlineData(999L, true)]
    [InlineData(1000L, false)]
    [InlineData(1001L, false)]
    [InlineData(null, false)]
    public void LowDiskOnlyWhenKnownAndBelowTheMinimum(long? available, bool low)
    {
        Assert.Equal(low, IsLowDisk(available, 1000));
    }
}
