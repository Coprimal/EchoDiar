using System.Runtime.InteropServices;
using EchoDiar.Core.Audio;
using EchoDiar.Platform.Screen;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Storage;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;

namespace EchoDiar.Platform.Tests;

// The video export on the real Media Foundation Source Reader, Sink Writer and AAC encoder (Windows only, CI job
// test-platform): the screen video copied as it is plus the microphone as one mono AAC 48 kHz track, read back through
// the Windows media APIs and a Source Reader.
public sealed class VideoExporterTests : IDisposable
{
    private const int Fps = 15;
    private const int Frames = 30; // 2 s of video
    private const double AudioSeconds = 1.5; // shorter than the video: the audio track keeps its own length
    private const int SampleRate = 16_000;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "EchoDiarPlatformTests-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public VideoExporterTests(ITestOutputHelper output)
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

    // Mic only: one video track with the source's frames (not re-encoded), one mono AAC 48 kHz track as long as the
    // audio, and the export as long as the video.
    [Fact]
    public async Task MicOnlyCopiesTheVideoAndAddsOneMonoAacTrackOfTheAudio()
    {
        var video = WriteVideo();
        var caf = WriteCaf();
        var destination = Path.Combine(_dir, "export.mp4");
        var audio = new VideoExportAudio(new(caf, 0), new(caf, 1));

        await VideoExporter.ExportAsync(video, audio, VideoExportChannels.Mic, destination,
            cancellationToken: TestContext.Current.CancellationToken);

        var source = ReadStreams(video);
        var export = ReadStreams(destination);
        var profile = await MediaEncodingProfile.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(destination));
        var sourceDuration = await Duration(video);
        var exportDuration = await Duration(destination);
        _output.WriteLine($"source: {string.Join("; ", source)}, {sourceDuration:0.0000} s");
        _output.WriteLine($"export: {string.Join("; ", export)}, {exportDuration:0.0000} s");
        _output.WriteLine($"profile: {profile.Video.Subtype}, {profile.Audio.Subtype} {profile.Audio.ChannelCount} ch {profile.Audio.SampleRate} Hz");

        var sourceVideo = Assert.Single(source, s => s.Major == PInvoke.MFMediaType_Video);
        Assert.Equal(2, export.Count);
        var exportVideo = Assert.Single(export, s => s.Major == PInvoke.MFMediaType_Video);
        var exportAudio = Assert.Single(export, s => s.Major == PInvoke.MFMediaType_Audio);
        Assert.Equal(sourceVideo.Subtype, exportVideo.Subtype);
        Assert.Equal(MediaEncodingSubtypes.H264, profile.Video.Subtype, ignoreCase: true);
        Assert.Equal(sourceVideo.Samples, exportVideo.Samples);
        Assert.InRange(exportVideo.End, sourceVideo.End - 1.0 / Fps, sourceVideo.End + 1.0 / Fps);
        Assert.Equal(MediaEncodingSubtypes.Aac, profile.Audio.Subtype, ignoreCase: true);
        Assert.Equal(1u, profile.Audio.ChannelCount);
        Assert.Equal(48_000u, profile.Audio.SampleRate);
        // The encoder's last frames (1024 samples, 21 ms each) may run a little past the audio.
        Assert.InRange(exportAudio.End, AudioSeconds - 0.05, AudioSeconds + 0.1);
        Assert.InRange(exportDuration, sourceDuration - 1.0 / Fps, sourceDuration + 1.0 / Fps);
    }

    /// <summary>A 320x240 H.264 video of <see cref="Frames"/> frames at <see cref="Fps"/>, written by the recorder's encoder.</summary>
    private string WriteVideo()
    {
        const int width = 320, height = 240;
        var path = Path.Combine(_dir, "video.mp4");
        var pixels = new byte[width * 4 * height];
        using var encoder = new VideoEncoder(path, width, height, Fps, 0.75, VideoCodec.H264);
        for (var i = 0; i < Frames; i++)
        {
            Array.Fill(pixels, (byte)(i * 8));
            encoder.WriteFrame(pixels, width * 4, i / (double)Fps, 1.0 / Fps);
        }
        encoder.Finish();
        return path;
    }

    /// <summary><see cref="AudioSeconds"/> of 2-channel Int16 at 16 kHz, as the recorder writes: a 440 Hz tone on ch0 (mic), ch1 silent.</summary>
    private string WriteCaf()
    {
        var path = Path.Combine(_dir, "audio.caf");
        var frames = (int)(AudioSeconds * SampleRate);
        var samples = new short[frames * 2];
        for (var i = 0; i < frames; i++) samples[2 * i] = (short)(8_000 * Math.Sin(2 * Math.PI * 440 * i / SampleRate));
        using var writer = new CafFile.Writer(path, SampleRate, 2, CafFile.SampleFormat.Int16);
        writer.Append(samples);
        return path;
    }

    private static async Task<double> Duration(string path)
    {
        using var source = MediaSource.CreateFromStorageFile(await StorageFile.GetFileFromPathAsync(path));
        await source.OpenAsync();
        return source.Duration?.TotalSeconds ?? 0;
    }

    private sealed record Stream(Guid Major, Guid Subtype, int Samples, double End)
    {
        public override string ToString() =>
            $"{(Major == PInvoke.MFMediaType_Video ? "video" : Major == PInvoke.MFMediaType_Audio ? "audio" : "other")} "
            + $"{Subtype} {Samples} samples to {End:0.0000} s";
    }

    /// <summary>Every stream of the file as stored (no decoder): its types, its sample count and where its last sample ends.</summary>
    private static List<Stream> ReadStreams(string path)
    {
        PInvoke.MFStartup(PInvoke.MF_VERSION, PInvoke.MFSTARTUP_FULL).ThrowOnFailure();
        IMFSourceReader? reader = null;
        try
        {
            PInvoke.MFCreateSourceReaderFromURL(path, null, out reader).ThrowOnFailure();
            List<(Guid Major, Guid Subtype)> types = [];
            for (uint i = 0; ; i++)
            {
                IMFMediaType type;
                try
                {
                    reader.GetCurrentMediaType(i, out type);
                }
                catch (COMException)
                {
                    break; // MF_E_INVALIDSTREAMNUMBER: no more streams
                }
                type.GetGUID(PInvoke.MF_MT_MAJOR_TYPE, out var major);
                type.GetGUID(PInvoke.MF_MT_SUBTYPE, out var subtype);
                Marshal.ReleaseComObject(type);
                types.Add((major, subtype));
                reader.SetStreamSelection(i, true);
            }
            var samples = new int[types.Count];
            var ends = new long[types.Count];
            var anyStream = unchecked((uint)MF_SOURCE_READER_CONSTANTS.MF_SOURCE_READER_ANY_STREAM);
            var open = types.Count;
            while (open > 0)
            {
                reader.ReadSample(anyStream, 0, out var index, out var flags, out var time, out var sample);
                if (sample is not null)
                {
                    sample.GetSampleDuration(out var duration);
                    samples[index]++;
                    ends[index] = Math.Max(ends[index], time + duration);
                    Marshal.ReleaseComObject(sample);
                }
                if (((MF_SOURCE_READER_FLAG)flags).HasFlag(MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ERROR)) throw new COMException("Reading failed");
                if (((MF_SOURCE_READER_FLAG)flags).HasFlag(MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM)) open--;
            }
            return [.. types.Select((t, i) => new Stream(t.Major, t.Subtype, samples[i], ends[i] / 10_000_000.0))];
        }
        finally
        {
            if (reader is not null) Marshal.FinalReleaseComObject(reader);
            PInvoke.MFShutdown();
        }
    }
}
