using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;

namespace EchoDiar.Platform.Screen;

public enum VideoCodec
{
    H264,
    Hevc,
}

/// <summary>
/// The video encoder MFTs found (the probe's <c>encodersFound</c>, "H.264: name" or "HEVC: name"), and whether an HEVC
/// encoder and an HEVC decoder exist.
/// </summary>
public sealed record VideoCodecSupport(IReadOnlyList<string> EncodersFound, bool HevcEncoderFound, bool HevcDecoderFound)
{
    /// <summary>HEVC only when it can be both written and played back here, else H.264.</summary>
    public VideoCodec Codec => HevcEncoderFound && HevcDecoderFound ? VideoCodec.Hevc : VideoCodec.H264;
}

/// <summary>
/// Writes a video-only mp4 through the Media Foundation Sink Writer (macOS <c>ScreenVideoWriter</c>): BGRA frames of a
/// fixed even size, each at its file time with its duration, encoded at constant quality (VBR) when the encoder takes
/// it, else at a fixed bitrate. Hardware encoders allowed, throttling off (frames arrive in real time). Errors are
/// <see cref="COMException"/>s whose message names the step and its HRESULT.
/// </summary>
/// <remarks>
/// Each instance holds one MFStartup, released by <see cref="Dispose"/> (Media Foundation counts them). Not
/// thread-safe: create and use it from one thread at a time, off the UI thread (MTA). A file that is disposed
/// without <see cref="Finish"/> is not playable.
/// </remarks>
public sealed class VideoEncoder : IDisposable
{
    private const double TicksPerSecond = 10_000_000; // Media Foundation times are in 100 ns
    private const MFT_ENUM_FLAG EnumFlags = MFT_ENUM_FLAG.MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG.MFT_ENUM_FLAG_ASYNCMFT
        | MFT_ENUM_FLAG.MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG.MFT_ENUM_FLAG_SORTANDFILTER;

    private readonly int _width;
    private readonly int _height;
    private readonly uint _stream;
    private IMFSinkWriter? _writer;
    private bool _finished;

    /// <param name="path">The mp4 to write (replaced when it exists).</param>
    /// <param name="width">Even, in pixels.</param>
    /// <param name="height">Even, in pixels.</param>
    /// <param name="quality">0..1, as macOS <c>AVVideoQualityKey</c> (CODECAPI_AVEncCommonQuality 0..100).</param>
    public VideoEncoder(string path, int width, int height, int fps, double quality, VideoCodec codec)
    {
        if (width <= 0 || height <= 0 || width % 2 != 0 || height % 2 != 0)
        {
            throw new ArgumentException($"The video size must be even and positive: {width}x{height}");
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fps);
        _width = width;
        _height = height;
        Codec = codec;

        Startup();
        try
        {
            (_writer, _stream, QualityError) = Open(path, codec, width, height, fps, quality);
        }
        catch
        {
            PInvoke.MFShutdown();
            throw;
        }
    }

    public VideoCodec Codec { get; }

    /// <summary>Null when the encoder took the constant-quality settings; else why, and the file uses a fixed bitrate.</summary>
    public string? QualityError { get; }

    /// <summary>The H.264 and HEVC encoder MFTs and HEVC decoder MFTs Media Foundation offers (hardware included).</summary>
    public static VideoCodecSupport Detect()
    {
        Startup();
        try
        {
            var h264 = Mfts(PInvoke.MFT_CATEGORY_VIDEO_ENCODER, null, VideoType(PInvoke.MFVideoFormat_H264));
            var hevc = Mfts(PInvoke.MFT_CATEGORY_VIDEO_ENCODER, null, VideoType(PInvoke.MFVideoFormat_HEVC));
            var hevcDecoders = Mfts(PInvoke.MFT_CATEGORY_VIDEO_DECODER, VideoType(PInvoke.MFVideoFormat_HEVC), null);
            return new VideoCodecSupport(
                [.. h264.Select(name => "H.264: " + name), .. hevc.Select(name => "HEVC: " + name)],
                hevc.Count > 0,
                hevcDecoders.Count > 0);
        }
        finally
        {
            PInvoke.MFShutdown();
        }
    }

