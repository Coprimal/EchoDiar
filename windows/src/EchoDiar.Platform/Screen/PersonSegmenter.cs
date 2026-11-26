using System.Diagnostics.CodeAnalysis;
using Microsoft.ML.OnnxRuntime;

namespace EchoDiar.Platform.Screen;

/// <summary>
/// Finds the person in a camera frame for the camera bubble's "Blur background": the bundled PP-HumanSeg model
/// (<see cref="ModelFileName"/>, OpenCV zoo) run on the CPU through the <c>onnxruntime.dll</c> that sherpa-onnx ships
/// (Microsoft.ML.OnnxRuntime.Managed).
/// <para>As the zoo's <c>pphumanseg.py</c>: the frame is stretched to 192x192 (bilinear), given as RGB planes
/// normalised to <c>(v / 255 - 0.5) / 0.5</c> (input <c>x</c>, 1x3x192x192 float); the output is the softmax of the two
/// classes (1x2x192x192, background then person), and the person is where channel 1 is above 0.5 (its argmax). Here
/// the edge around 0.5 is kept soft, feathered a little and scaled back to the frame bilinearly.</para>
/// <para>All buffers are made once; <see cref="Segment"/> allocates nothing per frame. One thread at a time; never
/// dispose it while <see cref="Segment"/> runs.</para>
/// </summary>
public sealed class PersonSegmenter : IDisposable
{
    /// <summary>The bundled model, in the <c>Models</c> folder next to the app.</summary>
    public const string ModelFileName = "human_segmentation_pphumanseg_2023mar.onnx";

    /// <summary>The model's input and output side, in pixels.</summary>
    public const int ModelSize = 192;

    private const int Plane = ModelSize * ModelSize;

    private readonly InferenceSession _session;
    private readonly RunOptions _runOptions;
    private readonly OrtValue _inputValue;
    private readonly OrtValue _outputValue;
    private readonly string[] _inputNames;
    private readonly string[] _outputNames;
    private readonly OrtValue[] _inputs;
    private readonly OrtValue[] _outputs;
    private readonly float[] _input = new float[3 * Plane];
    private readonly float[] _output = new float[2 * Plane];
    private readonly byte[] _small = new byte[Plane];
    private readonly byte[] _feathered = new byte[Plane];
    private bool _disposed;

    private PersonSegmenter(InferenceSession session, string inputName, string outputName)
    {
        _session = session;
        _runOptions = new RunOptions();
        _inputNames = [inputName];
        _outputNames = [outputName];
        // Pinned views of the managed arrays: the model reads and writes them in place.
        _inputValue = OrtValue.CreateTensorValueFromMemory(_input, [1, 3, ModelSize, ModelSize]);
        _outputValue = OrtValue.CreateTensorValueFromMemory(_output, [1, 2, ModelSize, ModelSize]);
        _inputs = [_inputValue];
        _outputs = [_outputValue];
    }

    /// <summary>The bundled model's path: <c>Models\</c> + <see cref="ModelFileName"/> in the app's folder.</summary>
    public static string DefaultModelPath => Path.Combine(AppContext.BaseDirectory, "Models", ModelFileName);

    /// <summary>
    /// Loads the model at <paramref name="modelPath"/> (null: <see cref="DefaultModelPath"/>) on the CPU, two threads,
    /// and checks its input and output shapes. Never throws: a missing or wrong model, a missing or mismatched
    /// <c>onnxruntime.dll</c> give false with <paramref name="error"/> (for the log).
    /// </summary>
    public static bool TryCreate([NotNullWhen(true)] out PersonSegmenter? segmenter, [NotNullWhen(false)] out string? error,
        string? modelPath = null)
    {
        segmenter = null;
        InferenceSession? session = null;
        try
        {
            var path = modelPath ?? DefaultModelPath;
            if (!File.Exists(path))
            {
                error = $"No model at {path}";
                return false;
            }
            using (var options = new SessionOptions())
            {
                options.IntraOpNumThreads = 2;
                options.InterOpNumThreads = 1;
                options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                options.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR;
                // Idle threads sleep instead of spinning between frames.
                options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
                session = new InferenceSession(path, options);
            }
            if (session.InputMetadata.Count != 1 || session.OutputMetadata.Count != 1)
            {
                error = $"The model has {session.InputMetadata.Count} inputs and {session.OutputMetadata.Count} outputs, not 1 and 1";
                session.Dispose();
                return false;
            }
            var (inputName, input) = session.InputMetadata.First();
            var (outputName, output) = session.OutputMetadata.First();
            if (!HasShape(input.Dimensions, 3) || !HasShape(output.Dimensions, 2))
            {
                error = $"Unexpected model shapes: input [{string.Join(",", input.Dimensions)}], output [{string.Join(",", output.Dimensions)}]";
                session.Dispose();
                return false;
            }
            segmenter = new PersonSegmenter(session, inputName, outputName);
            error = null;
            return true;
        }
        catch (Exception e)
        {
            // A missing or mismatched native onnxruntime.dll surfaces as DllNotFoundException, EntryPointNotFoundException
            // or a TypeInitializationException of the managed API; a bad model as OnnxRuntimeException.
            session?.Dispose();
            segmenter = null;
            error = $"{e.GetType().Name}: {e.Message}";
            return false;
        }
    }

