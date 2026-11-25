using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using EchoDiar.Core.Screen;
using Windows.UI.ViewManagement;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;
using Rect = EchoDiar.Core.Screen.ScreenGeometry.Rect;

namespace EchoDiar.Platform.Screen;

/// <summary>What <see cref="SourcePicker"/> picks: the order of its mode bar (macOS chooser rows).</summary>
public enum SourcePickerMode
{
    /// <summary>"Entire screen": hover highlights the monitor under the pointer, a click picks it.</summary>
    Display,

    /// <summary>"Window": hover highlights the window under the pointer, a click picks it.</summary>
    Window,

    /// <summary>"Portion of the screen": a drag on one monitor picks that rectangle.</summary>
    Portion,
}

/// <summary>The picker's texts, already localized by the caller (Platform has no Loc).</summary>
/// <param name="EntireScreen">Mode bar: "Entire screen".</param>
/// <param name="Window">Mode bar: "Window".</param>
/// <param name="Portion">Mode bar: "Portion of the screen".</param>
/// <param name="Caption">
/// Under the mode bar: "The recording starts as soon as you pick." (a new recording) or "Records it in this recording
/// from now on." (during a recording).
/// </param>
/// <param name="PressEscToCancel">Under the caption: "Press Esc to cancel".</param>
/// <param name="ClickToRecordDisplay">On the highlighted monitor: "Click to record this display".</param>
public sealed record SourcePickerTexts(
    string EntireScreen, string Window, string Portion, string Caption, string PressEscToCancel, string ClickToRecordDisplay);

