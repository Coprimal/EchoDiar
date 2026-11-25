using EchoDiar.Core.Screen;
using static EchoDiar.Core.Screen.VideoTimeline;

namespace EchoDiar.Tests.Core;

// Guards the video's sync with the audio file: which frames are written, at which file time and for how long.
public sealed class VideoTimelineTests
{
    private static void AssertWrites(Step step, double time, double duration)
    {
        Assert.True(step.Holds);
        Assert.NotNull(step.Write);
        Assert.Equal(time, step.Write.Value.Time, 9);
        Assert.Equal(duration, step.Write.Value.Duration, 9);
    }

    [Fact]
    public void PlaceholderAtZeroIsHeldUntilTheNextRealFrame()
    {
        var timeline = new VideoTimeline(fps: 15);

        AssertWrites(timeline.Frame(2.5), time: 0, duration: 2.5);
        // Screen off, then on again: the placeholder holds until the next real frame.
        AssertWrites(timeline.Placeholder(4.1), time: 2.5, duration: 1.6);
        AssertWrites(timeline.Frame(7.3), time: 4.1, duration: 3.2);
    }

    [Fact]
    public void StopPadsTheLastFrameToTheStopTime()
    {
        var timeline = new VideoTimeline(fps: 15);
        timeline.Frame(1.3);
        timeline.Frame(2.1);

        var last = timeline.Stop(9.7);

        Assert.NotNull(last);
        Assert.Equal(2.1, last.Value.Time, 9);
        Assert.Equal(7.6, last.Value.Duration, 9);
    }

    [Fact]
    public void FramesCloserThanOneFrameIntervalAreDropped()
    {
        var timeline = new VideoTimeline(fps: 10);
        timeline.Frame(1.0);

        Assert.Equal(new Step(false, null), timeline.Frame(1.05));
        AssertWrites(timeline.Frame(1.13), time: 1.0, duration: 0.13);
    }

    // Frames stamped before the start (negative), in a pause (null) or in a pause gap but delivered after resume
    // (before the last written time) are dropped, and the held frame is unchanged.
    [Theory]
    [InlineData(null)]
    [InlineData(-0.5)]
    [InlineData(1.5)]
    public void BackwardsNegativeOrPausedFramesAreDropped(double? fileTime)
    {
        var timeline = new VideoTimeline(fps: 15);
        timeline.Frame(2.0);

        Assert.Equal(new Step(false, null), timeline.Frame(fileTime));
        AssertWrites(timeline.Frame(2.5), time: 2.0, duration: 0.5);
    }
}
