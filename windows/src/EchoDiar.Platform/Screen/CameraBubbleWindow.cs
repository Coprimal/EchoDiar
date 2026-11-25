using System.ComponentModel;
using System.Runtime.InteropServices;
using EchoDiar.Core.Screen;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.WindowsAndMessaging;
using Rect = EchoDiar.Core.Screen.ScreenGeometry.Rect;

namespace EchoDiar.Platform.Screen;

/// <summary>Where the camera bubble is on screen: its monitor (HMONITOR) and its square in virtual-screen pixels.</summary>
public readonly record struct CameraBubblePlacement(nint Monitor, Rect Bounds);

/// <summary>
/// The camera bubble of a screen recording (macOS <c>CameraBubbleController</c>): a round topmost layered window,
/// 180 DIPs wide, 24 DIPs from the bottom-right corner of its target (the monitor's work area, or the recorded area),
/// showing the camera frames pushed to it (cropped to a centred square, already mirrored by the camera) or a neutral
/// grey placeholder (macOS white 0.23) until the first frame. It is dragged with the left mouse button inside its target,
/// never activated, left out of the taskbar and kept out of screen captures (WDA_EXCLUDEFROMCAPTURE): the recorder draws
/// the same frames into the video at <see cref="Placement"/> instead, so screen shares (Teams, Zoom) do not show it.
/// No border, no shadow (macOS neither).
/// <para>
/// Threading: the window lives on its own STA thread with its own message loop (per-monitor DPI aware v2, so every
/// coordinate is in physical pixels), started by the constructor. All members are safe from any thread, never block on
/// that thread and never throw; failures go to the log.
/// </para>
/// </summary>
public sealed unsafe class CameraBubbleWindow : IDisposable
{
    private const double Diameter = 180; // DIPs, macOS 180 points
    private const double Margin = 24; // DIPs, macOS 24 points
    private const uint PlaceholderGray = 0x3B; // macOS white 0.23
    private const uint ApplyMessage = PInvoke.WM_APP + 1;
    private const uint FrameMessage = PInvoke.WM_APP + 2;
    private const uint CloseMessage = PInvoke.WM_APP + 3;
    private const nuint LeftButtonDown = 0x0001; // MK_LBUTTON

    /// <summary>Rooted for the process's life: a collected delegate behind a live window class would crash.</summary>
    private static readonly WNDPROC Callback = WndProc;

    /// <summary>Live bubbles by handle (the window procedure's lookup).</summary>
    private static readonly Dictionary<HWND, CameraBubbleWindow> Live = [];

    private readonly Action<string>? _log;
    private readonly object _gate = new();

    // Guarded by _gate: what the callers asked for.
    private bool _disposed;
    private HWND _post;
    private bool _visible;
    private nint _monitor;
    private Rect? _area;
    private byte[]? _frame;
    private int _frameWidth, _frameHeight;
    private bool _hasFrame, _frameFresh, _framePosted;
    private CameraBubblePlacement? _placement;

    // Bubble thread only.
    private HWND _hwnd;
    private bool _done;
    private bool _shown;
    private bool _hiddenLogged;
    private bool _positioned;
    private nint _placedMonitor;
    private Rect? _placedArea;
    private Rect _target;
    private int _x, _y, _size;
    private HDC _dc;
    private HBITMAP _bitmap;
    private HGDIOBJ _oldBitmap;
    private uint* _bits;
    /// <summary>The circle's coverage (0-255) per pixel of the bubble, antialiased at the edge.</summary>
    private byte[] _mask = [];
    private byte[]? _rendering;
    private int _renderingWidth, _renderingHeight;
    private bool _renderingHas;
    private bool _dragging;
    private int _dragCursorX, _dragCursorY, _dragX, _dragY;

