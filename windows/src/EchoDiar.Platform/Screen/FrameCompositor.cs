using System.Globalization;
using System.Runtime.InteropServices;
using EchoDiar.Core.Screen;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Win32;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.DirectWrite;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.System.WinRT.Direct3D11;
using WinRT;

namespace EchoDiar.Platform.Screen;

/// <summary>
/// Draws one video frame of a fixed, even output size with Direct2D on the <see cref="CaptureDevice"/>: black, then
/// a captured texture aspect-fitted (black bars) or the placeholder (macOS <c>ScreenPlaceholder</c>: the localized
/// text in bold white letters of about height / 12, centred, wrapped to 80 % of the width), then <see cref="Overlay"/>.
/// The last captured picture is kept without overlays, so <see cref="Recompose"/> draws new overlays on it (a moving
/// camera bubble, fading click rings) when no new capture frame came. <see cref="ReadPixels"/> copies the last drawn
/// frame to the CPU (staging texture) as top-down BGRA rows.
/// </summary>
/// <remarks>
/// Not thread-safe; create, use and dispose it off the UI thread (MTA), as the device. It turns on the device's
/// multithread protection when offered, since Windows.Graphics.Capture uses the same device on its own threads.
/// </remarks>
internal sealed class FrameCompositor : IDisposable
{
    private const float Dpi = 96; // device-independent pixels are pixels
    private const int SampleStep = 16; // Sample reads every 16th pixel of every 16th row

    private static readonly D2D1_COLOR_F Black = new() { a = 1 };
    private static readonly D2D1_COLOR_F White = new() { r = 1, g = 1, b = 1, a = 1 };
    private static readonly D2D1_COLOR_F BubblePlaceholder = Gray(0x3B); // CameraBubbleWindow's placeholder grey
    private static readonly D2D1_COLOR_F RingColor = new() { r = 1, g = 0xD6 / 255f, b = 0x0A / 255f, a = 1 }; // yellow

    // Every COM object created here, released in reverse order by Dispose. Interface casts of these (QueryInterface
    // on the same wrapper) are not listed: releasing the wrapper releases them.
    private readonly List<object> _owned = [];
    private readonly ID3D11Device _device; // the CaptureDevice's, not owned
    private readonly ID3D11DeviceContext _context;
    private readonly ID2D1DeviceContext _target;
    private readonly ID3D11Texture2D _render;
    private readonly ID3D11Texture2D _staging;
    private readonly ID2D1Bitmap1 _targetBitmap; // the output, drawn on
    private readonly ID2D1Bitmap1 _base; // the last captured picture, aspect-fitted, without overlays
    private readonly ID2D1SolidColorBrush _overlayBrush;
    private readonly IDWriteTextFormat _textFormat;
    private readonly ID2D1SolidColorBrush _textBrush;
    private readonly string _placeholderText;
    private bool _hasBase;
    private bool _disposed;

    // The camera bubble's latest frame (replaced when its size changes), released by Dispose.
    private ID2D1Bitmap? _camera;
    private ID2D1BitmapBrush1? _cameraBrush;
    private int _cameraWidth;
    private int _cameraHeight;

