using System.Runtime.InteropServices;
using EchoDiar.Core;
using EchoDiar.Core.Audio;
using EchoDiar.Core.Model;
using EchoDiar.Platform.Audio;
using Windows.Win32;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.Variant;

namespace EchoDiar.Platform.Screen;

/// <summary>Which audio goes into a video export (macOS <c>VideoExporter.Channels</c>).</summary>
public enum VideoExportChannels
{
    Both,
    Mic,
    System,
    None,
}

/// <summary>One channel of an audio file.</summary>
public sealed record VideoExportSource(string Path, int Channel);

/// <summary>
/// A conversation's audio, found on disk (macOS <c>VideoExporter.Audio</c>): the microphone and the system audio, either
/// may be missing.
/// </summary>
public sealed record VideoExportAudio(VideoExportSource? Mic, VideoExportSource? System)
{
    /// <summary>The channels <paramref name="choice"/> takes; null when one of them is missing. Empty for None.</summary>
    public IReadOnlyList<VideoExportSource>? Sources(VideoExportChannels choice) => choice switch
    {
        VideoExportChannels.Both => Mic is not null && System is not null ? [Mic, System] : null,
        VideoExportChannels.Mic => Mic is not null ? [Mic] : null,
        VideoExportChannels.System => System is not null ? [System] : null,
        _ => [],
    };