    /// <summary>
    /// Writes one frame: <paramref name="bgra"/> holds the rows top-down, <paramref name="stride"/> bytes apart
    /// (at least width * 4); <paramref name="time"/> and <paramref name="duration"/> in file seconds.
    /// </summary>
    public unsafe void WriteFrame(ReadOnlySpan<byte> bgra, int stride, double time, double duration)
    {
        var writer = _writer ?? throw new ObjectDisposedException(nameof(VideoEncoder));
        if (_finished) throw new InvalidOperationException("The video is finished");
        var row = _width * 4;
        if (stride < row || bgra.Length < (long)stride * (_height - 1) + row)
        {
            throw new ArgumentException($"A {_width}x{_height} frame needs rows of {row} bytes: stride {stride}, {bgra.Length} bytes");
        }

        var size = row * _height;
        IMFMediaBuffer? buffer = null;
        IMFSample? sample = null;
        var step = "MFCreateMemoryBuffer";
        try
        {
            PInvoke.MFCreateMemoryBuffer((uint)size, out buffer).ThrowOnFailure();
            step = "IMFMediaBuffer.Lock";
            buffer.Lock(out byte* data);
            try
            {
                var target = new Span<byte>(data, size);
                if (stride == row) bgra[..size].CopyTo(target);
                else for (var y = 0; y < _height; y++) bgra.Slice(y * stride, row).CopyTo(target.Slice(y * row, row));
            }
            finally
            {
                buffer.Unlock();
            }
            buffer.SetCurrentLength((uint)size);

            step = "MFCreateSample";
            PInvoke.MFCreateSample(out sample).ThrowOnFailure();
            sample.AddBuffer(buffer);
            // The duration from the rounded end, so consecutive samples leave no 100 ns gaps.
            var start = Ticks(time);
            sample.SetSampleTime(start);
            sample.SetSampleDuration(Math.Max(1, Ticks(time + duration) - start));
            step = "IMFSinkWriter.WriteSample";
            writer.WriteSample(_stream, sample);
        }
        catch (Exception e)
        {
            throw Failure(step, e);
        }
        finally
        {
            if (sample is not null) Marshal.ReleaseComObject(sample);
            if (buffer is not null) Marshal.ReleaseComObject(buffer);
        }
    }

    /// <summary>Finalizes the file (it ends where the last frame ends). Frames cannot be written after it.</summary>
    public void Finish()
    {
        var writer = _writer ?? throw new ObjectDisposedException(nameof(VideoEncoder));
        if (_finished) return;
        _finished = true;
        try
        {
            writer.Finalize();
        }
        catch (Exception e)
        {
            throw Failure("IMFSinkWriter.Finalize", e);
        }
    }

    public void Dispose()
    {
        if (_writer is null) return;
        Marshal.FinalReleaseComObject(_writer);
        _writer = null;
        PInvoke.MFShutdown();
    }

    /// <summary>
    /// A sink writer with its video stream set up and writing begun, and why constant quality was refused (null when
    /// taken).
    /// </summary>
    private static (IMFSinkWriter Writer, uint Stream, string? QualityError) Open(
        string path, VideoCodec codec, int width, int height, int fps, double quality)
    {
        IMFAttributes? attributes = null;
        IMFMediaType? output = null;
        IMFMediaType? input = null;
        IMFAttributes? encoding = null;
        IMFSinkWriter? writer = null;
        string? qualityError = null;
        var step = "MFCreateAttributes";
        try
        {
            PInvoke.MFCreateAttributes(out attributes, 2).ThrowOnFailure();
            attributes.SetUINT32(PInvoke.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1);
            attributes.SetUINT32(PInvoke.MF_SINK_WRITER_DISABLE_THROTTLING, 1);
            step = "MFCreateSinkWriterFromURL";
            PInvoke.MFCreateSinkWriterFromURL(path, null, attributes, out writer).ThrowOnFailure();

            step = "output media type";
            output = MediaType(codec == VideoCodec.Hevc ? PInvoke.MFVideoFormat_HEVC : PInvoke.MFVideoFormat_H264, width, height, fps);
            // Required by the encoders; the rate itself only counts when constant quality is refused.
            output.SetUINT32(PInvoke.MF_MT_AVG_BITRATE, FallbackBitrate(width, height, fps));
            step = "IMFSinkWriter.AddStream";
            writer.AddStream(output, out var stream);

            step = "input media type";
            input = MediaType(PInvoke.MFVideoFormat_RGB32, width, height, fps);
            // Positive: top-down rows (RGB without it defaults to bottom-up, an upside-down video).
            input.SetUINT32(PInvoke.MF_MT_DEFAULT_STRIDE, (uint)(width * 4));
            step = "encoding parameters";
            PInvoke.MFCreateAttributes(out encoding, 2).ThrowOnFailure();
            encoding.SetUINT32(typeof(CODECAPI_AVEncCommonRateControlMode).GUID,
                (uint)eAVEncCommonRateControlMode.eAVEncCommonRateControlMode_Quality);
            encoding.SetUINT32(typeof(CODECAPI_AVEncCommonQuality).GUID, (uint)Math.Round(Math.Clamp(quality, 0, 1) * 100));
            step = "IMFSinkWriter.SetInputMediaType";
            try
            {
                writer.SetInputMediaType(stream, input, encoding);
            }
            catch (Exception e)
            {
                // Never fail the recording for the quality: the output type's bitrate applies instead.
                qualityError = $"0x{e.HResult:X8} {e.Message}";
                writer.SetInputMediaType(stream, input, null);
            }
            step = "IMFSinkWriter.BeginWriting";
            writer.BeginWriting();

            var opened = (writer, stream, qualityError);
            writer = null;
            return opened;
        }
        catch (Exception e)
        {
            throw Failure(step, e);
        }
        finally
        {
            if (writer is not null) Marshal.FinalReleaseComObject(writer);
            if (encoding is not null) Marshal.ReleaseComObject(encoding);
            if (input is not null) Marshal.ReleaseComObject(input);
            if (output is not null) Marshal.ReleaseComObject(output);
            if (attributes is not null) Marshal.ReleaseComObject(attributes);
        }
    }

