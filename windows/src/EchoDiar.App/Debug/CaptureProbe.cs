#if DEBUG
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using EchoDiar.Core.Audio;
using EchoDiar.Core.Models;
using EchoDiar.Core.Screen;
using EchoDiar.Platform.Audio;
using EchoDiar.Platform.Screen;
using EchoDiar.Transcribe;
using EchoDiar.Transcribe.Engines;
using LLama.Native;
using Microsoft.UI.Dispatching;

namespace EchoDiar.App;

/// <summary>
/// Debug-only headless harnesses for the VM gates: <c>--capture-probe</c> records the mic and system sources to CAF
/// files, <c>--native-probe</c> loads every native engine once, <c>--screen-probe</c> records a screen video with the
/// real recording engine (also started by <c>Probes\screen-probe.args</c>). Each writes its result JSON under
/// <c>Probes\</c> and the app then exits without a window.
/// </summary>
internal static class CaptureProbe
{
    private const string CaptureFlag = "--capture-probe";
    private const string NativeFlag = "--native-probe";
    private const string ScreenFlag = "--screen-probe";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Starts the probe named in <paramref name="commandLine"/>, if any, and calls <paramref name="exit"/> on this
    /// thread when it ends. A command line without any <c>--</c> flag runs <c>--screen-probe</c> with the arguments of
    /// <c>Probes\screen-probe.args</c> when that file exists (deleted as it is read).
    /// </summary>
    public static bool TryRun(string[] commandLine, Action exit)
    {
        var index = Array.FindIndex(commandLine, arg => arg is CaptureFlag or NativeFlag or ScreenFlag);
        string[] rest;
        Func<string[], Task> probe;
        if (index >= 0)
        {
            rest = commandLine[(index + 1)..];
            probe = commandLine[index] switch
            {
                CaptureFlag => RunCaptureAsync,
                NativeFlag => RunNativeAsync,
                _ => args => RunScreenAsync(args, []),
            };
        }
        else if (!commandLine.Skip(1).Any(arg => arg.StartsWith("--", StringComparison.Ordinal)) && ReadArgsFile() is { } file)
        {
            rest = file.Args;
            probe = args => RunScreenAsync(args, file.Errors);
        }
        else
        {
            return false;
        }

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        _ = Task.Run(async () =>
        {
            try
            {
                await probe(rest);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Probe failed: {ex}");
            }
            finally
            {
                dispatcher.TryEnqueue(() => exit());
            }
        });
        return true;
    }

    private static string ProbesDir => Path.Combine(App.DataRoot, "Probes");

    private static string Timestamp() => DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    private static string Describe(string label, Exception ex) =>
        $"{label}: {ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message}";

    // --capture-probe <seconds> <all|pid:N|exe:name.exe>