    /// <summary>Starts the bubble thread; nothing shows until <see cref="Show"/>. Never throws.</summary>
    public CameraBubbleWindow(Action<string>? log = null)
    {
        _log = log;
        try
        {
            var thread = new Thread(Run) { IsBackground = true, Name = "EchoDiar camera bubble" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        catch (Exception e)
        {
            log?.Invoke($"camera bubble: {e.GetType().Name} {e.Message}");
        }
    }

    /// <summary>
    /// Raised on the bubble thread whenever <see cref="Placement"/> changes (shown, dragged, kept inside its target after
    /// a display change, hidden: null). An exception thrown by a handler is logged, never rethrown.
    /// </summary>
    public event Action<CameraBubblePlacement?>? PlacementChanged;

    /// <summary>Where the bubble is on screen now, or null while it is hidden. Any thread.</summary>
    public CameraBubblePlacement? Placement
    {
        get
        {
            lock (_gate) return _placement;
        }
    }

    /// <summary>
    /// Shows the bubble inside the monitor <paramref name="monitor"/> (HMONITOR): in its work area, or in
    /// <paramref name="area"/> when given (that monitor's pixels, top-left origin: the region of a
    /// <see cref="ScreenSourceKind.Area"/> source). At the bottom-right corner when first shown or when the monitor or the
    /// area changed; where it was dragged to otherwise. The placeholder shows until the next <see cref="SetFrame"/>.
    /// Hidden while the monitor is gone or the area is off it. Returns at once; never throws.
    /// </summary>
    public void Show(nint monitor, Rect? area = null)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _visible = true;
            _monitor = monitor;
            _area = area;
            if (!_post.IsNull) PInvoke.PostMessage(_post, ApplyMessage, default, default);
        }
    }

