using System.ComponentModel;
using System.Runtime.InteropServices;
using EchoDiar.Core.Screen;
using Windows.UI.ViewManagement;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.WindowsAndMessaging;
using Rect = EchoDiar.Core.Screen.ScreenGeometry.Rect;

namespace EchoDiar.Platform.Screen;

/// <summary>
/// A thin, calm outline just outside the area a Portion screen video records (macOS <c>RecordingOutline</c> for an
/// area): a 2 DIP line in the system accent colour at 0.7 opacity, so the line itself is never in the picture. A display
/// or a window has no outline of EchoDiar's own: Windows' yellow capture border already marks it. The line is four thin
/// topmost layered windows (one per side, so no window covers the recorded area), click-through, never activated,
/// left out of the taskbar and kept out of screen captures (WDA_EXCLUDEFROMCAPTURE). A display change places it again
/// around the part of the area still on the monitor (as the recorder crops it), or hides it while the monitor is gone
/// or the area is off it. Nothing animates.
/// <para>
/// Threading: the windows live on their own STA thread with its own message loop (per-monitor DPI aware v2, so every
/// coordinate is in physical pixels). <see cref="Show"/> and <see cref="Dispose"/> are safe from any thread, never
/// block on that thread and never throw.
/// </para>
/// </summary>
public sealed unsafe class RecordingOutline : IDisposable
{
    private const double LineWidth = 2; // DIPs, macOS 2 points
    private const double Opacity = 0.7; // macOS controlAccentColor at 0.7
    private const uint CloseMessage = PInvoke.WM_APP + 1;

    /// <summary>Rooted for the process's life: a collected delegate behind a live window class would crash.</summary>
    private static readonly WNDPROC Callback = WndProc;

    /// <summary>Live band windows by handle (the window procedure's lookup).</summary>
    private static readonly Dictionary<HWND, RecordingOutline> Live = [];

    private readonly nint _monitor;
    private readonly Rect _area;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    /// <summary>Top, bottom, left, right; touched only on the outline thread.</summary>
    private readonly HWND[] _bands = new HWND[4];
    /// <summary>The window <see cref="Dispose"/> posts to, set while the loop runs; guarded by <see cref="_gate"/>.</summary>
    private HWND _post;
    /// <summary>Guarded by <see cref="_gate"/>.</summary>
    private bool _disposed;
    /// <summary>Outline thread only.</summary>
    private bool _done;
    private bool _hidden;

    private RecordingOutline(nint monitor, Rect area, Action<string>? log)
    {
        _monitor = monitor;
        _area = area;
        _log = log;
    }

    /// <summary>
    /// Shows the outline just outside <paramref name="area"/>, in the pixels of the monitor <paramref name="monitor"/>
    /// (HMONITOR), top-left origin: the region of a <see cref="ScreenSourceKind.Area"/> source. Returns at once; the
    /// windows appear on the outline thread. Never throws: when the outline cannot be shown, <paramref name="log"/> says
    /// why and the returned outline shows nothing. Show it only while a Portion video runs and dispose it when the
    /// screen turns off, the video ends or the recording stops.
    /// </summary>
    public static RecordingOutline Show(nint monitor, Rect area, Action<string>? log = null)
    {
        var outline = new RecordingOutline(monitor, area, log);
        try
        {
            var thread = new Thread(outline.Run) { IsBackground = true, Name = "EchoDiar recording outline" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        catch (Exception e)
        {
            log?.Invoke($"recording outline: {e.GetType().Name} {e.Message}");
        }
        return outline;
    }

    /// <summary>Hides the outline and releases its windows on the outline thread. Any thread; idempotent; never throws.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (!_post.IsNull) PInvoke.PostMessage(_post, CloseMessage, default, default);
        }
    }