    /// <param name="size">The output size, even and positive.</param>
    /// <param name="placeholderText">Already localized ("Screen recording off").</param>
    public FrameCompositor(CaptureDevice device, ScreenGeometry.Size size, string placeholderText)
    {
        if (size.Width <= 0 || size.Height <= 0 || size.Width % 2 != 0 || size.Height % 2 != 0)
        {
            throw new ArgumentException($"The output size must be even and positive: {size.Width}x{size.Height}");
        }
        Size = size;
        _placeholderText = placeholderText;
        _device = device.Device;
        _context = device.Context;
        try
        {
            // Documented on the immediate context; drivers also answer it on the device. Without it, no protection.
            (device.Context as ID3D11Multithread ?? device.Device as ID3D11Multithread)?.SetMultithreadProtected(true);

            _render = Own(CreateTexture(device.Device, size, D3D11_USAGE.D3D11_USAGE_DEFAULT,
                D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE, 0));
            var baseTexture = Own(CreateTexture(device.Device, size, D3D11_USAGE.D3D11_USAGE_DEFAULT,
                D3D11_BIND_FLAG.D3D11_BIND_RENDER_TARGET | D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE, 0));
            _staging = Own(CreateTexture(device.Device, size, D3D11_USAGE.D3D11_USAGE_STAGING, 0,
                D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ));

            PInvoke.D2D1CreateFactory(D2D1_FACTORY_TYPE.D2D1_FACTORY_TYPE_SINGLE_THREADED, typeof(ID2D1Factory1).GUID,
                null, out var factory).ThrowOnFailure();
            var d2dFactory = (ID2D1Factory1)Own(factory);
            d2dFactory.CreateDevice((IDXGIDevice)device.Device, out var d2dDevice);
            Own(d2dDevice).CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS.D2D1_DEVICE_CONTEXT_OPTIONS_NONE, out var target);
            _target = Own(target);
            _target.SetDpi(Dpi, Dpi);
            _target.CreateBitmapFromDxgiSurface((IDXGISurface)_render,
                BitmapProperties(D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED,
                    D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_CANNOT_DRAW),
                out var targetBitmap);
            _targetBitmap = Own(targetBitmap);
            _target.SetTarget(_targetBitmap);
            // A target too (the source is drawn on it), and drawable (it is drawn on the output).
            _target.CreateBitmapFromDxgiSurface((IDXGISurface)baseTexture,
                BitmapProperties(D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED, D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_TARGET),
                out var baseBitmap);
            _base = Own(baseBitmap);
            _target.CreateSolidColorBrush(White, null, out var brush);
            _textBrush = Own(brush);
            _target.CreateSolidColorBrush(White, null, out var overlayBrush);
            _overlayBrush = Own(overlayBrush);

            PInvoke.DWriteCreateFactory(DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_SHARED, out IDWriteFactory dwrite).ThrowOnFailure();
            var locale = CultureInfo.CurrentUICulture.Name;
            Own(dwrite).CreateTextFormat("Segoe UI", null, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_BOLD,
                DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH.DWRITE_FONT_STRETCH_NORMAL, size.Height / 12f,
                locale.Length > 0 ? locale : "en-us", out var textFormat);
            _textFormat = Own(textFormat);
            _textFormat.SetTextAlignment(DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_CENTER);
            _textFormat.SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT.DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>The output size, in pixels.</summary>
    public ScreenGeometry.Size Size { get; }

    /// <summary>
    /// Drawn on top of every frame (camera bubble, click rings), with the device context in pixels and the output
    /// size; inside the compositor's BeginDraw/EndDraw. Null: nothing.
    /// </summary>
    public Action<ID2D1DeviceContext, ScreenGeometry.Size>? Overlay { get; set; }

    /// <summary>
    /// Draws a frame into the output texture: <paramref name="source"/> cropped to <paramref name="crop"/> (its
    /// pixels, clipped to the texture) and aspect-fitted, or the placeholder when <paramref name="source"/> is null.
    /// An empty crop leaves the frame black. Throws <see cref="COMException"/> when Direct2D fails.
    /// </summary>
    public void Compose(ID3D11Texture2D? source, ScreenGeometry.Rect crop)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Draw(() =>
        {
            if (source is null)
            {
                _target.Clear(Black);
                DrawPlaceholder();
                _hasBase = false;
                return;
            }
            _target.SetTarget(_base);
            try
            {
                _target.Clear(Black);
                DrawSource(source, crop);
            }
            finally
            {
                _target.SetTarget(_targetBitmap);
            }
            _hasBase = true;
            DrawBase();
        });
    }

