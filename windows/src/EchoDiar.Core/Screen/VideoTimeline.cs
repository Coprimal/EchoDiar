namespace EchoDiar.Core.Screen;

/// <summary>
/// Decides which video frames are written and at which audio file time, in seconds (paused spans removed, as
/// <c>RecordingEngine.FileTime</c>). Port of the timing rules of macOS <c>ScreenRecorder.swift</c> (placeholder,
/// hold, stop pad) for an encoder that needs each sample's duration when it is written.
/// </summary>
/// <remarks>
/// The timeline always holds one picture, the last accepted one, not written yet: at first the placeholder at 0, so
/// the video starts at 0. Each call returns the held picture's sample to write now (time, and duration up to the
/// incoming picture's time) and whether the incoming picture is held instead. The caller keeps the held picture's
/// pixels and writes them with the returned timing:
/// <code>
/// var step = timeline.Frame(t);
/// if (step.Write is { } s) Write(held, s.Time, s.Duration);
/// if (step.Holds) held = frame;
/// </code>
/// Rules:
/// <list type="bullet">
/// <item>A null (paused), negative (before the start) or earlier-than-held time drops the frame.</item>
/// <item>A real frame closer than one frame interval to a held real frame is dropped. After a placeholder, any later
/// time is taken (macOS <c>ScreenVideoWriter.append</c> only needs a later time).</item>
/// <item>A real frame at the held placeholder's time, or a placeholder at or before the held real frame's time,
/// takes the held picture's slot: nothing is written, so no sample has a zero duration.</item>
/// <item><see cref="Stop"/> returns the held picture with its duration ending at the stop time (one frame interval
/// when the stop is not later), so the video ends with the audio. Calls after it do nothing.</item>
/// </list>
/// Not thread-safe: the owner calls it from one thread at a time.
/// </remarks>
public sealed class VideoTimeline
{
    /// <summary>A sample to write: file time and duration, in seconds.</summary>
    public readonly record struct Sample(double Time, double Duration);

    /// <summary>
    /// <paramref name="Holds"/>: the incoming picture is now held (its pixels kept); <paramref name="Write"/>: the
    /// previously held picture's sample to write now, if any.
    /// </summary>
    public readonly record struct Step(bool Holds, Sample? Write);

    private static readonly Step Drop = new(false, null);

    private double _heldTime;
    private bool _heldIsPlaceholder = true;
    private bool _stopped;

    public VideoTimeline(int fps)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fps);
        FrameInterval = 1.0 / fps;
    }

    /// <summary>The minimum time between two written real frames, in seconds.</summary>
    public double FrameInterval { get; }

    /// <summary>A real frame at <paramref name="fileTime"/> (null while paused).</summary>
    public Step Frame(double? fileTime)
    {
        if (_stopped || fileTime is not { } time || time < 0 || time < _heldTime) return Drop;
        if (!_heldIsPlaceholder && time < _heldTime + FrameInterval) return Drop;
        return Hold(time, placeholder: false);
    }

    /// <summary>The screen turned off at <paramref name="fileTime"/>: the placeholder holds until the next real frame.</summary>
    public Step Placeholder(double fileTime)
    {
        if (_stopped || _heldIsPlaceholder) return Drop;
        return Hold(Math.Max(fileTime, _heldTime), placeholder: true);
    }

    /// <summary>The held picture's last sample, ending at <paramref name="fileTime"/>; null once stopped.</summary>
    public Sample? Stop(double fileTime)
    {
        if (_stopped) return null;
        _stopped = true;
        return new Sample(_heldTime, fileTime > _heldTime ? fileTime - _heldTime : FrameInterval);
    }

    private Step Hold(double time, bool placeholder)
    {
        Sample? write = time > _heldTime ? new Sample(_heldTime, time - _heldTime) : null;
        _heldTime = time;
        _heldIsPlaceholder = placeholder;
        return new Step(true, write);
    }
}
