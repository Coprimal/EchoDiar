using System.Diagnostics;
using EchoDiar.Platform.Screen;

namespace EchoDiar.Platform.Tests;

// The background blur's segmenter on the bundled model and the onnxruntime.dll that ships (sherpa-onnx's, Windows
// only, CI job test-platform): guards the pairing of the ONNX Runtime managed API with that native DLL.
public sealed class PersonSegmenterTests
{
    private const int Width = 320;
    private const int Height = 240;

    private readonly ITestOutputHelper _output;

    public PersonSegmenterTests(ITestOutputHelper output) => _output = output;

    // The core of the blur: the bundled model loads through the shipped onnxruntime.dll and turns a camera-sized
    // frame into a frame-sized mask. A synthetic frame has no real person, so only the run itself is checked.
    [Fact]
    public void BundledModelRunsOnTheShippedOnnxRuntime()
    {
        Assert.True(File.Exists(PersonSegmenter.DefaultModelPath), $"No bundled model at {PersonSegmenter.DefaultModelPath}");

        var created = PersonSegmenter.TryCreate(out var segmenter, out var error);
        var runtime = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
            .FirstOrDefault(m => string.Equals(m.ModuleName, "onnxruntime.dll", StringComparison.OrdinalIgnoreCase));
        _output.WriteLine($"onnxruntime.dll: {runtime?.FileName ?? "not loaded"} {runtime?.FileVersionInfo.FileVersion}");

        // The error names the ORT problem on CI (DllNotFoundException, an API version mismatch, a bad model).
        Assert.True(created, $"TryCreate failed: {error}");
        Assert.Null(error);
        Assert.NotNull(segmenter);
        Assert.NotNull(runtime);
        // sherpa-onnx's copy from the test output, never another onnxruntime.dll on the system (Windows ML's in System32).
        Assert.StartsWith(AppContext.BaseDirectory, runtime.FileName, StringComparison.OrdinalIgnoreCase);

        using (segmenter)
        {
            var stride = Width * 4 + 64; // padded rows, as a mapped texture has
            var frame = MakeFrame(stride);
            var mask = new byte[Width * Height];

            segmenter.Segment(frame, Width, Height, stride, mask);

            _output.WriteLine($"mask: {Width}x{Height}, min {mask.Min()}, max {mask.Max()}, mean {mask.Average(b => b):0.0}");
        }
    }

    /// <summary>A mid-grey frame with a skin-toned ellipse in the middle (BGRA, padded rows).</summary>
    private static byte[] MakeFrame(int stride)
    {
        var pixels = new byte[stride * Height];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                double dx = (x - Width / 2.0) / (Width / 4.0), dy = (y - Height / 2.0) / (Height / 2.5);
                var inside = dx * dx + dy * dy <= 1;
                var i = y * stride + x * 4;
                pixels[i] = inside ? (byte)140 : (byte)128;     // B
                pixels[i + 1] = inside ? (byte)170 : (byte)128; // G
                pixels[i + 2] = inside ? (byte)220 : (byte)128; // R
                pixels[i + 3] = 255;
            }
        }
        return pixels;
    }
}
