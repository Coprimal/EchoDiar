using System.Diagnostics;
using System.Runtime.InteropServices;
using EchoDiar.Platform.SystemServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.Input;

namespace EchoDiar.Platform.Screen;

/// <summary>The mouse button of a click <see cref="ClickWatcher"/> reports.</summary>
public enum ClickButton
{
    Left,
    Right,
    Middle,
}

/// <summary>
/// Reports mouse button presses anywhere on the desktop, for the click rings of a screen video: Raw Input for the mouse
/// (usage page 1, usage 2) with RIDEV_INPUTSINK, so presses arrive while other apps are in front, on a message-only
/// window. Only the button-down flags are read (left, right, middle); the position is the cursor's (GetCursorPos) in
/// physical virtual-screen pixels. No low-level hook, no keyboard, no movement.
/// <para>
/// Threading: one window on its own thread (per-monitor DPI aware v2), started by the first <see cref="Start"/> and
/// kept until <see cref="Dispose"/>; <see cref="Start"/> and <see cref="Stop"/> only register and unregister the mouse
/// on it, in call order. Raw Input registration is process-wide per device usage: nothing else in EchoDiar may register
/// the mouse. All members are safe from any thread, never block on the watcher thread and never throw.
/// </para>
/// </summary>
public sealed unsafe class ClickWatcher : IDisposable
{
    private const uint ApplyMessage = PInvoke.WM_APP + 1;
    private const uint CloseMessage = PInvoke.WM_APP + 2;
    private const ushort GenericDesktopPage = 0x01; // HID_USAGE_PAGE_GENERIC
    private const ushort MouseUsage = 0x02; // HID_USAGE_GENERIC_MOUSE

    private readonly Action<string>? _log;
    private readonly object _gate = new();
    /// <summary>Guarded by <see cref="_gate"/>.</summary>
    private bool _wanted;
    private bool _disposed;
    private bool _started;
    /// <summary>The window Start, Stop and Dispose post to, set while the loop runs; guarded by <see cref="_gate"/>.</summary>
    private HWND _post;
    /// <summary>Watcher thread only.</summary>
    private HWND _hwnd;
    private bool _registered;
    private bool _done;

    public ClickWatcher(Action<string>? log = null)
    {
        _log = log;
    }

    /// <summary>
    /// A press: x and y in physical virtual-screen pixels, the button, and the time as QPC in 100 ns units
    /// (<c>Stopwatch.GetElapsedTime(0).Ticks</c>, the recorder's clock). Raised on the watcher thread; an exception
    /// thrown by a handler is logged, never rethrown.
    /// </summary>
    public event Action<int, int, ClickButton, long>? Clicked;

    /// <summary>Starts reporting presses (the first call starts the watcher thread). Idempotent; never throws.</summary>
    public void Start() => Set(true);

    /// <summary>Stops reporting presses (RIDEV_REMOVE); the thread stays for the next <see cref="Start"/>. Never throws.</summary>
    public void Stop() => Set(false);