    /// <summary>A progressive video type of <paramref name="subtype"/>, square pixels, at <paramref name="fps"/>.</summary>
    private static IMFMediaType MediaType(Guid subtype, int width, int height, int fps)
    {
        PInvoke.MFCreateMediaType(out var type).ThrowOnFailure();
        try
        {
            type.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            type.SetGUID(PInvoke.MF_MT_SUBTYPE, subtype);
            type.SetUINT32(PInvoke.MF_MT_INTERLACE_MODE, (uint)MFVideoInterlaceMode.MFVideoInterlace_Progressive);
            type.SetUINT64(PInvoke.MF_MT_FRAME_SIZE, Pack((uint)width, (uint)height));
            type.SetUINT64(PInvoke.MF_MT_FRAME_RATE, Pack((uint)fps, 1));
            type.SetUINT64(PInvoke.MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
            return type;
        }
        catch
        {
            Marshal.ReleaseComObject(type);
            throw;
        }
    }

    /// <summary>About 0.1 bit per pixel and frame (1080p at 30 fps: 6.2 Mbit/s).</summary>
    private static uint FallbackBitrate(int width, int height, int fps) =>
        (uint)Math.Min(uint.MaxValue, (long)width * height * fps / 10);

    /// <summary>The friendly names of the MFTs of <paramref name="category"/> that match the types.</summary>
    private static unsafe List<string> Mfts(Guid category, MFT_REGISTER_TYPE_INFO? input, MFT_REGISTER_TYPE_INFO? output)
    {
        var hr = PInvoke.MFTEnumEx(category, EnumFlags, input, output, out var activates, out var count);
        if (hr.Failed) throw new COMException($"MFTEnumEx failed: 0x{hr.Value:X8}", hr.Value);
        var names = new List<string>((int)count);
        try
        {
            var key = PInvoke.MFT_FRIENDLY_NAME_Attribute;
            for (var i = 0; i < count; i++)
            {
                PWSTR name = default;
                try
                {
                    activates[i]->GetAllocatedString(&key, &name, out _);
                    names.Add(name.ToString());
                }
                catch (Exception)
                {
                    names.Add("(unnamed)");
                }
                finally
                {
                    if (name.Value != null) PInvoke.CoTaskMemFree(name.Value);
                }
            }
        }
        finally
        {
            for (var i = 0; i < count; i++) activates[i]->Release();
            if (activates != null) PInvoke.CoTaskMemFree(activates);
        }
        return names;
    }

    private static MFT_REGISTER_TYPE_INFO VideoType(Guid subtype) =>
        new() { guidMajorType = PInvoke.MFMediaType_Video, guidSubtype = subtype };

    private static void Startup()
    {
        var hr = PInvoke.MFStartup(PInvoke.MF_VERSION, PInvoke.MFSTARTUP_FULL);
        if (hr.Failed) throw new COMException($"MFStartup failed: 0x{hr.Value:X8}", hr.Value);
    }

    private static long Ticks(double seconds) => (long)Math.Round(seconds * TicksPerSecond);

    private static ulong Pack(uint high, uint low) => (ulong)high << 32 | low;

    /// <summary>
    /// A failed step as a <see cref="COMException"/> with its HRESULT (.NET maps some HRESULTs, such as E_INVALIDARG and
    /// E_NOTIMPL, to other exception types; their HResult keeps the code).
    /// </summary>
    private static COMException Failure(string step, Exception e) =>
        new($"{step} failed: 0x{e.HResult:X8} {e.Message}", e.HResult);
}