/// <summary>
/// Picks a screen source on the screen itself (macOS <c>ScreenPickerOverlay</c> and <c>AreaSelectionOverlay</c>): one
/// topmost layered window per monitor, kept out of screen captures (WDA_EXCLUDEFROMCAPTURE). A mode bar at the top of
/// the monitor under the pointer switches between the macOS chooser's three choices (the chooser itself is this bar on
/// Windows), with the caption and the Esc hint below it. Display: the monitor under the pointer is highlighted, a click
/// picks it. Window: the topmost normal window under the pointer is highlighted (visible, not minimized, not cloaked,
/// not a tool window, at least 40 × 40 DIPs; the desktop and EchoDiar's own windows are never picked and hide what lies
/// behind them), a click picks it. Portion: a drag on one monitor draws a rectangle with its size in pixels; releasing
/// picks it (drags under 64 × 64 DIPs are ignored and the selection goes on). Esc, a right click, leaving EchoDiar
/// (another app activated), a display change or the token cancel. Nothing animates.
/// <para>
/// Threading: the overlays live on their own STA thread with its own message loop (per-monitor DPI aware v2, so every
/// coordinate is in physical pixels), never on the caller's UI thread. Every window, GDI object and the Esc hot key are
/// released before the task completes. One picker at a time.
/// </para>
/// </summary>
public static class SourcePicker
{
    /// <summary>
    /// Shows the overlays in <paramref name="mode"/> and completes with the picked source, or null when cancelled (Esc,
    /// right click, another app activated, a display change, <paramref name="cancellationToken"/>) or when the overlays
    /// could not be shown (<paramref name="log"/> says why). Never throws. The task completes on the picker thread,
    /// continuations run asynchronously. An <see cref="ScreenSourceKind.Area"/> region is in that monitor's pixels,
    /// clipped to it with even sides (<see cref="ScreenGeometry.AreaPixels"/>).
    /// </summary>
    public static Task<ScreenSource?> PickAsync(
        SourcePickerMode mode, SourcePickerTexts texts, CancellationToken cancellationToken = default, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var completion = new TaskCompletionSource<ScreenSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (cancellationToken.IsCancellationRequested)
        {
            completion.SetResult(null);
            return completion.Task;
        }
        var thread = new Thread(() => completion.SetResult(Overlays.Run(mode, texts, cancellationToken, log)))
        {
            IsBackground = true,
            Name = "EchoDiar source picker",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    /// <summary>One monitor's overlay: its window and the 32 bpp premultiplied DIB it is drawn from.</summary>
    private sealed unsafe class Overlay(Overlays owner, CaptureMonitor monitor)
    {
        public Overlays Owner { get; } = owner;

        public nint Monitor { get; } = monitor.Handle;

        /// <summary>The monitor in virtual-screen pixels; the overlay's client area, so local = monitor pixels.</summary>
        public Rect Bounds { get; } = monitor.Bounds;

        public HWND Hwnd;
        public double Scale = 1;
        public HDC Dc;
        public HBITMAP Bitmap;
        public HGDIOBJ OldBitmap;
        public HGDIOBJ OldFont;
        public HFONT TitleFont;
        public HFONT SmallFont;
        public uint* Bits;

        public int Width => Bounds.Width;

        public int Height => Bounds.Height;

        public int Px(double dips) => (int)Math.Round(dips * Scale);
    }

    /// <summary>What the pointer is over: its source, its frame in virtual-screen pixels and its label.</summary>
    private sealed record Target(ScreenSource Source, Rect Frame, string Title);

    /// <summary>One run of the picker, on its own thread.</summary>
    private sealed unsafe class Overlays
    {
        private const uint CancelMessage = PInvoke.WM_APP + 1;
        private const int EscapeHotKeyId = 1;
        private const double MinimumWindowSize = 40; // DIPs, macOS 40 points
        private const double MinimumAreaSize = 64; // DIPs, macOS 64 points
        private const uint Dark = 0xFF202020;
        private const uint White = 0x00FFFFFF; // COLORREF
        private const uint LightGray = 0x00CCCCCC; // COLORREF, macOS white at 0.8

        /// <summary>Rooted for the process's life: a collected delegate behind a live window class would crash.</summary>
        private static readonly WNDPROC Callback = WndProc;

        /// <summary>Live overlays by handle (the window procedure's lookup).</summary>
        private static readonly Dictionary<HWND, Overlay> Windows = [];

        private readonly SourcePickerTexts _texts;
        private readonly Action<string>? _log;
        private readonly List<Overlay> _overlays = [];
        private readonly HINSTANCE _instance = new(PInvoke.GetModuleHandle(default(PCWSTR)).Value);
        private readonly string _className = "EchoDiar.SourcePicker." + Guid.NewGuid().ToString("N");
        /// <summary>The system accent as 0x00RRGGBB.</summary>
        private readonly uint _accent = AccentColor();
        private readonly HCURSOR _arrow = PInvoke.LoadCursor(HINSTANCE.Null, PInvoke.IDC_ARROW);
        private readonly HCURSOR _cross = PInvoke.LoadCursor(HINSTANCE.Null, PInvoke.IDC_CROSS);
        private readonly HCURSOR _hand = PInvoke.LoadCursor(HINSTANCE.Null, PInvoke.IDC_HAND);
        private SourcePickerMode _mode;
        private bool _classRegistered;
        private bool _hotKey;
        private bool _activated;
        private bool _done;
        private ScreenSource? _result;
        private Target? _target;
        /// <summary>The overlay with the mode bar, its segments and the bar plus hint area, in its pixels.</summary>
        private Overlay? _bar;
        private Rect[] _segments = [];
        private Rect _barArea;
        /// <summary>A left press outside the bar in Display or Window mode: its release picks.</summary>
        private bool _pressed;
        /// <summary>The overlay a Portion drag runs on, its start and its rectangle, in that overlay's pixels.</summary>
        private Overlay? _drag;
        private int _originX, _originY;
        private Rect _selection;

        private Overlays(SourcePickerMode mode, SourcePickerTexts texts, Action<string>? log)
        {
            _mode = mode;
            _texts = texts;
            _log = log;
        }

        /// <summary>Shows the overlays, pumps messages until a pick or a cancel, and releases everything.</summary>
        public static ScreenSource? Run(SourcePickerMode mode, SourcePickerTexts texts, CancellationToken token, Action<string>? log)
        {
            Overlays? picker = null;
            try
            {
                // Physical pixels everywhere, whatever the process's manifest says.
                PInvoke.SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
                picker = new Overlays(mode, texts, log);
                if (!picker.Open()) return null;
                var post = picker._bar!.Hwnd;
                using (token.Register(() => PInvoke.PostMessage(post, CancelMessage, default, default)))
                {
                    picker.Loop();
                }
                return picker._result;
            }
            catch (Exception e)
            {
                log?.Invoke($"source picker: {e.GetType().Name} {e.Message}");
                return null;
            }
            finally
            {
                picker?.Close();
            }
        }

        private bool Open()
        {
            var monitors = GraphicsCapture.Monitors();
            if (monitors.Count == 0)
            {
                _log?.Invoke("source picker: no monitor");
                return false;
            }
            fixed (char* className = _className)
            {
                var windowClass = new WNDCLASSEXW
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                    lpfnWndProc = Callback,
                    hInstance = _instance,
                    lpszClassName = className,
                };
                if (PInvoke.RegisterClassEx(windowClass) == 0) throw new Win32Exception();
                _classRegistered = true;
            }
            foreach (var monitor in monitors) CreateOverlay(monitor);

            PInvoke.GetCursorPos(out var cursor);
            _bar = _overlays.FirstOrDefault(o => Contains(o.Bounds, cursor.X, cursor.Y))
                ?? _overlays.FirstOrDefault(o => monitors.Any(m => m.IsPrimary && m.Handle == o.Monitor))
                ?? _overlays[0];
            _target = NextTarget();
            foreach (var overlay in _overlays) Render(overlay);
            foreach (var overlay in _overlays) PInvoke.ShowWindow(overlay.Hwnd, SHOW_WINDOW_CMD.SW_SHOWNA);
            // For Esc: allowed while EchoDiar is in front (its chooser was just clicked). The hot key covers a start
            // from the tray or a global shortcut, where the overlays may not get the keyboard.
            PInvoke.SetForegroundWindow(_bar.Hwnd);
            _hotKey = PInvoke.RegisterHotKey(_bar.Hwnd, EscapeHotKeyId, HOT_KEY_MODIFIERS.MOD_NOREPEAT, (uint)VIRTUAL_KEY.VK_ESCAPE);
            return true;
        }

        private void CreateOverlay(CaptureMonitor monitor)
        {
            var overlay = new Overlay(this, monitor);
            var bounds = overlay.Bounds;
            fixed (char* className = _className)
            {
                overlay.Hwnd = PInvoke.CreateWindowEx(
                    WINDOW_EX_STYLE.WS_EX_LAYERED | WINDOW_EX_STYLE.WS_EX_TOPMOST | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW,
                    className, default(PCWSTR), WINDOW_STYLE.WS_POPUP,
                    bounds.X, bounds.Y, bounds.Width, bounds.Height, HWND.Null, HMENU.Null, _instance, null);
            }
            if (overlay.Hwnd.IsNull) throw new Win32Exception();
            _overlays.Add(overlay);
            lock (Windows) Windows[overlay.Hwnd] = overlay;

            if (!PInvoke.SetWindowDisplayAffinity(overlay.Hwnd, WINDOW_DISPLAY_AFFINITY.WDA_EXCLUDEFROMCAPTURE))
            {
                _log?.Invoke($"source picker: SetWindowDisplayAffinity failed, {Marshal.GetLastPInvokeError()}");
            }
            if (PInvoke.GetDpiForMonitor((HMONITOR)overlay.Monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out var dpi, out _).Succeeded
                && dpi > 0)
            {
                overlay.Scale = dpi / 96.0;
            }

            overlay.Dc = PInvoke.CreateCompatibleDC(HDC.Null);
            if (overlay.Dc.IsNull) throw new Win32Exception();
            var info = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)sizeof(BITMAPINFOHEADER),
                    biWidth = bounds.Width,
                    biHeight = -bounds.Height, // top-down rows
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0, // BI_RGB
                },
            };
            void* bits;
            overlay.Bitmap = PInvoke.CreateDIBSection(overlay.Dc, &info, DIB_USAGE.DIB_RGB_COLORS, &bits, default, 0);
            if (overlay.Bitmap.IsNull) throw new Win32Exception();
            overlay.Bits = (uint*)bits;
            overlay.OldBitmap = PInvoke.SelectObject(overlay.Dc, overlay.Bitmap);
            overlay.TitleFont = CreateFont(overlay.Px(13), 600); // FW_SEMIBOLD
            overlay.SmallFont = CreateFont(overlay.Px(11), 400); // FW_NORMAL
            overlay.OldFont = PInvoke.SelectObject(overlay.Dc, overlay.TitleFont);
            PInvoke.SetBkMode(overlay.Dc, BACKGROUND_MODE.TRANSPARENT);
        }

        private static HFONT CreateFont(int pixels, int weight)
        {
            // CreateFontIndirect, not CreateFont: CsWin32 projects CreateFont's charset/precision/quality
            // as byte enums while CreateFontW takes DWORDs, so the marshaller throws MarshalDirectiveException.
            var logFont = new LOGFONTW
            {
                lfHeight = -pixels,
                lfWeight = weight,
                lfCharSet = FONT_CHARSET.DEFAULT_CHARSET,
                lfOutPrecision = FONT_OUTPUT_PRECISION.OUT_DEFAULT_PRECIS,
                lfClipPrecision = FONT_CLIP_PRECISION.CLIP_DEFAULT_PRECIS,
                lfQuality = FONT_QUALITY.ANTIALIASED_QUALITY,
                lfFaceName = "Segoe UI",
            };
            var font = PInvoke.CreateFontIndirect(&logFont);
            return font.IsNull ? throw new Win32Exception() : font;
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

        /// <summary>Releases the hot key, every window and GDI object, and the class. Never throws.</summary>
        private void Close()
        {
            if (_hotKey && _bar is not null) PInvoke.UnregisterHotKey(_bar.Hwnd, EscapeHotKeyId);
            _hotKey = false;
            foreach (var overlay in _overlays)
            {
                lock (Windows) Windows.Remove(overlay.Hwnd);
                if (!PInvoke.DestroyWindow(overlay.Hwnd))
                {
                    _log?.Invoke($"source picker: DestroyWindow failed, {Marshal.GetLastPInvokeError()}");
                }
                if (!overlay.Dc.IsNull)
                {
                    if (!overlay.OldFont.IsNull) PInvoke.SelectObject(overlay.Dc, overlay.OldFont);
                    if (!overlay.OldBitmap.IsNull) PInvoke.SelectObject(overlay.Dc, overlay.OldBitmap);
                    PInvoke.DeleteDC(overlay.Dc);
                }
                if (!overlay.Bitmap.IsNull) PInvoke.DeleteObject(overlay.Bitmap);
                if (!overlay.TitleFont.IsNull) PInvoke.DeleteObject(overlay.TitleFont);
                if (!overlay.SmallFont.IsNull) PInvoke.DeleteObject(overlay.SmallFont);
            }
            _overlays.Clear();
            if (_classRegistered)
            {
                fixed (char* className = _className) PInvoke.UnregisterClass(className, _instance);
                _classRegistered = false;
            }
        }

        private void Finish(ScreenSource? result)
        {
            if (_done) return;
            _done = true;
            _result = result;
            // Wakes GetMessage: a sent message (WM_ACTIVATEAPP, WM_DISPLAYCHANGE) runs inside it without returning.
            if (_bar is not null) PInvoke.PostMessage(_bar.Hwnd, PInvoke.WM_NULL, default, default);
        }

        // MARK: - Messages

        private static LRESULT WndProc(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
        {
            Overlay? overlay;
            lock (Windows) Windows.TryGetValue(hwnd, out overlay);
            if (overlay is not null)
            {
                try
                {
                    if (overlay.Owner.Handle(overlay, message, wParam, lParam) is { } result) return result;
                }
                catch (Exception e)
                {
                    // An exception must not cross the native frame: the pick is cancelled instead.
                    overlay.Owner._log?.Invoke($"source picker: 0x{message:X4} threw, {e.GetType().Name} {e.Message}");
                    overlay.Owner.Finish(null);
                }
            }
            return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
        }

        private LRESULT? Handle(Overlay overlay, uint message, WPARAM wParam, LPARAM lParam)
        {
            switch (message)
            {
                case PInvoke.WM_MOUSEMOVE:
                    OnMove(overlay, X(lParam), Y(lParam));
                    return (LRESULT)0;
                case PInvoke.WM_LBUTTONDOWN:
                    OnDown(overlay, X(lParam), Y(lParam));
                    return (LRESULT)0;
                case PInvoke.WM_LBUTTONUP:
                    OnUp();
                    return (LRESULT)0;
                case PInvoke.WM_RBUTTONUP:
                case PInvoke.WM_HOTKEY:
                case PInvoke.WM_DISPLAYCHANGE:
                case PInvoke.WM_CLOSE:
                case CancelMessage:
                    Finish(null);
                    return (LRESULT)0;
                case PInvoke.WM_KEYDOWN when wParam.Value == (nuint)VIRTUAL_KEY.VK_ESCAPE:
                    Finish(null);
                    return (LRESULT)0;
                case PInvoke.WM_ACTIVATEAPP:
                    // Leaving EchoDiar cancels (macOS didResignActive), once the overlays were active at all.
                    if (wParam.Value != 0) _activated = true;
                    else if (_activated) Finish(null);
                    return (LRESULT)0;
                case PInvoke.WM_CAPTURECHANGED:
                    if (_drag == overlay)
                    {
                        _drag = null;
                        _selection = default;
                        Render(overlay);
                    }
                    return (LRESULT)0;
                case PInvoke.WM_SETCURSOR:
                    PInvoke.SetCursor(CursorAtPointer());
                    return (LRESULT)1;
                default:
                    return null;
            }
        }

        private static int X(LPARAM lParam) => (short)(lParam.Value & 0xFFFF);

        private static int Y(LPARAM lParam) => (short)((lParam.Value >> 16) & 0xFFFF);

        private void OnMove(Overlay overlay, int x, int y)
        {
            if (_drag == overlay)
            {
                x = Math.Clamp(x, 0, overlay.Width);
                y = Math.Clamp(y, 0, overlay.Height);
                _selection = new Rect(Math.Min(_originX, x), Math.Min(_originY, y), Math.Abs(x - _originX), Math.Abs(y - _originY));
                Render(overlay);
                return;
            }
            if (_mode != SourcePickerMode.Portion) UpdateTarget();
        }

        private void OnDown(Overlay overlay, int x, int y)
        {
            _pressed = false;
            if (overlay == _bar && Contains(_barArea, x, y))
            {
                for (var i = 0; i < _segments.Length; i++)
                {
                    if (Contains(_segments[i], x, y)) SwitchMode((SourcePickerMode)i);
                }
                return;
            }
            if (_mode != SourcePickerMode.Portion)
            {
                _pressed = true;
                return;
            }
            _drag = overlay;
            _originX = Math.Clamp(x, 0, overlay.Width);
            _originY = Math.Clamp(y, 0, overlay.Height);
            _selection = default;
            PInvoke.SetCapture(overlay.Hwnd);
        }

        private void OnUp()
        {
            if (_drag is { } overlay)
            {
                var selection = _selection;
                _drag = null; // before ReleaseCapture: its WM_CAPTURECHANGED must not find a drag
                _selection = default;
                PInvoke.ReleaseCapture();
                var minimum = overlay.Px(MinimumAreaSize);
                if (selection.Width >= minimum && selection.Height >= minimum
                    && ScreenGeometry.AreaPixels(selection, new ScreenGeometry.Size(overlay.Width, overlay.Height)) is { } area)
                {
                    Finish(ScreenSource.Area(overlay.Monitor, area));
                }
                else
                {
                    Render(overlay);
                }
                return;
            }
            if (!_pressed) return;
            _pressed = false;
            // A click picks what is under the pointer now; a click on nothing does nothing.
            if (NextTarget() is { } target) Finish(target.Source);
        }

        private void SwitchMode(SourcePickerMode mode)
        {
            if (mode == _mode) return;
            _mode = mode;
            _target = NextTarget();
            foreach (var overlay in _overlays) Render(overlay);
        }

        /// <summary>Highlights what is under the pointer; only the overlays whose highlight changes are redrawn.</summary>
        private void UpdateTarget()
        {
            var next = NextTarget();
            if (Equals(next, _target)) return;
            var old = _target;
            _target = next;
            foreach (var overlay in _overlays)
            {
                if (Touches(old, overlay) || Touches(next, overlay)) Render(overlay);
            }
        }

        private static bool Touches(Target? target, Overlay overlay) =>
            target is not null && !Intersect(target.Frame, overlay.Bounds).IsEmpty();

        private HCURSOR CursorAtPointer()
        {
            if (_bar is not null && PInvoke.GetCursorPos(out var cursor)
                && Contains(_barArea, cursor.X - _bar.Bounds.X, cursor.Y - _bar.Bounds.Y))
            {
                return _arrow;
            }
            return _mode == SourcePickerMode.Portion ? _cross : _hand;
        }

        // MARK: - Targets

        private Target? NextTarget()
        {
            if (_mode == SourcePickerMode.Portion) return null;
            if (!PInvoke.GetCursorPos(out var cursor)) return _target;
            var under = _overlays.FirstOrDefault(o => Contains(o.Bounds, cursor.X, cursor.Y));
            if (_mode == SourcePickerMode.Display)
            {
                // Between monitors (a gap in the layout) the last one stays.
                return under is null
                    ? _target
                    : new Target(ScreenSource.Display(under.Monitor), under.Bounds, _texts.ClickToRecordDisplay);
            }
            return WindowTarget(cursor.X, cursor.Y, under?.Scale ?? 1);
        }

        /// <summary>
        /// The topmost pickable window at (<paramref name="x"/>, <paramref name="y"/>), front to back; null over the
        /// desktop or over EchoDiar's own windows, which hide what lies behind them (macOS).
        /// </summary>
        private Target? WindowTarget(int x, int y, double scale)
        {
            var own = (uint)Environment.ProcessId;
            var shell = PInvoke.GetShellWindow();
            var minimum = (int)Math.Round(MinimumWindowSize * scale);
            HWND hit = default;
            Rect frame = default;
            uint hitProcess = 0;
            PInvoke.EnumWindows((window, _) =>
            {
                if (_overlays.Any(o => o.Hwnd == window) || !PInvoke.IsWindowVisible(window) || PInvoke.IsIconic(window)
                    || IsCloaked(window) || FrameOf(window) is not { } bounds || !Contains(bounds, x, y))
                {
                    return true;
                }
                PInvoke.GetWindowThreadProcessId(window, out var process);
                if (process == own || window == shell || IsDesktop(window)) return false;
                var style = (WINDOW_EX_STYLE)PInvoke.GetWindowLong(window, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
                // Tool windows (the taskbar, floating palettes) and click-through overlays are passed through, as
                // macOS skips windows off layer 0.
                if ((style & (WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_TRANSPARENT)) != 0
                    || bounds.Width < minimum || bounds.Height < minimum)
                {
                    return true;
                }
                hit = window;
                frame = bounds;
                hitProcess = process;
                return false;
            }, default);
            if (hit.IsNull) return null;
            var title = _target is { Source.Kind: ScreenSourceKind.Window } current && current.Source.Handle == (nint)hit
                ? current.Title
                : WindowTitle(hit, hitProcess);
            return new Target(ScreenSource.Window(hit), frame, title);
        }

        private static bool IsCloaked(HWND window)
        {
            uint cloaked = 0;
            return PInvoke.DwmGetWindowAttribute(window, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(uint)).Succeeded
                && cloaked != 0;
        }

        /// <summary>The visible frame (without the invisible resize borders), else the window rect.</summary>
        private static Rect? FrameOf(HWND window)
        {
            RECT rect;
            if (PInvoke.DwmGetWindowAttribute(window, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, &rect, (uint)sizeof(RECT)).Failed
                && !PInvoke.GetWindowRect(window, out rect))
            {
                return null;
            }
            return new Rect(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top);
        }

        /// <summary>The desktop's windows (Progman, and the WorkerW ones behind the icons).</summary>
        private static bool IsDesktop(HWND window)
        {
            var buffer = stackalloc char[16];
            var length = PInvoke.GetClassName(window, buffer, 16);
            var name = new string(buffer, 0, Math.Max(length, 0));
            return name is "Progman" or "WorkerW";
        }

        /// <summary>The window's title, else its process's name (macOS shows the app's name when there is no title).</summary>
        private static string WindowTitle(HWND window, uint process)
        {
            var length = PInvoke.GetWindowTextLength(window);
            if (length > 0)
            {
                var buffer = new char[length + 1];
                fixed (char* text = buffer)
                {
                    var read = PInvoke.GetWindowText(window, text, buffer.Length);
                    if (read > 0) return new string(text, 0, read);
                }
            }
            try
            {
                using var owner = Process.GetProcessById((int)process);
                return owner.ProcessName;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
            {
                return "";
            }
        }

        // MARK: - Drawing

        /// <summary>
        /// Draws <paramref name="overlay"/>: a light dim, the part of the highlight on this monitor (a light accent
        /// fill, a 2 DIP accent border and its label), the Portion rectangle with its size, and the mode bar. Every
        /// pixel has some alpha, so clicks never pass through to the window being picked.
        /// </summary>
        private void Render(Overlay overlay)
        {
            var whole = new Rect(0, 0, overlay.Width, overlay.Height);
            var highlight = _target is null
                ? default
                : Intersect(Offset(_target.Frame, -overlay.Bounds.X, -overlay.Bounds.Y), whole);
            var dim = _mode == SourcePickerMode.Portion || (_mode == SourcePickerMode.Display && highlight.IsEmpty())
                ? Premultiplied(0, 0.2)
                : Premultiplied(0, 0.06);
            Fill(overlay, whole, dim);
            if (!highlight.IsEmpty())
            {
                Fill(overlay, highlight, Premultiplied(_accent, 0.15));
                Stroke(overlay, highlight, overlay.Px(2), 0xFF000000 | _accent);
                if (_target!.Title.Length > 0) Label(overlay, highlight, _target.Title);
            }
            if (_drag == overlay && !_selection.IsEmpty())
            {
                Fill(overlay, _selection, Premultiplied(0, 1 / 255.0)); // nearly clear, still hit
                Stroke(overlay, _selection, 1, 0xFFFFFFFF);
                SizeLabel(overlay, _selection);
            }
            if (overlay == _bar) DrawBar(overlay);
            Present(overlay);
        }

        /// <summary>The title on a dark box, centred in <paramref name="area"/>.</summary>
        private void Label(Overlay overlay, Rect area, string title)
        {
            int padX = overlay.Px(10), padY = overlay.Px(6);
            var maxWidth = Math.Clamp(area.Width - 2 * padX - overlay.Px(16), overlay.Px(40), overlay.Px(420));
            var (width, height) = Measure(overlay, overlay.TitleFont, title);
            width = Math.Min(width, maxWidth);
            var box = new Rect(
                area.X + (area.Width - width) / 2 - padX, area.Y + (area.Height - height) / 2 - padY,
                width + 2 * padX, height + 2 * padY);
            Fill(overlay, box, Dark);
            Text(overlay, overlay.TitleFont, title, new Rect(box.X + padX, box.Y + padY, width, height), White);
            Opaque(overlay, box);
        }

        /// <summary>"800 × 600" below the rectangle, or inside its bottom edge when there is no room below (macOS).</summary>
        private static void SizeLabel(Overlay overlay, Rect selection)
        {
            var text = $"{selection.Width} × {selection.Height}";
            int padX = overlay.Px(6), padY = overlay.Px(3), gap = overlay.Px(4);
            var (width, height) = Measure(overlay, overlay.SmallFont, text);
            int boxWidth = width + 2 * padX, boxHeight = height + 2 * padY;
            var y = selection.Y + selection.Height + gap;
            if (y + boxHeight > overlay.Height) y = selection.Y + selection.Height - boxHeight - gap;
            var x = Math.Clamp(selection.X, 0, Math.Max(overlay.Width - boxWidth, 0));
            var box = new Rect(x, y, boxWidth, boxHeight);
            Fill(overlay, box, Dark);
            Text(overlay, overlay.SmallFont, text, new Rect(x + padX, y + padY, width, height), White);
            Opaque(overlay, box);
        }

        /// <summary>
        /// The mode bar centred at the top (the current mode in the accent colour), and below it the caption and the Esc
        /// hint. Records the segments and the whole area for the clicks.
        /// </summary>
        private void DrawBar(Overlay overlay)
        {
            string[] labels = [_texts.EntireScreen, _texts.Window, _texts.Portion];
            int padX = overlay.Px(14), padY = overlay.Px(7), inset = overlay.Px(4), top = overlay.Px(16), gap = overlay.Px(6);
            var sizes = labels.Select(label => Measure(overlay, overlay.TitleFont, label)).ToArray();
            var textHeight = sizes.Max(size => size.Height);
            var barWidth = sizes.Sum(size => size.Width + 2 * padX) + 2 * inset;
            var bar = new Rect((overlay.Width - barWidth) / 2, top, barWidth, textHeight + 2 * padY + 2 * inset);
            Fill(overlay, bar, Dark);
            var segments = new Rect[labels.Length];
            var x = bar.X + inset;
            for (var i = 0; i < labels.Length; i++)
            {
                segments[i] = new Rect(x, bar.Y + inset, sizes[i].Width + 2 * padX, textHeight + 2 * padY);
                if (i == (int)_mode) Fill(overlay, segments[i], 0xFF000000 | _accent);
                Text(overlay, overlay.TitleFont, labels[i],
                    new Rect(x + padX, bar.Y + inset + padY, sizes[i].Width, textHeight), White);
                x += segments[i].Width;
            }
            Opaque(overlay, bar);

            var caption = Measure(overlay, overlay.SmallFont, _texts.Caption);
            var escape = Measure(overlay, overlay.SmallFont, _texts.PressEscToCancel);
            var hintWidth = Math.Max(caption.Width, escape.Width);
            var hint = new Rect(
                (overlay.Width - hintWidth) / 2 - padX, bar.Y + bar.Height + gap,
                hintWidth + 2 * padX, caption.Height + escape.Height + 2 * padY);
            Fill(overlay, hint, Dark);
            Text(overlay, overlay.SmallFont, _texts.Caption, new Rect(hint.X + padX, hint.Y + padY, hintWidth, caption.Height), White);
            Text(overlay, overlay.SmallFont, _texts.PressEscToCancel,
                new Rect(hint.X + padX, hint.Y + padY + caption.Height, hintWidth, escape.Height), LightGray);
            Opaque(overlay, hint);

            _segments = segments;
            var left = Math.Min(bar.X, hint.X);
            _barArea = new Rect(left, bar.Y, Math.Max(bar.X + bar.Width, hint.X + hint.Width) - left, hint.Y + hint.Height - bar.Y);
        }

        private void Present(Overlay overlay)
        {
            PInvoke.GdiFlush();
            var destination = new System.Drawing.Point(overlay.Bounds.X, overlay.Bounds.Y);
            var size = new SIZE { cx = overlay.Width, cy = overlay.Height };
            var source = new System.Drawing.Point(0, 0);
            var blend = new BLENDFUNCTION
            {
                BlendOp = (byte)PInvoke.AC_SRC_OVER,
                SourceConstantAlpha = 255,
                AlphaFormat = (byte)PInvoke.AC_SRC_ALPHA,
            };
            if (!PInvoke.UpdateLayeredWindow(
                overlay.Hwnd, HDC.Null, destination, size, overlay.Dc, source, default, blend,
                UPDATE_LAYERED_WINDOW_FLAGS.ULW_ALPHA))
            {
                _log?.Invoke($"source picker: UpdateLayeredWindow failed, {Marshal.GetLastPInvokeError()}");
            }
        }

        /// <summary>The size of <paramref name="text"/> on one line.</summary>
        private static (int Width, int Height) Measure(Overlay overlay, HFONT font, string text)
        {
            PInvoke.SelectObject(overlay.Dc, font);
            var rect = default(RECT);
            fixed (char* chars = text)
            {
                PInvoke.DrawText(overlay.Dc, chars, text.Length, &rect,
                    DRAW_TEXT_FORMAT.DT_CALCRECT | DRAW_TEXT_FORMAT.DT_SINGLELINE | DRAW_TEXT_FORMAT.DT_NOPREFIX);
            }
            return (rect.right - rect.left, rect.bottom - rect.top);
        }

        /// <summary>
        /// <paramref name="text"/> centred on one line in <paramref name="area"/>, cut with an ellipsis when too long.
        /// GDI clears the alpha of the pixels it draws: <see cref="Opaque"/> the box afterwards.
        /// </summary>
        private static void Text(Overlay overlay, HFONT font, string text, Rect area, uint color)
        {
            PInvoke.SelectObject(overlay.Dc, font);
            PInvoke.SetTextColor(overlay.Dc, new COLORREF(color));
            var rect = new RECT { left = area.X, top = area.Y, right = area.X + area.Width, bottom = area.Y + area.Height };
            fixed (char* chars = text)
            {
                PInvoke.DrawText(overlay.Dc, chars, text.Length, &rect,
                    DRAW_TEXT_FORMAT.DT_SINGLELINE | DRAW_TEXT_FORMAT.DT_CENTER | DRAW_TEXT_FORMAT.DT_VCENTER
                    | DRAW_TEXT_FORMAT.DT_NOPREFIX | DRAW_TEXT_FORMAT.DT_END_ELLIPSIS);
            }
        }

        /// <summary>Sets full alpha in <paramref name="area"/> (an opaque box with GDI text in it).</summary>
        private static void Opaque(Overlay overlay, Rect area)
        {
            PInvoke.GdiFlush();
            var clipped = Intersect(area, new Rect(0, 0, overlay.Width, overlay.Height));
            for (var y = clipped.Y; y < clipped.Y + clipped.Height; y++)
            {
                var row = overlay.Bits + (long)y * overlay.Width + clipped.X;
                for (var x = 0; x < clipped.Width; x++) row[x] |= 0xFF000000;
            }
        }

        private static void Fill(Overlay overlay, Rect area, uint pixel)
        {
            var clipped = Intersect(area, new Rect(0, 0, overlay.Width, overlay.Height));
            for (var y = clipped.Y; y < clipped.Y + clipped.Height; y++)
            {
                new Span<uint>(overlay.Bits + (long)y * overlay.Width + clipped.X, clipped.Width).Fill(pixel);
            }
        }

        private static void Stroke(Overlay overlay, Rect area, int thickness, uint pixel)
        {
            Fill(overlay, area with { Height = thickness }, pixel);
            Fill(overlay, area with { Y = area.Y + area.Height - thickness, Height = thickness }, pixel);
            Fill(overlay, area with { Width = thickness }, pixel);
            Fill(overlay, area with { X = area.X + area.Width - thickness, Width = thickness }, pixel);
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
                return 0x0078D4; // cosmetic: never fails the pick
            }
        }

        private static Rect Offset(Rect rect, int dx, int dy) => rect with { X = rect.X + dx, Y = rect.Y + dy };

        private static bool Contains(Rect rect, int x, int y) =>
            x >= rect.X && x < rect.X + rect.Width && y >= rect.Y && y < rect.Y + rect.Height;

        private static Rect Intersect(Rect a, Rect b)
        {
            int left = Math.Max(a.X, b.X), top = Math.Max(a.Y, b.Y);
            int right = Math.Min(a.X + a.Width, b.X + b.Width), bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
            return right <= left || bottom <= top ? default : new Rect(left, top, right - left, bottom - top);
        }
    }
}

file static class RectExtensions
{
    public static bool IsEmpty(this ScreenGeometry.Rect rect) => rect.Width <= 0 || rect.Height <= 0;
}
