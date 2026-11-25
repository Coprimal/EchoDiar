using EchoDiar.Platform.Screen;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace EchoDiar.Platform.Tests;

// The screen video writer on the real Media Foundation Sink Writer and H.264 encoder (Windows only, CI job
// test-platform), read back through the Windows media APIs.
public sealed class VideoEncoderTests : IDisposable
{
    private const int Width = 320;
    private const int Height = 240;
    private const int Fps = 15;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "EchoDiarPlatformTests-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public VideoEncoderTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // The media source may release the file a moment after it closes; a leftover temp folder is harmless.
        }
    }

    // The core of the video file: frames written at their file times come back as an H.264 mp4 that lasts until the
    // last frame ends (its longer duration included), so the video lines up with the audio.
    [Fact]
    public async Task FramesAtFileTimesReadBackAsH264EndingWithTheLastFrame()
    {
        var support = VideoEncoder.Detect();
        _output.WriteLine($"encoders: {string.Join(", ", support.EncodersFound)}; HEVC decoder: {support.HevcDecoderFound}");

        const int frames = 30;
        const double lastDuration = 0.5;
        var end = (frames - 1) / (double)Fps + lastDuration;
        var path = Path.Combine(_dir, "video.mp4");
        var stride = Width * 4 + 64; // padded rows, as a mapped texture has
        var pixels = new byte[stride * Height];
        using (var encoder = new VideoEncoder(path, Width, Height, Fps, 0.75, VideoCodec.H264))
        {
            for (var i = 0; i < frames; i++)
            {
                Array.Fill(pixels, (byte)(i * 8));
                encoder.WriteFrame(pixels, stride, i / (double)Fps, i == frames - 1 ? lastDuration : 1.0 / Fps);
            }
            encoder.Finish();
            _output.WriteLine($"quality: {encoder.QualityError ?? "constant quality"}");
        }

        var file = await StorageFile.GetFileFromPathAsync(path);
        var profile = await MediaEncodingProfile.CreateFromFileAsync(file);
        TimeSpan? duration;
        using (var source = MediaSource.CreateFromStorageFile(file))
        {
            await source.OpenAsync();
            duration = source.Duration;
        }
        _output.WriteLine(
            $"video.mp4: {profile.Video.Subtype} {profile.Video.Width}x{profile.Video.Height}, {duration?.TotalSeconds:0.0000} s "
            + $"(expected {end:0.0000}), {new FileInfo(path).Length} bytes");

        Assert.Equal(MediaEncodingSubtypes.H264, profile.Video.Subtype, ignoreCase: true);
        Assert.Equal((uint)Width, profile.Video.Width);
        Assert.Equal((uint)Height, profile.Video.Height);
        Assert.NotNull(duration);
        Assert.InRange(duration.Value.TotalSeconds, end - 1.0 / Fps, end + 1.0 / Fps);
    }
}