    /// <summary>Hides the bubble and drops its frame (the next show starts with the placeholder). Never throws.</summary>
    public void Hide()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _visible = false;
            _hasFrame = _frameFresh = false;
            if (!_post.IsNull) PInvoke.PostMessage(_post, ApplyMessage, default, default);
        }
    }

    /// <summary>
    /// Shows <paramref name="bgra"/> in the bubble: BGRA rows, top-down, <paramref name="stride"/> bytes apart (as
    /// <see cref="CameraSource.CopyLatest"/> gives them), mirrored already. Copies the pixels and returns; frames that
    /// arrive faster than the bubble draws replace each other. Ignored while hidden or when the sizes do not fit. Any
    /// thread (the camera's); never throws.
    /// </summary>
    public void SetFrame(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0 || stride < width * 4 || bgra.Length < (long)stride * (height - 1) + width * 4) return;
        lock (_gate)
        {
            if (_disposed || !_visible) return;
            var row = width * 4;
            if (_frame is null || _frame.Length != row * height) _frame = new byte[row * height];
            for (var y = 0; y < height; y++) bgra.Slice(y * stride, row).CopyTo(_frame.AsSpan(y * row, row));
            _frameWidth = width;
            _frameHeight = height;
            _hasFrame = _frameFresh = true;
            if (!_framePosted && !_post.IsNull)
            {
                _framePosted = PInvoke.PostMessage(_post, FrameMessage, default, default);
            }
        }
    }

    /// <summary>Shows the placeholder again until the next <see cref="SetFrame"/>. Never throws.</summary>
    public void ClearFrame()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _hasFrame = _frameFresh = false;
            if (!_framePosted && !_post.IsNull)
            {
                _framePosted = PInvoke.PostMessage(_post, FrameMessage, default, default);
            }
        }
    }

    /// <summary>Hides the bubble and releases its window on the bubble thread. Any thread; idempotent; never throws.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _visible = false;
            if (!_post.IsNull) PInvoke.PostMessage(_post, CloseMessage, default, default);
        }
    }

    /// <summary>Creates the window, pumps messages until disposed, and releases everything.</summary>
    private void Run()
    {
        var instance = new HINSTANCE(PInvoke.GetModuleHandle(default(PCWSTR)).Value);
        var className = "EchoDiar.CameraBubble." + Guid.NewGuid().ToString("N");
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
                    hCursor = PInvoke.LoadCursor(HINSTANCE.Null, PInvoke.IDC_ARROW),
                    lpszClassName = name,
                };
                if (PInvoke.RegisterClassEx(windowClass) == 0) throw new Win32Exception();
                registered = true;
                // Not WS_EX_TRANSPARENT: the bubble takes the mouse (its transparent corners let clicks through).
                _hwnd = PInvoke.CreateWindowEx(
                    WINDOW_EX_STYLE.WS_EX_LAYERED | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE
                    | WINDOW_EX_STYLE.WS_EX_TOPMOST,
                    name, default(PCWSTR), WINDOW_STYLE.WS_POPUP, 0, 0, 1, 1, HWND.Null, HMENU.Null, instance, null);
                if (_hwnd.IsNull) throw new Win32Exception();
            }
            lock (Live) Live[_hwnd] = this;
            if (!PInvoke.SetWindowDisplayAffinity(_hwnd, WINDOW_DISPLAY_AFFINITY.WDA_EXCLUDEFROMCAPTURE))
            {
                _log?.Invoke($"camera bubble: SetWindowDisplayAffinity failed, {Marshal.GetLastPInvokeError()}");
            }
            _dc = PInvoke.CreateCompatibleDC(HDC.Null);
            if (_dc.IsNull) throw new Win32Exception();
            lock (_gate)
            {
                if (_disposed) return; // disposed before the window existed
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
            _log?.Invoke($"camera bubble: {e.GetType().Name} {e.Message}");
        }
        finally
        {
            lock (_gate)
            {
                _post = HWND.Null; // never posted to a destroyed (or recycled) handle
                _placement = null;
            }
            if (!_hwnd.IsNull)
            {
                lock (Live) Live.Remove(_hwnd);
                if (!PInvoke.DestroyWindow(_hwnd))
                {
                    _log?.Invoke($"camera bubble: DestroyWindow failed, {Marshal.GetLastPInvokeError()}");
                }
            }
            FreeSurface();
            if (!_dc.IsNull) PInvoke.DeleteDC(_dc);
            if (registered)
            {
                fixed (char* name = className) PInvoke.UnregisterClass(name, instance);
            }
        }
    }

    private static LRESULT WndProc(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        CameraBubbleWindow? bubble;
        lock (Live) Live.TryGetValue(hwnd, out bubble);
        if (bubble is not null)
        {
            try
            {
                switch (message)
                {
                    case ApplyMessage:
                    case PInvoke.WM_DISPLAYCHANGE:
                    case PInvoke.WM_DPICHANGED:
                        bubble.Apply();
                        return (LRESULT)0;
                    case FrameMessage:
                        bubble.TakeFrame();
                        if (bubble._shown) bubble.Render();
                        return (LRESULT)0;
                    case CloseMessage:
                        bubble.HideWindow(); // PlacementChanged(null) before the window goes
                        bubble.Finish();
                        return (LRESULT)0;
                    case PInvoke.WM_CLOSE:
                        return (LRESULT)0; // only Dispose ends the bubble
                    case PInvoke.WM_MOUSEACTIVATE:
                        return (LRESULT)(nint)PInvoke.MA_NOACTIVATE;
                    case PInvoke.WM_LBUTTONDOWN:
                        bubble.BeginDrag();
                        return (LRESULT)0;
                    case PInvoke.WM_MOUSEMOVE:
                        bubble.Drag((wParam.Value & LeftButtonDown) != 0);
                        return (LRESULT)0;
                    case PInvoke.WM_LBUTTONUP:
                        bubble._dragging = false;
                        PInvoke.ReleaseCapture();
                        return (LRESULT)0;
                    case PInvoke.WM_CAPTURECHANGED:
                        bubble._dragging = false;
                        return (LRESULT)0;
                }
            }
            catch (Exception e)
            {
                // An exception must not cross the native frame: the bubble ends instead.
                bubble._log?.Invoke($"camera bubble: 0x{message:X4} threw, {e.GetType().Name} {e.Message}");
                bubble.Finish();
            }
        }
        return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void Finish()
    {
        if (_done) return;
        _done = true;
        // Wakes GetMessage: a sent message (WM_DISPLAYCHANGE) runs inside it without returning.
        PInvoke.PostMessage(_hwnd, PInvoke.WM_NULL, default, default);
    }

    // MARK: - Placement

    /// <summary>Shows, places or hides the bubble to match the last <see cref="Show"/> or <see cref="Hide"/>.</summary>
    private void Apply()
    {
        bool visible;
        nint monitor;
        Rect? area;
        lock (_gate)
        {
            visible = _visible && !_disposed;
            monitor = _monitor;
            area = _area;
        }
        if (!visible)
        {
            HideWindow();
            return;
        }
        if (Target(monitor, area) is not { } target)
        {
            if (!_hiddenLogged) _log?.Invoke("camera bubble: the monitor is gone or the area is off it, hidden");
            _hiddenLogged = true;
            HideWindow();
            return;
        }
        _hiddenLogged = false;
        var scale = PInvoke.GetDpiForMonitor((HMONITOR)monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out var dpi, out _).Succeeded
            && dpi > 0 ? dpi / 96.0 : 1;
        var size = Math.Max(1, Math.Min((int)Math.Round(Diameter * scale), Math.Min(target.Width, target.Height)));
        var toCorner = !_positioned || monitor != _placedMonitor || area != _placedArea || size != _size;
        if (size != _size) CreateSurface(size);
        _target = target;
        if (toCorner)
        {
            var inset = Math.Min((int)Math.Round(Margin * scale), Math.Min(target.Width - size, target.Height - size) / 2);
            _x = target.X + target.Width - size - inset;
            _y = target.Y + target.Height - size - inset;
        }
        (_x, _y) = Clamped(_x, _y);
        _positioned = true;
        _placedMonitor = monitor;
        _placedArea = area;
        TakeFrame();
        Render();
        if (!_shown)
        {
            PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_SHOWNA);
            _shown = true;
        }
        Publish();
    }

    /// <summary>The rectangle the bubble stays in, in virtual-screen pixels; null when the monitor is gone or the area is off it.</summary>
    private static Rect? Target(nint monitor, Rect? area)
    {
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        if (!PInvoke.GetMonitorInfo((HMONITOR)monitor, &info)) return null;
        var bounds = info.rcMonitor;
        if (area is null)
        {
            var work = info.rcWork;
            return new Rect(work.left, work.top, work.right - work.left, work.bottom - work.top);
        }
        return ScreenGeometry.AreaPixels(area.Value, new ScreenGeometry.Size(bounds.right - bounds.left, bounds.bottom - bounds.top))
            is { } pixels ? pixels with { X = bounds.left + pixels.X, Y = bounds.top + pixels.Y } : null;
    }

    private (int X, int Y) Clamped(int x, int y) =>
        (Math.Clamp(x, _target.X, Math.Max(_target.X, _target.X + _target.Width - _size)),
         Math.Clamp(y, _target.Y, Math.Max(_target.Y, _target.Y + _target.Height - _size)));

    private void HideWindow()
    {
        _dragging = false;
        if (_shown) PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_HIDE);
        _shown = false;
        Publish();
    }

    /// <summary>Stores the bubble's placement and raises <see cref="PlacementChanged"/> when it changed.</summary>
    private void Publish()
    {
        CameraBubblePlacement? placement = _shown ? new(_placedMonitor, new Rect(_x, _y, _size, _size)) : null;
        lock (_gate)
        {
            if (_placement == placement) return;
            _placement = placement;
        }
        try
        {
            PlacementChanged?.Invoke(placement);
        }
        catch (Exception e)
        {
            _log?.Invoke($"camera bubble: a PlacementChanged handler threw, {e.GetType().Name} {e.Message}");
        }
    }

    // MARK: - Drag

    private void BeginDrag()
    {
        if (!PInvoke.GetCursorPos(out var cursor)) return;
        _dragging = true;
        (_dragCursorX, _dragCursorY, _dragX, _dragY) = (cursor.X, cursor.Y, _x, _y);
        PInvoke.SetCapture(_hwnd);
    }

    /// <summary>Follows the pointer (screen deltas: the window moves under it) while the left button is down.</summary>
    private void Drag(bool leftDown)
    {
        if (!_dragging) return;
        if (!leftDown)
        {
            // The release was missed (it happened outside the bubble).
            _dragging = false;
            PInvoke.ReleaseCapture();
            return;
        }
        if (!PInvoke.GetCursorPos(out var cursor)) return;
        var (x, y) = Clamped(_dragX + cursor.X - _dragCursorX, _dragY + cursor.Y - _dragCursorY);
        if (x == _x && y == _y) return;
        (_x, _y) = (x, y);
        PInvoke.SetWindowPos(_hwnd, HWND.Null, x, y, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        Publish();
    }

    // MARK: - Drawing

    /// <summary>Swaps in the latest pushed frame, if any (the callers' buffer becomes the next one they fill).</summary>
    private void TakeFrame()
    {
        lock (_gate)
        {
            _framePosted = false;
            if (_frameFresh)
            {
                (_rendering, _frame) = (_frame, _rendering);
                _renderingWidth = _frameWidth;
                _renderingHeight = _frameHeight;
                _frameFresh = false;
            }
            _renderingHas = _hasFrame && _rendering is not null;
        }
    }

    /// <summary>A <paramref name="size"/> × <paramref name="size"/> 32 bpp DIB selected into the DC, and its circle mask.</summary>
    private void CreateSurface(int size)
    {
        FreeSurface();
        var info = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(BITMAPINFOHEADER),
                biWidth = size,
                biHeight = -size, // top-down rows
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0, // BI_RGB
            },
        };
        void* bits;
        _bitmap = PInvoke.CreateDIBSection(_dc, &info, DIB_USAGE.DIB_RGB_COLORS, &bits, default, 0);
        if (_bitmap.IsNull) throw new Win32Exception();
        _bits = (uint*)bits;
        _oldBitmap = PInvoke.SelectObject(_dc, _bitmap);
        _size = size;
        _mask = new byte[size * size];
        var radius = size / 2.0;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                double dx = x + 0.5 - radius, dy = y + 0.5 - radius;
                var coverage = Math.Clamp(radius - Math.Sqrt(dx * dx + dy * dy) + 0.5, 0, 1);
                _mask[y * size + x] = (byte)Math.Round(coverage * 255);
            }
        }
    }

    private void FreeSurface()
    {
        if (!_oldBitmap.IsNull) PInvoke.SelectObject(_dc, _oldBitmap);
        if (!_bitmap.IsNull) PInvoke.DeleteObject(_bitmap);
        _oldBitmap = default;
        _bitmap = default;
        _bits = null;
        _size = 0;
    }

    /// <summary>
    /// Draws the frame (its centred square, box-filtered to the bubble's size) or the placeholder inside the circle,
    /// premultiplied, and hands it to the window at its position.
    /// </summary>
    private void Render()
    {
        if (_bits is null) return;
        var size = _size;
        if (_renderingHas)
        {
            int width = _renderingWidth, height = _renderingHeight, side = Math.Min(width, height);
            int left = (width - side) / 2, top = (height - side) / 2;
            var scale = side / (double)size;
            fixed (byte* frame = _rendering)
            {
                for (var y = 0; y < size; y++)
                {
                    int y0 = top + (int)(y * scale), y1 = Math.Max(y0 + 1, Math.Min(top + side, top + (int)((y + 1) * scale)));
                    for (var x = 0; x < size; x++)
                    {
                        var coverage = (uint)_mask[y * size + x];
                        if (coverage == 0)
                        {
                            _bits[y * size + x] = 0;
                            continue;
                        }
                        int x0 = left + (int)(x * scale), x1 = Math.Max(x0 + 1, Math.Min(left + side, left + (int)((x + 1) * scale)));
                        uint b = 0, g = 0, r = 0, count = 0;
                        for (var sy = y0; sy < y1; sy++)
                        {
                            var pixel = frame + (sy * width + x0) * 4;
                            for (var sx = x0; sx < x1; sx++, pixel += 4)
                            {
                                b += pixel[0];
                                g += pixel[1];
                                r += pixel[2];
                                count++;
                            }
                        }
                        _bits[y * size + x] = Pixel(r / count, g / count, b / count, coverage);
                    }
                }
            }
        }
        else
        {
            for (var i = 0; i < size * size; i++)
            {
                _bits[i] = Pixel(PlaceholderGray, PlaceholderGray, PlaceholderGray, _mask[i]);
            }
        }
        var destination = new System.Drawing.Point(_x, _y);
        var extent = new SIZE { cx = size, cy = size };
        var source = new System.Drawing.Point(0, 0);
        var blend = new BLENDFUNCTION
        {
            BlendOp = (byte)PInvoke.AC_SRC_OVER,
            SourceConstantAlpha = 255,
            AlphaFormat = (byte)PInvoke.AC_SRC_ALPHA,
        };
        if (!PInvoke.UpdateLayeredWindow(
            _hwnd, HDC.Null, destination, extent, _dc, source, default, blend, UPDATE_LAYERED_WINDOW_FLAGS.ULW_ALPHA))
        {
            _log?.Invoke($"camera bubble: UpdateLayeredWindow failed, {Marshal.GetLastPInvokeError()}");
        }
    }

    /// <summary>An opaque colour at <paramref name="alpha"/> (0-255), premultiplied BGRA.</summary>
    private static uint Pixel(uint r, uint g, uint b, uint alpha) =>
        alpha << 24 | (r * alpha / 255) << 16 | (g * alpha / 255) << 8 | b * alpha / 255;
}
