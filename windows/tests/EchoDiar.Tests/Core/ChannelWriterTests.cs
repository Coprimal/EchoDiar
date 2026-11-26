using EchoDiar.Core.Audio;
using EchoDiar.Core.Model;

namespace EchoDiar.Tests.Core;

// The capture writer's core: ring blocks are placed in the 2-channel 16 kHz CAF by their QPC time (100 ns units).
public sealed class ChannelWriterTests : IDisposable
{
    private const long T0 = 1_000_000_000; // an arbitrary QPC time, in 100 ns units
    private const long TicksPerMs = 10_000;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "EchoDiarTests-" + Guid.NewGuid().ToString("N"));

    public ChannelWriterTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void GapBetweenBlocksBecomesSilenceAtItsFrame()
    {
        var path = Path.Combine(_dir, "audio.caf");
        var writer = new ChannelWriter(path);
        var mic = new RingBuffer(16_000, 16_000);
        var system = new RingBuffer(16_000, 16_000);
        writer.Start(mic, system, T0);
        var block = Enumerable.Repeat(0.5f, 1_600).ToArray(); // 100 ms
        system.Write(block, T0);
        system.Write(block, T0 + 150 * TicksPerMs); // 50 ms after the first block ends
        writer.Stop();

        Assert.Null(writer.Stats.Failure);
        using var reader = new CafFile.Reader(path);
        Assert.Equal(4_000, reader.Frames);
        var samples = reader.Read(0, 4_000);
        float Ch(int channel, int frame) => samples[frame * 2 + channel];
        for (var frame = 0; frame < 4_000; frame++)
        {
            var expected = frame is >= 1_600 and < 2_400 ? 0f : 0.5f;
            Assert.Equal(expected, Ch(RecordingSession.Channel.System, frame), 0.001f);
            Assert.Equal(0f, Ch(RecordingSession.Channel.Mic, frame)); // the empty mic is padded
        }
    }

    [Fact]
    public void FortyEightKilohertzBlocksBecomeSixteenKilohertzFrames()
    {
        var writer = new ChannelWriter(Path.Combine(_dir, "audio.caf"));
        var mic = new RingBuffer(48_000, 48_000);
        var system = new RingBuffer(48_000, 48_000);
        writer.Start(mic, system, T0);
        const int blocks = 50;
        var block = Enumerable.Range(0, 480).Select(i => (float)Math.Sin(i * 2 * Math.PI * 440 / 48_000) * 0.5f).ToArray();
        for (var i = 0; i < blocks; i++)
        {
            mic.Write(block, T0 + i * 10 * TicksPerMs);
            system.Write(block, T0 + i * 10 * TicksPerMs);
        }
        writer.Stop();

        Assert.Null(writer.Stats.Failure);
        Assert.InRange(writer.Stats.FramesWritten, blocks * 160 - 1, blocks * 160 + 1);
    }

    // Guards video frame placement: a QPC time maps to the file second where audio stamped with it is written, and
    // after a pause the mapping continues from the paused position.
    [Fact]
    public void FileTimeMatchesWhereAudioIsWrittenAcrossPause()
    {
        var path = Path.Combine(_dir, "audio.caf");
        var writer = new ChannelWriter(path);
        var mic = new RingBuffer(32_000, 16_000);
        var system = new RingBuffer(32_000, 16_000);
        writer.Start(mic, system, T0);
        var second = Enumerable.Repeat(0.5f, 16_000).ToArray();
        mic.Write(second, T0);
        system.Write(second, T0);
        AssertFileTime(0.5, writer.FileTime(T0 + 500 * TicksPerMs));
        writer.Pause();
        var pausedAt = writer.Stats.Duration;
        Assert.Equal(1.0, pausedAt, 1e-9); // the captured second is written before the pause

        var resume = T0 + 10_000 * TicksPerMs;
        writer.Resume(resume);
        AssertFileTime(pausedAt, writer.FileTime(resume));
        var stamp = resume + 500 * TicksPerMs;
        var fileTime = writer.FileTime(stamp);
        AssertFileTime(pausedAt + 0.5, fileTime);
        system.Write(Enumerable.Repeat(0.5f, 1_600).ToArray(), stamp);
        writer.Stop();

        Assert.Null(writer.Stats.Failure);
        using var reader = new CafFile.Reader(path);
        var samples = reader.Read(0, (int)reader.Frames);
        var first = Enumerable.Range((int)(pausedAt * 16_000), (int)reader.Frames - (int)(pausedAt * 16_000))
            .First(frame => samples[frame * 2 + RecordingSession.Channel.System] > 0.25f);
        Assert.Equal(fileTime!.Value * 16_000, first, 1.0);
    }

    [Fact]
    public void FileTimeIsNullUnlessRunning()
    {
        var writer = new ChannelWriter(Path.Combine(_dir, "audio.caf"));
        Assert.Null(writer.FileTime(T0));
        writer.Start(new RingBuffer(16_000, 16_000), new RingBuffer(16_000, 16_000), T0);
        writer.Pause();
        Assert.Null(writer.FileTime(T0));
        writer.Resume(T0);
        Assert.NotNull(writer.FileTime(T0));
        writer.Stop();
        Assert.Null(writer.FileTime(T0));
    }

    private static void AssertFileTime(double expected, double? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected, actual.Value, 1e-9);
    }

    // Guards the "No system audio" banner: a tap that never delivered sound is suspect, one that went quiet after
    // delivering sound (the call ended, the app still holds its output open) is not.
    [Theory]
    [InlineData(0, true)]
    [InlineData(16_000, false)]
    public void SilentTapIsSuspectedOnlyForZerosFromTheStart(int soundFrames, bool suspected)
    {
        var frames = soundFrames + 6 * 16_000;
        var writer = new ChannelWriter(Path.Combine(_dir, "audio.caf")) { IsTargetPlayingOutput = () => true };
        var mic = new RingBuffer(frames, 16_000);
        var system = new RingBuffer(frames, 16_000);
        writer.Start(mic, system, T0);
        var sound = Enumerable.Range(0, frames)
            .Select(i => i < soundFrames ? (float)Math.Sin(i * 2 * Math.PI * 440 / 16_000) * 0.5f : 0f).ToArray();
        mic.Write(sound, T0);
        system.Write(sound, T0);
        writer.Stop();

        Assert.Null(writer.Stats.Failure);
        Assert.Equal(suspected, writer.Stats.SilentTapSuspected);
    }
}
