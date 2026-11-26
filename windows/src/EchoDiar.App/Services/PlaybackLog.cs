using System.Globalization;

namespace EchoDiar.App.Services;

/// <summary>
/// How far the video drifts from the audio, appended to <c>Logs\playback.log</c> under the library root, one line per
/// event: <c>&lt;ISO time&gt; seek|sample|correct drift=&lt;ms&gt; audio=&lt;s&gt; video=&lt;s&gt;</c> (drift = video minus
/// audio). Always on, in Release too (unlike <c>StartupTrace</c>). Past about 1 MB the file is renamed
/// <c>playback.log.1</c> (replacing the older one) and a new one starts. The line is stamped by the caller and written
/// off its thread, in order. Never throws.
/// </summary>
internal static class PlaybackLog
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly Lock Gate = new();

    /// <summary>The previous write: each one runs after it, so the lines keep their order.</summary>
    private static Task _tail = Task.CompletedTask;

    public static string PathIn(string root) => Path.Combine(root, "Logs", "playback.log");

    /// <param name="kind"><c>seek</c>, <c>sample</c> or <c>correct</c>.</param>
    /// <param name="audio">The audio's play head, seconds.</param>
    /// <param name="video">The video's position, seconds.</param>
    public static void Write(string root, string kind, double audio, double video)
    {
        try
        {
            var line = string.Create(CultureInfo.InvariantCulture,
                $"{DateTimeOffset.Now:O} {kind} drift={(int)Math.Round((video - audio) * 1000)} audio={audio:0.000} video={video:0.000}{Environment.NewLine}");
            var path = PathIn(root);
            lock (Gate) _tail = _tail.ContinueWith(_ => Append(path, line), TaskScheduler.Default);
        }
        catch
        {
            // A log line is never worth a failure.
        }
    }

    private static void Append(string path, string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var file = new FileInfo(path);
            if (file.Exists && file.Length > MaxBytes) File.Move(path, path + ".1", overwrite: true);
            File.AppendAllText(path, line);
        }
        catch
        {
            // As above: the next line tries again.
        }
    }
}