    /// <summary>Unregisters the mouse and ends the watcher thread. Any thread; idempotent; never throws.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _wanted = false;
            if (!_post.IsNull) PInvoke.PostMessage(_post, CloseMessage, default, default);
        }
    }

    private void Set(bool wanted)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _wanted = wanted;
            if (!_post.IsNull)
            {
                PInvoke.PostMessage(_post, ApplyMessage, default, default);
                return;
            }
            if (_started || !wanted) return; // the starting thread applies _wanted once its window exists
            try
            {
                var thread = new Thread(Run) { IsBackground = true, Name = "EchoDiar click watcher" };
                thread.Start();
                _started = true;
            }
            catch (Exception e)
            {
                _log?.Invoke($"click watcher: {e.GetType().Name} {e.Message}");
            }
        }
    }

    /// <summary>Creates the message-only window, pumps messages until disposed, and releases everything.</summary>
    private void Run()
    {
        MessageWindow? window = null;
        try
        {
            // GetCursorPos in physical pixels, whatever the process's manifest says.
            PInvoke.SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            window = new MessageWindow(_log);
            _hwnd = window.Handle;
            window.AddHandler(PInvoke.WM_INPUT, (_, lParam) =>
            {
                OnInput(lParam);
                return null; // DefWindowProc cleans up the input
            });
            window.AddHandler(ApplyMessage, (_, _) =>
            {
                Apply();
                return (LRESULT)0;
            });
            window.AddHandler(CloseMessage, (_, _) =>
            {
                _done = true;
                return (LRESULT)0;
            });
            lock (_gate)
            {
                if (_disposed) return;
                _post = _hwnd;
            }
            Apply();
            while (!_done)
            {
                var got = PInvoke.GetMessage(out var message, HWND.Null, 0, 0);
                if (got.Value <= 0) break; // WM_QUIT or an error
                PInvoke.TranslateMessage(message);
                PInvoke.DispatchMessage(message);
            }
        }
        catch (Exception e)
        {
            _log?.Invoke($"click watcher: {e.GetType().Name} {e.Message}");
        }
        finally
        {
            lock (_gate) _post = HWND.Null; // never posted to a destroyed (or recycled) handle
            try
            {
                Register(false);
                window?.Dispose();
            }
            catch (Exception e)
            {
                _log?.Invoke($"click watcher: {e.GetType().Name} {e.Message}");
            }
        }
    }

    /// <summary>Registers or unregisters the mouse to match the last Start or Stop.</summary>
    private void Apply()
    {
        bool wanted;
        lock (_gate) wanted = _wanted && !_disposed;
        Register(wanted);
    }

    private void Register(bool on)
    {
        if (on == _registered) return;
        var device = new RAWINPUTDEVICE
        {
            usUsagePage = GenericDesktopPage,
            usUsage = MouseUsage,
            // RIDEV_REMOVE needs no target window; RIDEV_INPUTSINK needs one.
            dwFlags = on ? RAWINPUTDEVICE_FLAGS.RIDEV_INPUTSINK : RAWINPUTDEVICE_FLAGS.RIDEV_REMOVE,
            hwndTarget = on ? _hwnd : HWND.Null,
        };
        if (PInvoke.RegisterRawInputDevices(&device, 1, (uint)sizeof(RAWINPUTDEVICE)))
        {
            _registered = on;
        }
        else
        {
            _log?.Invoke($"click watcher: RegisterRawInputDevices({(on ? "on" : "off")}) failed, {Marshal.GetLastPInvokeError()}");
        }
    }

    /// <summary>Reads one WM_INPUT (foreground or sink) and raises <see cref="Clicked"/> for each button pressed.</summary>
    private void OnInput(LPARAM lParam)
    {
        var time = Stopwatch.GetElapsedTime(0).Ticks;
        if (!_registered) return; // a message queued before Stop
        RAWINPUT input;
        var size = (uint)sizeof(RAWINPUT);
        var read = PInvoke.GetRawInputData(
            new HRAWINPUT(lParam.Value), RAW_INPUT_DATA_COMMAND_FLAGS.RID_INPUT, &input, &size, (uint)sizeof(RAWINPUTHEADER));
        if (read == unchecked((uint)-1) || read < (uint)sizeof(RAWINPUTHEADER)) return;
        if (input.header.dwType != (uint)RID_DEVICE_INFO_TYPE.RIM_TYPEMOUSE) return;
        var flags = (uint)input.data.mouse.Anonymous.Anonymous.usButtonFlags;
        if ((flags & (PInvoke.RI_MOUSE_LEFT_BUTTON_DOWN | PInvoke.RI_MOUSE_RIGHT_BUTTON_DOWN | PInvoke.RI_MOUSE_MIDDLE_BUTTON_DOWN)) == 0)
        {
            return;
        }
        if (!PInvoke.GetCursorPos(out var cursor)) return;
        if ((flags & PInvoke.RI_MOUSE_LEFT_BUTTON_DOWN) != 0) Raise(cursor.X, cursor.Y, ClickButton.Left, time);
        if ((flags & PInvoke.RI_MOUSE_RIGHT_BUTTON_DOWN) != 0) Raise(cursor.X, cursor.Y, ClickButton.Right, time);
        if ((flags & PInvoke.RI_MOUSE_MIDDLE_BUTTON_DOWN) != 0) Raise(cursor.X, cursor.Y, ClickButton.Middle, time);
    }

    private void Raise(int x, int y, ClickButton button, long time)
    {
        try
        {
            Clicked?.Invoke(x, y, button, time);
        }
        catch (Exception e)
        {
            _log?.Invoke($"click watcher: a Clicked handler threw, {e.GetType().Name} {e.Message}");
        }
    }
}