    /// <summary>
    /// The person in <paramref name="bgra"/> (BGRA rows, top-down, <paramref name="stride"/> bytes apart) as
    /// <paramref name="mask"/>: <paramref name="width"/> x <paramref name="height"/> bytes, rows <paramref name="width"/>
    /// apart, 255 on the person, 0 on the background, a soft edge between. Throws on bad sizes and when the model
    /// fails (<see cref="OnnxRuntimeException"/>).
    /// </summary>
    public void Segment(ReadOnlySpan<byte> bgra, int width, int height, int stride, Span<byte> mask)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (width <= 0 || height <= 0 || stride < width * 4 || bgra.Length < (long)stride * (height - 1) + width * 4L)
        {
            throw new ArgumentException($"A {width}x{height} BGRA frame with a stride of {stride} does not fit {bgra.Length} bytes");
        }
        if (mask.Length < width * height)
        {
            throw new ArgumentException($"A {width}x{height} mask needs {width * height} bytes, not {mask.Length}");
        }
        Preprocess(bgra, width, height, stride);
        _session.Run(_runOptions, _inputNames, _inputs, _outputNames, _outputs);
        Postprocess();
        Upscale(mask, width, height);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _inputValue.Dispose();
        _outputValue.Dispose();
        _runOptions.Dispose();
        _session.Dispose();
    }

    /// <summary>[1, channels, 192, 192] (a free dimension, negative, counts as matching).</summary>
    private static bool HasShape(int[] dimensions, int channels) =>
        dimensions.Length == 4 && dimensions[0] is 1 or < 0 && dimensions[1] == channels
        && dimensions[2] is ModelSize or < 0 && dimensions[3] is ModelSize or < 0;

    /// <summary>The frame stretched to 192x192 (bilinear, as cv.resize) into RGB planes, <c>v / 127.5 - 1</c>.</summary>
    private void Preprocess(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        float scaleX = width / (float)ModelSize, scaleY = height / (float)ModelSize;
        for (var oy = 0; oy < ModelSize; oy++)
        {
            var sy = Math.Clamp((oy + 0.5f) * scaleY - 0.5f, 0, height - 1);
            var y0 = (int)sy;
            var y1 = Math.Min(y0 + 1, height - 1);
            var fy = sy - y0;
            var row0 = bgra.Slice(y0 * stride);
            var row1 = bgra.Slice(y1 * stride);
            for (var ox = 0; ox < ModelSize; ox++)
            {
                var sx = Math.Clamp((ox + 0.5f) * scaleX - 0.5f, 0, width - 1);
                var x0 = (int)sx;
                var x1 = Math.Min(x0 + 1, width - 1);
                var fx = sx - x0;
                int a = x0 * 4, b = x1 * 4;
                var index = oy * ModelSize + ox;
                for (var c = 0; c < 3; c++)
                {
                    // BGRA byte c (B, G, R) goes to plane 2 - c (R first).
                    var top = row0[a + c] + (row0[b + c] - row0[a + c]) * fx;
                    var bottom = row1[a + c] + (row1[b + c] - row1[a + c]) * fx;
                    _input[(2 - c) * Plane + index] = (top + (bottom - top) * fy) / 127.5f - 1f;
                }
            }
        }
    }

    /// <summary>
    /// The person's probability (channel 1) as a soft mask: 0 below 0.3, 255 above 0.7, smooth between (the argmax
    /// edge, 0.5, stays in the middle), then a 3x3 box for a little feathering.
    /// </summary>
    private void Postprocess()
    {
        for (var i = 0; i < Plane; i++)
        {
            var t = Math.Clamp((_output[Plane + i] - 0.3f) / 0.4f, 0f, 1f);
            _small[i] = (byte)(t * t * (3 - 2 * t) * 255 + 0.5f);
        }
        for (var y = 0; y < ModelSize; y++)
        {
            for (var x = 0; x < ModelSize; x++)
            {
                int sum = 0, count = 0;
                for (var dy = -1; dy <= 1; dy++)
                {
                    var yy = y + dy;
                    if ((uint)yy >= ModelSize) continue;
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var xx = x + dx;
                        if ((uint)xx >= ModelSize) continue;
                        sum += _small[yy * ModelSize + xx];
                        count++;
                    }
                }
                _feathered[y * ModelSize + x] = (byte)((sum + count / 2) / count);
            }
        }
    }

    /// <summary>The 192x192 mask scaled to the frame, bilinear.</summary>
    private void Upscale(Span<byte> mask, int width, int height)
    {
        float scaleX = ModelSize / (float)width, scaleY = ModelSize / (float)height;
        for (var y = 0; y < height; y++)
        {
            var sy = Math.Clamp((y + 0.5f) * scaleY - 0.5f, 0, ModelSize - 1);
            var y0 = (int)sy;
            var y1 = Math.Min(y0 + 1, ModelSize - 1);
            var fy = sy - y0;
            int row0 = y0 * ModelSize, row1 = y1 * ModelSize;
            var to = mask.Slice(y * width, width);
            for (var x = 0; x < width; x++)
            {
                var sx = Math.Clamp((x + 0.5f) * scaleX - 0.5f, 0, ModelSize - 1);
                var x0 = (int)sx;
                var x1 = Math.Min(x0 + 1, ModelSize - 1);
                var fx = sx - x0;
                var top = _feathered[row0 + x0] + (_feathered[row0 + x1] - _feathered[row0 + x0]) * fx;
                var bottom = _feathered[row1 + x0] + (_feathered[row1 + x1] - _feathered[row1 + x0]) * fx;
                to[x] = (byte)(top + (bottom - top) * fy + 0.5f);
            }
        }
    }
}