    private static async Task RunCaptureAsync(string[] args)
    {
        var dir = Path.Combine(ProbesDir, Timestamp());
        Directory.CreateDirectory(dir);
        var errors = new ErrorList();
        var mic = new ProbeChannel(Path.Combine(dir, "mic.caf"));
        var system = new ProbeChannel(Path.Combine(dir, "system.caf"));
        var mode = "none";
        MicCapture? micCapture = null;
        LoopbackCapture? loopback = null;
        try
        {
            if (args.Length < 2 || !int.TryParse(args[0], CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
            {
                errors.Add($"usage: {CaptureFlag} <seconds> <all|pid:N|exe:name.exe>");
                return;
            }

            micCapture = StartMic(mic, errors);
            (loopback, mode) = StartSystem(args[1], system, errors);
            await Task.Delay(TimeSpan.FromSeconds(seconds));
        }
        finally
        {
            StopAndFinish("mic", micCapture is null ? null : micCapture.Dispose, mic, errors);
            StopAndFinish("system", loopback is null ? null : loopback.Dispose, system, errors);
            var result = new
            {
                micRate = mic.Rate,
                micFormat = mic.Format,
                systemRate = system.Rate,
                micFrames = mic.Frames,
                systemFrames = system.Frames,
                micPeak = mic.Peak,
                systemPeak = system.Peak,
                mode,
                firstMicQpc = mic.FirstQpc,
                firstSystemQpc = system.FirstQpc,
                errors = errors.ToArray(),
            };
            await File.WriteAllTextAsync(Path.Combine(dir, "probe-result.json"), JsonSerializer.Serialize(result, JsonOptions));
        }
    }

    private static MicCapture? StartMic(ProbeChannel channel, ErrorList errors)
    {
        var capture = new MicCapture();
        try
        {
            capture.Failed += ex => errors.Add(Describe("mic failed", ex));
            capture.Start(channel.OnSamples);
            channel.Rate = capture.SampleRate;
            channel.Format = capture.Format;
            return capture;
        }
        catch (Exception ex)
        {
            errors.Add(Describe("mic", ex));
            capture.Dispose();
            return null;
        }
    }

    private static (LoopbackCapture? Capture, string Mode) StartSystem(string target, ProbeChannel channel, ErrorList errors)
    {
        Func<LoopbackCapture> create;
        string mode;
        if (target == "all")
        {
            create = () => LoopbackCapture.AllExcept(Environment.ProcessId);
            mode = "all";
        }
        else if (target.StartsWith("pid:", StringComparison.Ordinal) && int.TryParse(target[4..], CultureInfo.InvariantCulture, out var pid))
        {
            create = () => LoopbackCapture.ForProcessTree(pid);
            mode = "pid";
        }
        else if (target.StartsWith("exe:", StringComparison.Ordinal))
        {
            var root = FindProcess(target[4..]);
            if (root is null)
            {
                errors.Add($"system: no running process named {target[4..]}");
                return (null, "none");
            }

            create = () => LoopbackCapture.ForProcessTree(root.Value);
            mode = "exe";
        }
        else
        {
            errors.Add($"system: unknown target {target} (all|pid:N|exe:name.exe)");
            return (null, "none");
        }

        if (StartLoopback(create, "system", channel, errors) is { } capture) return (capture, mode);
        if (StartLoopback(LoopbackCapture.Endpoint, "system endpoint fallback", channel, errors) is { } fallback)
        {
            return (fallback, "endpoint-fallback");
        }

        return (null, "none");
    }

    private static LoopbackCapture? StartLoopback(Func<LoopbackCapture> create, string label, ProbeChannel channel, ErrorList errors)
    {
        LoopbackCapture? capture = null;
        try
        {
            capture = create();
            capture.Failed += ex => errors.Add(Describe($"{label} failed", ex));
            capture.Start(channel.OnSamples);
            channel.Rate = capture.SampleRate;
            return capture;
        }
        catch (Exception ex)
        {
            errors.Add(Describe(label, ex));
            capture?.Dispose();
            return null;
        }
    }

    /// <summary>The earliest started process with this exe name: the likely root of a multi-process app.</summary>
    private static int? FindProcess(string exeName)
    {
        var name = Path.GetFileNameWithoutExtension(exeName);
        var processes = System.Diagnostics.Process.GetProcessesByName(name);
        try
        {
            return processes
                .OrderBy(p => StartTimeOrMax(p))
                .Select(p => (int?)p.Id)
                .FirstOrDefault();
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    private static DateTime StartTimeOrMax(System.Diagnostics.Process process)
    {
        try
        {
            return process.StartTime;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return DateTime.MaxValue;
        }
    }

    private static void StopAndFinish(string label, Action? dispose, ProbeChannel channel, ErrorList errors)
    {
        try
        {
            dispose?.Invoke();
        }
        catch (Exception ex)
        {
            errors.Add(Describe($"{label} stop", ex));
        }

        try
        {
            channel.Finish();
        }
        catch (Exception ex)
        {
            errors.Add(Describe($"{label} file", ex));
        }
    }

    /// <summary>One source's mono Float32 CAF file and stats. Written on its capture thread, read after Stop joins it.</summary>
    private sealed class ProbeChannel(string path)
    {
        private CafFile.Writer? _writer;

        public int? Rate { get; set; }
        public string? Format { get; set; }
        public long Frames { get; private set; }
        public float Peak { get; private set; }
        public long? FirstQpc { get; private set; }

        public void OnSamples(ReadOnlySpan<float> samples, long qpc100ns, int sampleRate)
        {
            if (_writer is null)
            {
                _writer = new CafFile.Writer(path, sampleRate, 1, CafFile.SampleFormat.Float32);
                FirstQpc = qpc100ns;
            }

            _writer.Append(samples);
            Frames += samples.Length;
            foreach (var sample in samples)
            {
                var magnitude = Math.Abs(sample);
                if (magnitude > Peak) Peak = magnitude; // NaN never wins, so the JSON stays valid
            }
        }

        /// <summary>Patches the data size; a started source that sent nothing still gets an empty file.</summary>
        public void Finish()
        {
            if (_writer is null && Rate is { } rate)
            {
                _writer = new CafFile.Writer(path, rate, 1, CafFile.SampleFormat.Float32);
            }

            _writer?.Finish();
            _writer?.Dispose();
            _writer = null;
        }
    }

    /// <summary>Errors from the probe thread and the capture threads' Failed events.</summary>
    private sealed class ErrorList
    {
        private readonly List<string> _items = [];

        public void Add(string error)
        {
            lock (_items) _items.Add(error);
        }

        public string[] ToArray()
        {
            lock (_items) return [.. _items];
        }
    }

    // --screen-probe --target display|window:<title>|area:x,y,w,h --seconds N [--start-at S] [--screen-off-at S]
    //   [--screen-on-at S [--on-target display|window:<title>|area:x,y,w,h]] [--fps N] [--quality Q]
    // or the same arguments one per line in Probes\screen-probe.args (an optional first line --screen-probe).

    private const string ScreenUsage = $"usage: {ScreenFlag} --target display|window:<title>|area:x,y,w,h --seconds N " +
        "[--start-at S] [--screen-off-at S] [--screen-on-at S [--on-target <target>]] [--fps N] [--quality Q]";

    private static readonly string[] ScreenOptionNames =
        ["--target", "--seconds", "--start-at", "--screen-off-at", "--screen-on-at", "--on-target", "--fps", "--quality"];

    private sealed record ScreenOptions(
        string Target, double Seconds, double StartAt, double? OffAt, double? OnAt, string OnTarget, int Fps, double Quality);

    private static string ArgsFile => Path.Combine(ProbesDir, "screen-probe.args");

    /// <summary>
    /// The arguments of <see cref="ArgsFile"/> (UTF-8, one per line, empty lines ignored), null when there is none. The
    /// file is deleted even when it cannot be read, so a leftover never takes over a later launch.
    /// </summary>
    private static (string[] Args, string[] Errors)? ReadArgsFile()
    {
        var path = ArgsFile;
        if (!File.Exists(path)) return null;
        string[] args = [];
        var errors = new List<string>();
        try
        {
            args = [.. File.ReadAllLines(path, System.Text.Encoding.UTF8).Select(line => line.Trim()).Where(line => line.Length > 0)];
            if (args.Length > 0 && args[0] == ScreenFlag) args = args[1..];
        }
        catch (Exception ex)
        {
            errors.Add(Describe("args file", ex));
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            errors.Add(Describe("args file delete", ex));
        }

        return (args, [.. errors]);
    }

    /// <summary>
    /// Records <c>--seconds</c> with the real <see cref="RecordingEngine"/> (all system audio, default mic) and a
    /// <see cref="ScreenRecorder"/> on its file clock, issuing start/off/on/stop at their seconds after the engine
    /// started, and writes <c>result.json</c> in <c>Probes\screen-&lt;stamp&gt;\</c> whatever fails. Each event logs
    /// the engine's file time read as it is issued; off and stop pass that same time to the recorder (start and on read
    /// their own a moment later). A late start keeps the recorder's leading placeholder from 0. When the engine cannot
    /// start, the events and the video run on a probe clock (seconds since then) so capture and encoding are still
    /// measured. <c>videoSeconds</c> is the stop file time the last picture is padded to (null when the stop failed or
    /// the video ended early), not read back from the file; <c>audioSeconds</c> is the engine's final duration: the audio
    /// stops right after the stop time is read and before the video is finalized, so the two should match.
    /// </summary>
    private static async Task RunScreenAsync(string[] args, string[] readErrors)
    {
        var dir = Path.Combine(ProbesDir, $"screen-{Timestamp()}");
        Directory.CreateDirectory(dir);
        var errors = new ErrorList();
        foreach (var error in readErrors) errors.Add(error);
        var events = new List<object>();
        var videoPath = Path.Combine(dir, "video.mp4");
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var cpuStart = process.TotalProcessorTime;
        bool? wgcSupported = null;
        VideoCodecSupport? support = null;
        RecordingEngine? engine = null;
        string? sessionFolder = null;
        string? audioPath = null;
        double? audioSeconds = null;
        double? stopTime = null;
        var stopped = false;
        var recorderStarted = false;
        var ended = 0;
        var clock = "none";
        Func<long, double?> fileTime = static _ => null;
        var recorder = new ScreenRecorder();
        recorder.Ended += reason =>
        {
            Volatile.Write(ref ended, 1);
            errors.Add($"recorder ended early: {reason}");
        };

        double? Issue(string kind)
        {
            var time = fileTime(Now());
            events.Add(new { kind, requestedAt = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture), fileTime = time });
            return time;
        }

        try
        {
            try
            {
                wgcSupported = GraphicsCapture.IsSupported();
            }
            catch (Exception ex)
            {
                errors.Add(Describe("wgc", ex));
            }

            try
            {
                support = VideoEncoder.Detect();
            }
            catch (Exception ex)
            {
                errors.Add(Describe("encoders", ex));
            }

            if (ParseScreen(args, errors) is not { } options) return;
            if (ResolveTarget(options.Target, errors) is not { } source) return;

            engine = new RecordingEngine(_ => LoopbackCapture.AllExcept(Environment.ProcessId), id => new MicCapture(id));
            engine.SnapshotChanged += snapshot =>
            {
                if (snapshot.EndReason is { } reason && reason != RecordingEngine.EndReason.User) errors.Add($"engine ended: {reason}");
            };
            try
            {
                sessionFolder = engine.Start(CaptureTarget.Global, "auto", null, dir);
                fileTime = engine.FileTime;
                clock = "engine";
            }
            catch (Exception ex)
            {
                errors.Add(Describe("engine", ex));
                errors.Add("engine did not start: event and video file times are seconds of the probe clock");
                var origin = Now();
                fileTime = qpc => (qpc - origin) / (double)TimeSpan.TicksPerSecond;
                clock = "probe";
            }

            var wall = System.Diagnostics.Stopwatch.StartNew();
            await Until(wall, options.StartAt);
            Issue("start");
            try
            {
                await recorder.StartAsync(source, options.Fps, options.Quality, showsCursor: true, showsClicks: false, videoPath,
                    RecordingEngine.MinimumFreeBytes, fileTime, "Screen recording off");
                recorderStarted = true;
            }
            catch (Exception ex)
            {
                errors.Add(Describe("start", ex));
            }

            if (options.OffAt is { } offAt)
            {
                await Until(wall, offAt);
                var time = Issue("off");
                try
                {
                    await recorder.ScreenOffAsync(time);
                }
                catch (Exception ex)
                {
                    errors.Add(Describe("off", ex));
                }
            }

            if (options.OnAt is { } onAt)
            {
                await Until(wall, onAt);
                if (ResolveTarget(options.OnTarget, errors) is { } onSource)
                {
                    Issue("on");
                    try
                    {
                        await recorder.ScreenOnAsync(onSource);
                    }
                    catch (Exception ex)
                    {
                        errors.Add(Describe("on", ex));
                    }
                }
            }

            await Until(wall, options.Seconds);
        }
        catch (Exception ex)
        {
            errors.Add(Describe("probe", ex));
        }
        finally
        {
            if (engine is not null)
            {
                // The audio stops right after the stop time is read, before the video is finalized, so the audio does
                // not run on while the recorder tears down; the explicit time keeps the video's end independent of it.
                stopTime = Issue("stop");
                try
                {
                    if (engine.Stop() is { } session && sessionFolder is not null)
                    {
                        audioPath = Path.Combine(sessionFolder, session.AudioFileName);
                        audioSeconds = engine.CurrentSnapshot().Elapsed;
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(Describe("engine stop", ex));
                }

                try
                {
                    await recorder.StopAsync(stopTime);
                    stopped = true;
                }
                catch (Exception ex)
                {
                    errors.Add(Describe("stop", ex));
                }
            }

            long? bytes = null;
            try
            {
                if (File.Exists(videoPath)) bytes = new FileInfo(videoPath).Length;
            }
            catch (Exception ex)
            {
                errors.Add(Describe("video size", ex));
            }

            process.Refresh();
            var stats = recorder.Stats;
            // A start that failed after the device was made reports none: make one here, so the gate still sees it.
            var deviceKind = stats.DeviceKind;
            if (deviceKind is null)
            {
                try
                {
                    using var device = CaptureDevice.Create();
                    deviceKind = device.Kind;
                }
                catch (Exception ex)
                {
                    errors.Add(Describe("d3d", ex));
                }
            }

            var result = new
            {
                args,
                sessionId = process.SessionId,
                wgcSupported,
                d3dDevice = deviceKind?.ToString().ToLowerInvariant(),
                encodersFound = support?.EncodersFound,
                hevcEncoderFound = support?.HevcEncoderFound,
                hevcDecoderFound = support?.HevcDecoderFound,
                codec = stats.Codec?.ToString().ToLowerInvariant(),
                width = stats.Width,
                height = stats.Height,
                qualityError = stats.QualityError,
                fileClock = clock,
                events,
                frames = stats.FramesWritten,
                placeholders = stats.PlaceholdersWritten,
                latencySamples = stats.LatencySamples,
                latencyMs = stats.LatencyMs is { } latency ? new { median = latency.Median, max = latency.Max, min = latency.Min } : null,
                videoSeconds = recorderStarted && stopped && Volatile.Read(ref ended) == 0 ? stopTime : null,
                audioSeconds,
                videoPath = bytes is null ? null : videoPath,
                audioPath,
                bytes,
                cpuSeconds = (process.TotalProcessorTime - cpuStart).TotalSeconds,
                lastError = stats.LastError,
                sessions = stats.Sessions.Select(session => new
                {
                    kind = session.Kind.ToString().ToLowerInvariant(),
                    startedMs = session.StartedMs,
                    arrivals = session.Arrivals,
                    firstArrivalMs = session.FirstArrivalMs,
                    firstFrame = session.FirstFrame is { } frame
                        ? new
                        {
                            meanR = frame.MeanR,
                            meanG = frame.MeanG,
                            meanB = frame.MeanB,
                            minAlpha = frame.MinAlpha,
                            maxAlpha = frame.MaxAlpha,
                            samples = frame.Samples,
                            black = frame.Black,
                        }
                        : null,
                    sampleError = session.SampleError,
                    restarted = session.Restarted,
                }).ToArray(),
                errors = errors.ToArray(),
            };
            await File.WriteAllTextAsync(Path.Combine(dir, "result.json"), JsonSerializer.Serialize(result, JsonOptions));
        }
    }

    /// <summary>The options, or null after adding the problem and the usage to <paramref name="errors"/>.</summary>
    private static ScreenOptions? ParseScreen(string[] args, ErrorList errors)
    {
        var values = new Dictionary<string, string>();
        for (var i = 0; i < args.Length; i += 2)
        {
            if (!ScreenOptionNames.Contains(args[i])) return Invalid($"unknown argument {args[i]}");
            if (i + 1 >= args.Length) return Invalid($"{args[i]} needs a value");
            values[args[i]] = args[i + 1];
        }

        // NaN for a value that is not a number: every range check below fails on it.
        double? Number(string name) => values.TryGetValue(name, out var value)
            ? double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : double.NaN
            : null;

        if (!values.TryGetValue("--target", out var target)) return Invalid("--target is required");
        if (Number("--seconds") is not { } seconds || !(seconds > 0)) return Invalid("--seconds must be a number above 0");
        var startAt = Number("--start-at") ?? 0;
        if (!(startAt >= 0 && startAt < seconds)) return Invalid("--start-at must be at least 0 and below --seconds");
        var offAt = Number("--screen-off-at");
        if (offAt is { } off && !(off > startAt && off < seconds)) return Invalid("--screen-off-at must be after --start-at and below --seconds");
        var onAt = Number("--screen-on-at");
        if (onAt is { } on && !(offAt is { } before && on > before && on < seconds))
        {
            return Invalid("--screen-on-at needs --screen-off-at before it and must be below --seconds");
        }
        if (onAt is null && values.ContainsKey("--on-target")) return Invalid("--on-target needs --screen-on-at");
        var fps = 15;
        if (values.TryGetValue("--fps", out var fpsText)
            && !(int.TryParse(fpsText, CultureInfo.InvariantCulture, out fps) && fps > 0))
        {
            return Invalid("--fps must be a whole number above 0");
        }
        var quality = Number("--quality") ?? 0.75;
        if (!(quality >= 0 && quality <= 1)) return Invalid("--quality must be between 0 and 1");

        return new ScreenOptions(target, seconds, startAt, offAt, onAt, values.GetValueOrDefault("--on-target", target), fps, quality);

        ScreenOptions? Invalid(string problem)
        {
            errors.Add(problem);
            errors.Add(ScreenUsage);
            return null;
        }
    }

    /// <summary>
    /// <c>display</c>: the primary monitor; <c>window:&lt;title&gt;</c>: the first visible top-level window (Z order)
    /// whose title contains the text, case-insensitive; <c>area:x,y,w,h</c>: pixels of the primary monitor, top-left
    /// origin. Null after adding the reason to <paramref name="errors"/>.
    /// </summary>
    private static ScreenSource? ResolveTarget(string target, ErrorList errors)
    {
        try
        {
            if (target == "display")
            {
                return PrimaryMonitor(errors) is { } monitor ? ScreenSource.Display(monitor.Handle) : null;
            }

            if (target.StartsWith("window:", StringComparison.Ordinal))
            {
                var title = target["window:".Length..];
                var window = title.Length == 0 ? 0 : WindowWithTitle(title);
                if (window != 0) return ScreenSource.Window(window);
                errors.Add($"target {target}: no visible top-level window whose title contains \"{title}\"");
                return null;
            }

            if (target.StartsWith("area:", StringComparison.Ordinal))
            {
                var parts = target["area:".Length..].Split(',');
                var numbers = new int[4];
                if (parts.Length != 4 || !parts.Select((part, i) => int.TryParse(part, CultureInfo.InvariantCulture, out numbers[i])).All(ok => ok))
                {
                    errors.Add($"target {target}: expected area:x,y,w,h in whole pixels");
                    return null;
                }

                if (PrimaryMonitor(errors) is not { } monitor) return null;
                var area = ScreenGeometry.AreaPixels(new ScreenGeometry.Rect(numbers[0], numbers[1], numbers[2], numbers[3]),
                    new ScreenGeometry.Size(monitor.Bounds.Width, monitor.Bounds.Height));
                if (area is { } pixels) return ScreenSource.Area(monitor.Handle, pixels);
                errors.Add($"target {target}: not on the primary display ({monitor.Bounds.Width}x{monitor.Bounds.Height})");
                return null;
            }

            errors.Add($"unknown target {target} (display|window:<title>|area:x,y,w,h)");
            return null;
        }
        catch (Exception ex)
        {
            errors.Add(Describe($"target {target}", ex));
            return null;
        }
    }

    private static CaptureMonitor? PrimaryMonitor(ErrorList errors)
    {
        foreach (var monitor in GraphicsCapture.Monitors())
        {
            if (monitor.IsPrimary) return monitor;
        }

        errors.Add("no primary display");
        return null;
    }

    private static nint WindowWithTitle(string title)
    {
        nint found = 0;
        var buffer = new char[512];
        _ = EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window)) return true;
            var length = GetWindowTextW(window, buffer, buffer.Length);
            if (length <= 0 || !new string(buffer, 0, length).Contains(title, StringComparison.OrdinalIgnoreCase)) return true;
            found = window;
            return false;
        }, 0);
        return found;
    }