    /// <summary>
    /// Draws the last captured picture again with <see cref="Overlay"/> on it (no new capture frame). False, and
    /// nothing drawn, when the last frame was the placeholder or none was composed. Throws <see cref="COMException"/>
    /// when Direct2D fails.
    /// </summary>
    public bool Recompose()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_hasBase) return false;
        Draw(DrawBase);
        return true;
    }

    /// <summary>
    /// Keeps <paramref name="bgra"/> (top-down, <paramref name="stride"/> bytes a row, opaque) as the camera frame
    /// <see cref="DrawBubble"/> draws. Throws <see cref="COMException"/> when Direct2D fails.
    /// </summary>
    public unsafe void SetCameraFrame(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (width <= 0 || height <= 0 || stride < width * 4 || bgra.Length < (long)stride * (height - 1) + width * 4)
        {
            throw new ArgumentException($"A {width}x{height} camera frame needs rows of {width * 4} bytes: stride {stride}, {bgra.Length} bytes");
        }
        fixed (byte* pixels = bgra)
        {
            if (_camera is not null && width == _cameraWidth && height == _cameraHeight)
            {
                _camera.CopyFromMemory(null, pixels, (uint)stride);
                return;
            }
            ReleaseCamera();
            // The blittable D2D1_BITMAP_PROPERTIES overload (no COM field to marshal, unlike _PROPERTIES1).
            var properties = new D2D1_BITMAP_PROPERTIES
            {
                pixelFormat = new D2D1_PIXEL_FORMAT
                {
                    format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED,
                },
                dpiX = Dpi,
                dpiY = Dpi,
            };
            _target.CreateBitmap(new D2D_SIZE_U { width = (uint)width, height = (uint)height }, pixels, (uint)stride,
                &properties, out ID2D1Bitmap camera);
            _camera = camera;
            _target.CreateBitmapBrush(camera, new D2D1_BITMAP_BRUSH_PROPERTIES1
            {
                extendModeX = D2D1_EXTEND_MODE.D2D1_EXTEND_MODE_CLAMP,
                extendModeY = D2D1_EXTEND_MODE.D2D1_EXTEND_MODE_CLAMP,
                interpolationMode = D2D1_INTERPOLATION_MODE.D2D1_INTERPOLATION_MODE_LINEAR,
            }, null, out var brush);
            _cameraBrush = brush;
            _cameraWidth = width;
            _cameraHeight = height;
        }
    }

    /// <summary>
    /// The camera bubble in <paramref name="bounds"/> (output pixels): the camera frame's centred square in a circle,
    /// or the grey placeholder without <paramref name="camera"/> or a frame. Inside <see cref="Overlay"/> only.
    /// </summary>
    public unsafe void DrawBubble(ScreenGeometry.Rect bounds, bool camera)
    {
        float width = bounds.Width, height = bounds.Height;
        var ellipse = new D2D1_ELLIPSE
        {
            point = new D2D_POINT_2F { x = bounds.X + width / 2, y = bounds.Y + height / 2 },
            radiusX = width / 2,
            radiusY = height / 2,
        };
        if (!camera || _cameraBrush is null)
        {
            _overlayBrush.SetColor(BubblePlaceholder);
            _target.FillEllipse(ellipse, _overlayBrush);
            return;
        }
        // The brush maps the frame's centred square onto the bubble.
        float side = Math.Min(_cameraWidth, _cameraHeight);
        float scaleX = width / side, scaleY = height / side;
        var transform = new D2D_MATRIX_3X2_F();
        transform.m11 = scaleX;
        transform.m22 = scaleY;
        transform.dx = bounds.X - (_cameraWidth - side) / 2 * scaleX;
        transform.dy = bounds.Y - (_cameraHeight - side) / 2 * scaleY;
        _cameraBrush.SetTransform(&transform);
        _target.FillEllipse(ellipse, _cameraBrush);
    }

    /// <summary>
    /// A click ring at (<paramref name="x"/>, <paramref name="y"/>), output pixels: yellow, with a dark edge on both
    /// sides so it reads on any background. Inside <see cref="Overlay"/> only.
    /// </summary>
    public void DrawRing(float x, float y, float radius, float stroke, float opacity)
    {
        var ellipse = new D2D1_ELLIPSE { point = new D2D_POINT_2F { x = x, y = y }, radiusX = radius, radiusY = radius };
        _overlayBrush.SetColor(new D2D1_COLOR_F { a = 0.55f * opacity });
        _target.DrawEllipse(ellipse, _overlayBrush, stroke * 2);
        _overlayBrush.SetColor(RingColor with { a = 0.95f * opacity });
        _target.DrawEllipse(ellipse, _overlayBrush, stroke);
    }

    /// <summary>
    /// Copies the last composed frame into <paramref name="destination"/> (at least width * 4 * height bytes): rows
    /// top-down, width * 4 bytes apart.
    /// </summary>
    public unsafe void ReadPixels(Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var row = Size.Width * 4;
        if (destination.Length < row * Size.Height)
        {
            throw new ArgumentException($"A {Size.Width}x{Size.Height} frame needs {row * Size.Height} bytes", nameof(destination));
        }
        _context.CopyResource(_staging, _render);
        _context.Map(_staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, out var mapped);
        try
        {
            var data = (byte*)mapped.pData;
            for (var y = 0; y < Size.Height; y++)
            {
                new ReadOnlySpan<byte>(data + (long)y * mapped.RowPitch, row).CopyTo(destination.Slice(y * row, row));
            }
        }
        finally
        {
            _context.Unmap(_staging, 0);
        }
    }

    /// <summary>
    /// Sparse statistics of the raw <paramref name="source"/> (a capture frame as Windows.Graphics.Capture gave it,
    /// BGRA, before any drawing) over its top-left <paramref name="width"/> x <paramref name="height"/> pixels: every
    /// 16th pixel of every 16th row. Reads a one-off staging copy of the whole texture (released here), so call it
    /// rarely (once per capture session). Null when that area is empty. Throws <see cref="COMException"/> when D3D fails.
    /// </summary>
    public unsafe ScreenFrameSample? Sample(ID3D11Texture2D source, int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        source.GetDesc(out var desc);
        width = Math.Min(width, (int)desc.Width);
        height = Math.Min(height, (int)desc.Height);
        if (width <= 0 || height <= 0) return null;

        desc.Usage = D3D11_USAGE.D3D11_USAGE_STAGING;
        desc.BindFlags = 0;
        desc.CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ;
        desc.MiscFlags = 0;
        _device.CreateTexture2D(desc, null, out var staging);
        try
        {
            _context.CopyResource(staging, source);
            _context.Map(staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, out var mapped);
            try
            {
                long red = 0, green = 0, blue = 0;
                int minAlpha = byte.MaxValue, maxAlpha = 0, count = 0, lit = 0;
                var data = (byte*)mapped.pData;
                for (var y = 0; y < height; y += SampleStep)
                {
                    var row = data + (long)y * mapped.RowPitch;
                    for (var x = 0; x < width; x += SampleStep)
                    {
                        var pixel = row + x * 4;
                        blue += pixel[0];
                        green += pixel[1];
                        red += pixel[2];
                        minAlpha = Math.Min(minAlpha, pixel[3]);
                        maxAlpha = Math.Max(maxAlpha, pixel[3]);
                        if (pixel[0] != 0 || pixel[1] != 0 || pixel[2] != 0) lit++;
                        count++;
                    }
                }
                return new ScreenFrameSample((double)red / count, (double)green / count, (double)blue / count,
                    minAlpha, maxAlpha, count, Black: lit * 100 <= count); // the pointer may light a few
            }
            finally
            {
                _context.Unmap(staging, 0);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(staging);
        }
    }

    /// <summary>
    /// The D3D11 texture behind a capture frame's surface (<c>Direct3D11CaptureFrame.Surface</c>), through
    /// IDirect3DDxgiInterfaceAccess. The caller releases it (<see cref="Marshal.ReleaseComObject"/>) before the frame
    /// is disposed.
    /// </summary>
    public static unsafe ID3D11Texture2D Texture(IDirect3DSurface surface)
    {
        var unknown = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(unknown);
            try
            {
                var iid = typeof(ID3D11Texture2D).GUID;
                access.GetInterface(&iid, out var texture);
                try
                {
                    return (ID3D11Texture2D)Marshal.GetObjectForIUnknown((nint)texture);
                }
                finally
                {
                    Marshal.Release((nint)texture);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(access);
            }
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseCamera();
        if (_target is not null) _target.SetTarget(null);
        for (var i = _owned.Count - 1; i >= 0; i--) Marshal.ReleaseComObject(_owned[i]);
        _owned.Clear();
    }

    /// <summary>BeginDraw, <paramref name="draw"/> on the output, <see cref="Overlay"/>, EndDraw.</summary>
    private unsafe void Draw(Action draw)
    {
        _target.BeginDraw();
        try
        {
            draw();
            Overlay?.Invoke(_target, Size);
        }
        catch
        {
            _target.EndDraw(null, null); // so the next BeginDraw works; the drawing error is the one thrown
            throw;
        }
        var hr = _target.EndDraw(null, null);
        if (hr.Failed) throw new COMException($"ID2D1DeviceContext.EndDraw failed: 0x{hr.Value:X8}", hr.Value);
    }

    /// <summary>The kept captured picture on the output, 1:1 (both are the output size).</summary>
    private void DrawBase()
    {
        _target.Clear(Black);
        _target.DrawBitmap(_base, Rect(0, 0, Size.Width, Size.Height), 1,
            D2D1_BITMAP_INTERPOLATION_MODE.D2D1_BITMAP_INTERPOLATION_MODE_NEAREST_NEIGHBOR, null);
    }

    private void ReleaseCamera()
    {
        if (_cameraBrush is not null) Marshal.ReleaseComObject(_cameraBrush);
        if (_camera is not null) Marshal.ReleaseComObject(_camera);
        _cameraBrush = null;
        _camera = null;
        _cameraWidth = 0;
        _cameraHeight = 0;
    }

    private void DrawSource(ID3D11Texture2D source, ScreenGeometry.Rect crop)
    {
        source.GetDesc(out var desc);
        int left = Math.Clamp(crop.X, 0, (int)desc.Width), top = Math.Clamp(crop.Y, 0, (int)desc.Height);
        int right = Math.Clamp(crop.X + crop.Width, left, (int)desc.Width);
        int bottom = Math.Clamp(crop.Y + crop.Height, top, (int)desc.Height);
        if (right - left <= 0 || bottom - top <= 0) return;

        // Captured frames are opaque screen content: their alpha is not a coverage to blend with (a monitor's frame
        // may carry any alpha, 0 included), so it is ignored and every pixel is drawn as opaque.
        _target.CreateBitmapFromDxgiSurface((IDXGISurface)source,
            BitmapProperties(D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_IGNORE, D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_NONE),
            out var bitmap);
        try
        {
            var fit = ScreenGeometry.AspectFit(new ScreenGeometry.Size(right - left, bottom - top), Size);
            _target.DrawBitmap(bitmap, Rect(fit.X, fit.Y, fit.Width, fit.Height), 1,
                D2D1_BITMAP_INTERPOLATION_MODE.D2D1_BITMAP_INTERPOLATION_MODE_LINEAR,
                Rect(left, top, right - left, bottom - top));
        }
        finally
        {
            Marshal.ReleaseComObject(bitmap);
        }
    }

    private void DrawPlaceholder()
    {
        if (_placeholderText.Length == 0) return;
        var layout = Rect(Size.Width * 0.1f, 0, Size.Width * 0.8f, Size.Height);
        _target.DrawText(_placeholderText, (uint)_placeholderText.Length, _textFormat, layout, _textBrush,
            D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_NONE, DWRITE_MEASURING_MODE.DWRITE_MEASURING_MODE_NATURAL);
    }

    private T Own<T>(T com) where T : class
    {
        _owned.Add(com);
        return com;
    }

    private static ID3D11Texture2D CreateTexture(ID3D11Device device, ScreenGeometry.Size size, D3D11_USAGE usage,
        D3D11_BIND_FLAG bind, D3D11_CPU_ACCESS_FLAG cpuAccess)
    {
        var desc = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)size.Width,
            Height = (uint)size.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
            Usage = usage,
            BindFlags = bind,
            CPUAccessFlags = cpuAccess,
        };
        device.CreateTexture2D(desc, null, out var texture);
        return texture;
    }

    private static D2D1_BITMAP_PROPERTIES1_unmanaged BitmapProperties(D2D1_ALPHA_MODE alpha, D2D1_BITMAP_OPTIONS options) => new()
    {
        pixelFormat = new D2D1_PIXEL_FORMAT { format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, alphaMode = alpha },
        dpiX = Dpi,
        dpiY = Dpi,
        bitmapOptions = options,
    };

    private static D2D1_COLOR_F Gray(int value) => new() { r = value / 255f, g = value / 255f, b = value / 255f, a = 1 };

    private static D2D_RECT_F Rect(float x, float y, float width, float height) =>
        new() { left = x, top = y, right = x + width, bottom = y + height };
}