    /// <summary>Creates the windows, places them, pumps messages until disposed, and releases everything.</summary>
    private void Run()
    {
        var instance = new HINSTANCE(PInvoke.GetModuleHandle(default(PCWSTR)).Value);
        var className = "EchoDiar.RecordingOutline." + Guid.NewGuid().ToString("N");
        var registered = false;
        try
        {
            // Physical pixels everywhere, whatever the process's manifest says.
            PInvoke.SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            fixed (char* name = className)
            {
                var windowClass = new WNDCLASSEXW
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                    lpfnWndProc = Callback,
                    hInstance = instance,
                    lpszClassName = name,
                };
                if (PInvoke.RegisterClassEx(windowClass) == 0) throw new Win32Exception();
                registered = true;
                for (var i = 0; i < _bands.Length; i++)
                {
                    var hwnd = PInvoke.CreateWindowEx(
                        WINDOW_EX_STYLE.WS_EX_LAYERED | WINDOW_EX_STYLE.WS_EX_TRANSPARENT | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW
                        | WINDOW_EX_STYLE.WS_EX_NOACTIVATE | WINDOW_EX_STYLE.WS_EX_TOPMOST,
                        name, default(PCWSTR), WINDOW_STYLE.WS_POPUP, 0, 0, 1, 1, HWND.Null, HMENU.Null, instance, null);
                    if (hwnd.IsNull) throw new Win32Exception();
                    _bands[i] = hwnd;
                    lock (Live) Live[hwnd] = this;
                    if (!PInvoke.SetWindowDisplayAffinity(hwnd, WINDOW_DISPLAY_AFFINITY.WDA_EXCLUDEFROMCAPTURE))
                    {
                        _log?.Invoke($"recording outline: SetWindowDisplayAffinity failed, {Marshal.GetLastPInvokeError()}");
                    }
                }
            }
            lock (_gate)
            {
                if (_disposed) return; // disposed before the windows existed
                _post = _bands[0];
            }
            Place();
            Loop();
        }
        catch (Exception e)
        {
            _log?.Invoke($"recording outline: {e.GetType().Name} {e.Message}");
        }
        finally
        {
            lock (_gate) _post = HWND.Null; // never posted to a destroyed (or recycled) handle
            foreach (var hwnd in _bands)
            {
                if (hwnd.IsNull) continue;
                lock (Live) Live.Remove(hwnd);
                if (!PInvoke.DestroyWindow(hwnd))
                {
                    _log?.Invoke($"recording outline: DestroyWindow failed, {Marshal.GetLastPInvokeError()}");
                }
            }
            if (registered)
            {
                fixed (char* name = className) PInvoke.UnregisterClass(name, instance);
            }
        }
    }

    private void Loop()
    {
        while (!_done)
        {
            var got = PInvoke.GetMessage(out var message, HWND.Null, 0, 0);
            if (got.Value <= 0) break; // WM_QUIT or an error
            PInvoke.TranslateMessage(message);
            PInvoke.DispatchMessage(message);
        }
    }

    private void Finish()
    {
        if (_done) return;
        _done = true;
        // Wakes GetMessage: a sent message (WM_DISPLAYCHANGE) runs inside it without returning.
        PInvoke.PostMessage(_bands[0], PInvoke.WM_NULL, default, default);
    }

    private static LRESULT WndProc(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        RecordingOutline? outline;
        lock (Live) Live.TryGetValue(hwnd, out outline);
        if (outline is not null)
        {
            try
            {
                switch (message)
                {
                    case CloseMessage:
                        outline.Finish();
                        return (LRESULT)0;
                    case PInvoke.WM_CLOSE:
                        return (LRESULT)0; // only Dispose ends the outline
                    case PInvoke.WM_DISPLAYCHANGE:
                        if (hwnd == outline._bands[0]) outline.Place(); // once, not once per band
                        return (LRESULT)0;
                }
            }
            catch (Exception e)
            {
                // An exception must not cross the native frame: the outline ends instead.
                outline._log?.Invoke($"recording outline: 0x{message:X4} threw, {e.GetType().Name} {e.Message}");
                outline.Finish();
            }
        }
        return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
    }

    /// <summary>
    /// Places the four bands just outside the recorded part of the area (<see cref="ScreenGeometry.AreaPixels"/> on the
    /// monitor's current size, as the recorder crops) and shows them; hides them while the monitor is gone or the area
    /// is off it. Bands may reach past the monitor's edge (macOS too): never inside the area.
    /// </summary>
    private void Place()
    {
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        var area = PInvoke.GetMonitorInfo((HMONITOR)_monitor, &info)
            ? ScreenGeometry.AreaPixels(_area, new ScreenGeometry.Size(
                info.rcMonitor.right - info.rcMonitor.left, info.rcMonitor.bottom - info.rcMonitor.top))
            : null;
        if (area is not { } recorded)
        {
            if (!_hidden) _log?.Invoke("recording outline: the monitor is gone or the area is off it, hidden");
            _hidden = true;
            foreach (var hwnd in _bands) PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_HIDE);
            return;
        }
        var monitor = info.rcMonitor;
        var scale = PInvoke.GetDpiForMonitor((HMONITOR)_monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out var dpi, out _).Succeeded
            && dpi > 0 ? dpi / 96.0 : 1;
        var line = Math.Max(1, (int)Math.Round(LineWidth * scale));
        int left = monitor.left + recorded.X - line, top = monitor.top + recorded.Y - line;
        int width = recorded.Width + 2 * line, height = recorded.Height + 2 * line;
        Rect[] frames =
        [
            new(left, top, width, line),
            new(left, top + height - line, width, line),
            new(left, top + line, line, recorded.Height),
            new(left + width - line, top + line, line, recorded.Height),
        ];
        var pixel = Premultiplied(AccentColor(), Opacity);
        for (var i = 0; i < _bands.Length; i++)
        {
            Present(_bands[i], frames[i], pixel);
            PInvoke.ShowWindow(_bands[i], SHOW_WINDOW_CMD.SW_SHOWNA);
        }
        _hidden = false;
    }

    /// <summary>Moves <paramref name="hwnd"/> to <paramref name="frame"/> filled with <paramref name="pixel"/>.</summary>
    private void Present(HWND hwnd, Rect frame, uint pixel)
    {
        // The window keeps its own copy of the content: the DC and bitmap are freed right away.
        var dc = PInvoke.CreateCompatibleDC(HDC.Null);
        if (dc.IsNull) throw new Win32Exception();
        HBITMAP bitmap = default;
        HGDIOBJ old = default;
        try
        {
            var bitmapInfo = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)sizeof(BITMAPINFOHEADER),
                    biWidth = frame.Width,
                    biHeight = -frame.Height, // top-down rows
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0, // BI_RGB
                },
            };
            void* bits;
            bitmap = PInvoke.CreateDIBSection(dc, &bitmapInfo, DIB_USAGE.DIB_RGB_COLORS, &bits, default, 0);
            if (bitmap.IsNull) throw new Win32Exception();
            new Span<uint>(bits, frame.Width * frame.Height).Fill(pixel);
            old = PInvoke.SelectObject(dc, bitmap);
            var destination = new System.Drawing.Point(frame.X, frame.Y);
            var size = new SIZE { cx = frame.Width, cy = frame.Height };
            var source = new System.Drawing.Point(0, 0);
            var blend = new BLENDFUNCTION
            {
                BlendOp = (byte)PInvoke.AC_SRC_OVER,
                SourceConstantAlpha = 255,
                AlphaFormat = (byte)PInvoke.AC_SRC_ALPHA,
            };
            if (!PInvoke.UpdateLayeredWindow(
                hwnd, HDC.Null, destination, size, dc, source, default, blend, UPDATE_LAYERED_WINDOW_FLAGS.ULW_ALPHA))
            {
                _log?.Invoke($"recording outline: UpdateLayeredWindow failed, {Marshal.GetLastPInvokeError()}");
            }
        }
        finally
        {
            if (!old.IsNull) PInvoke.SelectObject(dc, old);
            PInvoke.DeleteDC(dc);
            if (!bitmap.IsNull) PInvoke.DeleteObject(bitmap);
        }
    }

    /// <summary><paramref name="rgb"/> (0x00RRGGBB) at <paramref name="alpha"/>, premultiplied BGRA.</summary>
    private static uint Premultiplied(uint rgb, double alpha)
    {
        var a = (uint)Math.Round(alpha * 255);
        uint r = (rgb >> 16 & 0xFF) * a / 255, g = (rgb >> 8 & 0xFF) * a / 255, b = (rgb & 0xFF) * a / 255;
        return a << 24 | r << 16 | g << 8 | b;
    }

    /// <summary>The system accent colour as 0x00RRGGBB; Windows' default blue when it cannot be read.</summary>
    private static uint AccentColor()
    {
        try
        {
            var color = new UISettings().GetColorValue(UIColorType.Accent);
            return (uint)color.R << 16 | (uint)color.G << 8 | color.B;
        }
        catch (Exception)
        {
            return 0x0078D4; // cosmetic: never hides the outline
        }
    }
}