    /// <summary>
    /// The folder's audio as the transcription finds it (macOS <c>VideoExporter.Audio.find</c>): the session's
    /// <c>audioFileName</c> (<c>audio.caf</c> while not compressed, <c>audio.m4a</c> after); with
    /// <c>audio-mic.m4a</c>, its <c>audio-system.m4a</c> beside it; a stereo file holds ch0 mic and ch1 system, a mono
    /// one is the system audio. Null when there is no audio. Reads the file's header: call off the UI thread.
    /// </summary>
    public static VideoExportAudio? Find(string folder)
    {
        try
        {
            var name = RecordingSession.Read(folder).AudioFileName;
            if (Existing(folder, name) is not { } path) return null;
            if (name == AudioCompressor.MicFileName)
            {
                var system = Existing(folder, AudioCompressor.SystemFileName);
                return new(new(path, 0), system is null ? null : new(system, 0));
            }
            return ChannelCount(path) switch
            {
                0 => null,
                1 => new(null, new(path, 0)),
                _ => new(new(path, RecordingSession.Channel.Mic), new(path, RecordingSession.Channel.System)),
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException
            or global::System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string? Existing(string folder, string name) =>
        ConversationFiles.AudioPath(folder, name) is { } path && File.Exists(path) ? path : null;

    private static int ChannelCount(string path)
    {
        if (!Path.GetExtension(path).Equals(".caf", StringComparison.OrdinalIgnoreCase)) return new MediaFoundationAudio().ChannelCount(path);
        using var caf = new CafFile.Reader(path);
        return caf.Channels;
    }
}

/// <summary>
/// Writes a conversation's screen recording with its audio into one mp4 (macOS <c>VideoExporter</c>): the video track
/// copied as it is (no re-encoding: same codec, H.264 or HEVC, same frames and times, so a video that started late
/// still starts late), plus one mono AAC-LC 48 kHz track of the microphone, the system audio, both mixed, or no audio
/// track. Video time is audio-file time, so both tracks start at zero; audio past the video's end is left out, so the
/// export lasts as long as the video. Errors are <see cref="COMException"/>s whose message names the step and its
/// HRESULT, or <see cref="AudioEncodeException"/>s from the audio.
/// </summary>
/// <remarks>
/// The audio is first encoded to a temp m4a (<see cref="MediaFoundationAudio.EncodeMonoAacMix"/>), then both
/// compressed streams are copied sample by sample through a Media Foundation Source Reader into a Sink Writer that has
/// no encoder (its input type is its output type).
/// </remarks>
public static class VideoExporter
{
    private static readonly uint FirstVideoStream = unchecked((uint)MF_SOURCE_READER_CONSTANTS.MF_SOURCE_READER_FIRST_VIDEO_STREAM);
    private static readonly uint FirstAudioStream = unchecked((uint)MF_SOURCE_READER_CONSTANTS.MF_SOURCE_READER_FIRST_AUDIO_STREAM);
    private static readonly uint AllStreams = unchecked((uint)MF_SOURCE_READER_CONSTANTS.MF_SOURCE_READER_ALL_STREAMS);
    private static readonly uint MediaSource = unchecked((uint)MF_SOURCE_READER_CONSTANTS.MF_SOURCE_READER_MEDIASOURCE);

    /// <summary>
    /// Writes <paramref name="video"/> plus the <paramref name="channels"/> of <paramref name="audio"/> to
    /// <paramref name="destination"/> (an mp4, replaced when it exists, but never one of the files read:
    /// <see cref="ArgumentException"/>), off the calling thread. Works in a hidden temp file next to the destination
    /// (and a temp m4a in the temp folder) that is moved into place at the end and removed on failure or cancellation.
    /// <paramref name="progress"/> gets 0..1 from a worker thread.
    /// </summary>
    public static Task ExportAsync(
        string video, VideoExportAudio? audio, VideoExportChannels channels, string destination,
        Action<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var target = Path.GetFullPath(destination);
        string?[] inputs = [video, audio?.Mic?.Path, audio?.System?.Path];
        if (inputs.OfType<string>().Any(input => string.Equals(Path.GetFullPath(input), target, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("The destination is one of the files being read", nameof(destination));
        }
        var sources = audio is null ? channels == VideoExportChannels.None ? [] : null : audio.Sources(channels);
        if (sources is null) throw new ArgumentException($"The conversation has no {channels} audio", nameof(channels));
        return Task.Run(() => Export(video, sources, target, progress, cancellationToken), cancellationToken);
    }

    private static void Export(
        string video, IReadOnlyList<VideoExportSource> sources, string destination, Action<double>? progress,
        CancellationToken cancellationToken)
    {
        var folder = Path.GetDirectoryName(destination) ?? throw new ArgumentException("No folder", nameof(destination));
        var temp = Path.Combine(folder, $".{Path.GetFileNameWithoutExtension(destination)}-{Guid.NewGuid():N}.mp4");
        var tempAudio = sources.Count == 0 ? null : Path.Combine(Path.GetTempPath(), $"EchoDiar-export-{Guid.NewGuid():N}.m4a");
        try
        {
            // The audio first (half the progress), then the copy.
            var audioShare = tempAudio is null ? 0 : 0.5;
            if (tempAudio is not null)
            {
                MediaFoundationAudio.EncodeMonoAacMix(
                    [.. sources.Select(s => (s.Path, s.Channel))], tempAudio, done => progress?.Invoke(done * audioShare),
                    cancellationToken);
            }
            Remux(video, tempAudio, temp, done => progress?.Invoke(audioShare + done * (1 - audioShare)), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, destination, overwrite: true);
            progress?.Invoke(1);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        finally
        {
            if (tempAudio is not null) TryDelete(tempAudio);
        }
    }

    /// <summary>
    /// Copies the first video stream of <paramref name="video"/> and the first audio stream of <paramref name="audio"/>
    /// (when given) into <paramref name="output"/>, interleaved by time, without decoding; audio from the video's end
    /// on is dropped.
    /// </summary>
    private static void Remux(string video, string? audio, string output, Action<double> progress, CancellationToken cancellationToken)
    {
        Startup();
        IMFAttributes? attributes = null;
        IMFSinkWriter? writer = null;
        Track? videoTrack = null;
        Track? audioTrack = null;
        var step = "MFCreateAttributes";
        try
        {
            PInvoke.MFCreateAttributes(out attributes, 1).ThrowOnFailure();
            attributes.SetUINT32(PInvoke.MF_SINK_WRITER_DISABLE_THROTTLING, 1);
            step = "MFCreateSinkWriterFromURL";
            PInvoke.MFCreateSinkWriterFromURL(output, null, attributes, out writer).ThrowOnFailure();

            step = "video source";
            videoTrack = Track.Open(video, FirstVideoStream, writer);
            var duration = videoTrack.Duration();
            if (audio is not null)
            {
                step = "audio source";
                audioTrack = Track.Open(audio, FirstAudioStream, writer);
            }
            step = "IMFSinkWriter.BeginWriting";
            writer.BeginWriting();

            step = "first samples";
            videoTrack.Next();
            audioTrack?.Next();
            var reported = -1.0;
            while (!videoTrack.Ended || audioTrack is { Ended: false })
            {
                cancellationToken.ThrowIfCancellationRequested();
                // The earlier of the two; audio from the video's end on is left out.
                if (audioTrack is { Ended: false } && duration > 0 && audioTrack.Time >= duration)
                {
                    audioTrack.End();
                    continue;
                }
                var track = audioTrack is { Ended: false } && (videoTrack.Ended || audioTrack.Time < videoTrack.Time)
                    ? audioTrack
                    : videoTrack;
                step = track == videoTrack ? "video sample" : "audio sample";
                track.WriteAndNext();
                if (duration > 0 && !videoTrack.Ended)
                {
                    var done = Math.Min(1, videoTrack.Time / (double)duration);
                    if (done - reported >= 0.01)
                    {
                        reported = done;
                        progress(Math.Min(done, 0.99));
                    }
                }
            }
            step = "IMFSinkWriter.Finalize";
            writer.Finalize();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw Failure(step, e);
        }
        finally
        {
            audioTrack?.Dispose();
            videoTrack?.Dispose();
            if (writer is not null) Marshal.FinalReleaseComObject(writer);
            if (attributes is not null) Marshal.ReleaseComObject(attributes);
            PInvoke.MFShutdown();
        }
    }

    /// <summary>
    /// One compressed stream read from a file and its stream in the sink writer, with the next sample held until it is
    /// written.
    /// </summary>
    private sealed class Track : IDisposable
    {
        private readonly IMFSourceReader _reader;
        private readonly uint _source;
        private readonly IMFSinkWriter _writer;
        private readonly uint _stream;
        private IMFSample? _sample;

        private Track(IMFSourceReader reader, uint source, IMFSinkWriter writer, uint stream)
        {
            _reader = reader;
            _source = source;
            _writer = writer;
            _stream = stream;
        }

        /// <summary>The held sample's time, in 100 ns.</summary>
        public long Time { get; private set; }

        public bool Ended { get; private set; }

        /// <summary>
        /// Reads <paramref name="stream"/> of <paramref name="path"/> as stored (no decoder: its current type is its
        /// native one) and adds it to <paramref name="writer"/> with that type in and out, so nothing is encoded.
        /// </summary>
        public static Track Open(string path, uint stream, IMFSinkWriter writer)
        {
            IMFSourceReader? reader = null;
            IMFMediaType? type = null;
            var step = "MFCreateSourceReaderFromURL";
            try
            {
                PInvoke.MFCreateSourceReaderFromURL(path, null, out reader).ThrowOnFailure();
                step = "IMFSourceReader.SetStreamSelection";
                reader.SetStreamSelection(AllStreams, false);
                reader.SetStreamSelection(stream, true);
                step = "IMFSourceReader.GetCurrentMediaType";
                reader.GetCurrentMediaType(stream, out type);
                step = "IMFSinkWriter.AddStream";
                writer.AddStream(type, out var index);
                step = "IMFSinkWriter.SetInputMediaType";
                writer.SetInputMediaType(index, type, null);
                var track = new Track(reader, stream, writer, index);
                reader = null;
                return track;
            }
            catch (Exception e)
            {
                throw Failure($"{Path.GetFileName(path)}: {step}", e);
            }
            finally
            {
                if (type is not null) Marshal.ReleaseComObject(type);
                if (reader is not null) Marshal.FinalReleaseComObject(reader);
            }
        }

        /// <summary>The file's length (MF_PD_DURATION) in 100 ns; 0 when it does not say.</summary>
        public long Duration()
        {
            _reader.GetPresentationAttribute(MediaSource, PInvoke.MF_PD_DURATION, out var value);
            try
            {
                return value.vt == VARENUM.VT_UI8 ? (long)Math.Min(value.uhVal, long.MaxValue) : 0;
            }
            finally
            {
                PInvoke.PropVariantClear(ref value);
            }
        }

        /// <summary>Holds the next sample, or ends; a gap in the stream is passed on as a stream tick.</summary>
        public void Next()
        {
            while (!Ended)
            {
                _reader.ReadSample(_source, 0, out _, out var flags, out var time, out var sample);
                var status = (MF_SOURCE_READER_FLAG)flags;
                if (status.HasFlag(MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ERROR))
                {
                    if (sample is not null) Marshal.ReleaseComObject(sample);
                    throw new COMException("The source reader failed");
                }
                if (status.HasFlag(MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_STREAMTICK)) _writer.SendStreamTick(_stream, time);
                if (sample is not null)
                {
                    _sample = sample;
                    Time = time;
                    return;
                }
                if (status.HasFlag(MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM)) Ended = true;
            }
        }

        /// <summary>Writes the held sample as it was read (its times, key frame and decode time kept), then holds the next.</summary>
        public void WriteAndNext()
        {
            if (_sample is { } sample)
            {
                _sample = null;
                try
                {
                    _writer.WriteSample(_stream, sample);
                }
                finally
                {
                    Marshal.ReleaseComObject(sample);
                }
            }
            Next();
        }

        /// <summary>Stops here: the held sample and the rest are not written.</summary>
        public void End()
        {
            Release();
            Ended = true;
        }

        public void Dispose()
        {
            Release();
            Marshal.FinalReleaseComObject(_reader);
        }

        private void Release()
        {
            if (_sample is not null) Marshal.ReleaseComObject(_sample);
            _sample = null;
        }
    }

    private static void Startup()
    {
        var hr = PInvoke.MFStartup(PInvoke.MF_VERSION, PInvoke.MFSTARTUP_FULL);
        if (hr.Failed) throw new COMException($"MFStartup failed: 0x{hr.Value:X8}", hr.Value);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A leftover temp file is harmless.
        }
    }

    /// <summary>A failed step as a <see cref="COMException"/> with its HRESULT (as <see cref="VideoEncoder"/>).</summary>
    private static COMException Failure(string step, Exception e) =>
        e is COMException { Message: var message } com && message.Contains(" failed: 0x", StringComparison.Ordinal)
            ? new($"{step}: {message}", com.HResult)
            : new($"{step} failed: 0x{e.HResult:X8} {e.Message}", e.HResult);
}