    /// <summary>Waits until <paramref name="second"/> seconds after <paramref name="wall"/> started.</summary>
    private static async Task Until(System.Diagnostics.Stopwatch wall, double second)
    {
        var wait = TimeSpan.FromSeconds(second) - wall.Elapsed;
        if (wait > TimeSpan.Zero) await Task.Delay(wait);
    }

    /// <summary>QPC now in 100 ns units: the clock of the engine and of <see cref="ScreenRecorder"/>.</summary>
    private static long Now() => System.Diagnostics.Stopwatch.GetElapsedTime(0).Ticks;

    private delegate bool EnumWindowsProc(nint window, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(nint window, [Out] char[] text, int maxCount);

    // --native-probe <modelsDir>, with sample.wav and two-voice.wav in <modelsDir>\..

    // Ad-hoc entries over the files in <modelsDir> (the engines only need the file names): the catalog has no tiny Whisper.
    private static readonly ModelEntry WhisperTiny = ModelCatalog.Entry("whisper-base")! with
    {
        Id = "whisper-tiny-probe",
        Files = ["ggml-tiny.bin"],
    };

    private static readonly ModelEntry Diarizer = ModelCatalog.Entry("sherpa-pyannote-3")!;

    private static async Task RunNativeAsync(string[] args)
    {
        Directory.CreateDirectory(ProbesDir);
        var stamp = Timestamp();
        var path = Path.Combine(ProbesDir, $"native-{stamp}.json");
        var work = Path.Combine(ProbesDir, $"native-{stamp}");
        var errors = new List<string>();
        string? whisperText = null;
        int? sherpaSegments = null;
        int? sherpaSpeakers = null;
        string? llamaSystemInfo = null;
        try
        {
            if (args.Length < 1)
            {
                errors.Add($"usage: {NativeFlag} <modelsDir>");
                return;
            }

            var models = Path.GetFullPath(args[0]);
            var fixtures = Path.GetFullPath(Path.Combine(models, ".."));
            Directory.CreateDirectory(work);

            try
            {
                var audio = Channel(Path.Combine(fixtures, "sample.wav"), work);
                var segments = await WhisperEngine.Create(WhisperTiny, models)
                    .TranscribeAsync(audio, [new LanguageRange(0, null, "en")], _ => { }, CancellationToken.None);
                whisperText = string.Join(" ", segments.Select(s => s.Text)).Trim();
            }
            catch (Exception ex)
            {
                errors.Add(Describe("whisper", ex));
            }

            try
            {
                var audio = Channel(Path.Combine(fixtures, "two-voice.wav"), work);
                var turns = await SherpaDiarizer.Create(Diarizer, models, null).DiarizeAsync(audio, _ => { }, CancellationToken.None);
                sherpaSegments = turns.Count;
                sherpaSpeakers = turns.Select(t => t.SpeakerIndex).Distinct().Count();
            }
            catch (Exception ex)
            {
                errors.Add(Describe("sherpa", ex));
            }

            try
            {
                llamaSystemInfo = Marshal.PtrToStringUTF8(NativeApi.llama_print_system_info()); // static buffer owned by llama.cpp
            }
            catch (Exception ex)
            {
                errors.Add(Describe("llama", ex));
            }
        }
        finally
        {
            var result = new
            {
                processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                whisperText,
                sherpaSegments,
                sherpaSpeakers,
                llamaSystemInfo,
                errors,
            };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(result, JsonOptions));
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
    }

    /// <summary>The 16 kHz mono CAF the engines read, made from a 16 kHz PCM16 WAV fixture in <paramref name="dir"/>.</summary>
    private static string Channel(string wavPath, string dir)
    {
        var caf = Path.Combine(dir, Path.ChangeExtension(Path.GetFileName(wavPath), ".caf"));
        MonoAudio.Extract(0, wavPath, caf, (path, _) =>
        {
            var (samples, rate) = ReadWav(path);
            if (rate != MonoAudio.SampleRate) throw new InvalidDataException($"{path} is {rate} Hz; the engines need 16000 Hz.");
            return samples;
        });
        return caf;
    }

    /// <summary>Reads a 16-bit PCM WAV file as mono floats (channels averaged).</summary>
    private static (float[] Samples, int SampleRate) ReadWav(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (ReadId(reader) != "RIFF") throw new InvalidDataException($"{path} is not a RIFF file.");
        reader.ReadInt32();
        if (ReadId(reader) != "WAVE") throw new InvalidDataException($"{path} is not a WAVE file.");

        int channels = 0, rate = 0, bits = 0;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var id = ReadId(reader);
            var size = reader.ReadUInt32();
            var next = reader.BaseStream.Position + size + (size & 1);
            if (id == "fmt ")
            {
                var format = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                rate = reader.ReadInt32();
                reader.ReadInt32(); // byte rate
                reader.ReadUInt16(); // block align
                bits = reader.ReadUInt16();
                if (format != 1 || bits != 16) throw new InvalidDataException($"{path}: only 16-bit PCM is supported (format {format}, {bits} bits).");
            }
            else if (id == "data")
            {
                if (channels == 0) throw new InvalidDataException($"{path}: data chunk before fmt chunk.");
                var bytes = reader.ReadBytes((int)Math.Min(size, reader.BaseStream.Length - reader.BaseStream.Position));
                var pcm = MemoryMarshal.Cast<byte, short>(bytes.AsSpan(0, bytes.Length - bytes.Length % (2 * channels)));
                var samples = new float[pcm.Length / channels];
                for (var frame = 0; frame < samples.Length; frame++)
                {
                    var sum = 0f;
                    for (var channel = 0; channel < channels; channel++) sum += pcm[frame * channels + channel];
                    samples[frame] = sum / channels / 32768f;
                }

                return (samples, rate);
            }

            reader.BaseStream.Position = next;
        }

        throw new InvalidDataException($"{path} has no data chunk.");
    }

    private static string ReadId(BinaryReader reader) => System.Text.Encoding.ASCII.GetString(reader.ReadBytes(4));
}
#endif
