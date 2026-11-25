using System.Runtime.InteropServices;
using EchoDiar.Core.Screen;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.WinRT.Graphics.Capture;
using WinRT;

namespace EchoDiar.Platform.Screen;

/// <summary>Which D3D11 driver a <see cref="CaptureDevice"/> runs on (the probe's <c>d3dDevice</c>).</summary>
public enum CaptureDeviceKind
{
    Hardware,

    /// <summary>The WARP software rasterizer, when no hardware device could be created (a VM without a GPU).</summary>
    Warp,
}

/// <summary>A monitor that can be captured.</summary>
/// <param name="Handle">The HMONITOR, for <see cref="GraphicsCapture.ForMonitor"/>.</param>
/// <param name="DeviceName">The GDI device name (<c>\\.\DISPLAY1</c>).</param>
/// <param name="Bounds">The monitor in virtual-screen pixels (physical pixels in a per-monitor DPI aware process).</param>
public readonly record struct CaptureMonitor(nint Handle, string DeviceName, ScreenGeometry.Rect Bounds, bool IsPrimary);

/// <summary>
/// The D3D11 device (BGRA support, for Direct2D) that Windows.Graphics.Capture frames and the compositor share:
/// hardware first, WARP when that fails. Create, use and dispose it off the UI thread (MTA): the COM wrappers are
/// bound to the apartment that created them.
/// </summary>
public sealed class CaptureDevice : IDisposable
{
    private bool _disposed;

    private CaptureDevice(ID3D11Device device, ID3D11DeviceContext context, CaptureDeviceKind kind, IDirect3DDevice direct3DDevice)
    {
        Device = device;
        Context = context;
        Kind = kind;
        Direct3DDevice = direct3DDevice;
    }

    internal ID3D11Device Device { get; }

    internal ID3D11DeviceContext Context { get; }

    public CaptureDeviceKind Kind { get; }

    /// <summary>The device as WinRT sees it, for <c>Direct3D11CaptureFramePool.CreateFreeThreaded</c>.</summary>
    public IDirect3DDevice Direct3DDevice { get; }

    /// <summary>A hardware device, else WARP. Throws <see cref="COMException"/> (both HRESULTs) when neither can be created.</summary>
    public static CaptureDevice Create()
    {
        var hardware = PInvoke.D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE, HMODULE.Null,
            D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT, [], PInvoke.D3D11_SDK_VERSION, out var device, out var context);
        var kind = CaptureDeviceKind.Hardware;
        if (hardware.Failed)
        {
            var warp = PInvoke.D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_WARP, HMODULE.Null,
                D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT, [], PInvoke.D3D11_SDK_VERSION, out device, out context);
            if (warp.Failed)
            {
                throw new COMException(
                    $"D3D11CreateDevice failed: hardware 0x{hardware.Value:X8}, WARP 0x{warp.Value:X8}", warp.Value);
            }
            kind = CaptureDeviceKind.Warp;
        }

        try
        {
            return new CaptureDevice(device, context, kind, WrapDevice(device));
        }
        catch
        {
            Marshal.FinalReleaseComObject(context);
            Marshal.FinalReleaseComObject(device);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Direct3DDevice.Dispose();
        Marshal.FinalReleaseComObject(Context);
        Marshal.FinalReleaseComObject(Device);
    }

    /// <summary>The WinRT IDirect3DDevice over <paramref name="device"/> (CreateDirect3D11DeviceFromDXGIDevice).</summary>
    private static IDirect3DDevice WrapDevice(ID3D11Device device)
    {
        // The cast is a QueryInterface on the same wrapper: releasing the device releases it.
        var dxgi = (IDXGIDevice)device;
        var hr = PInvoke.CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable);
        if (hr.Failed) throw new COMException($"CreateDirect3D11DeviceFromDXGIDevice failed: 0x{hr.Value:X8}", hr.Value);
        var pointer = Marshal.GetIUnknownForObject(inspectable);
        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(pointer); // C#/WinRT holds its own reference
        }
        finally
        {
            Marshal.Release(pointer);
            Marshal.ReleaseComObject(inspectable);
        }
    }
}

/// <summary>Windows.Graphics.Capture helpers: support, monitors, and capture items for a monitor or a window.</summary>
public static class GraphicsCapture
{
    // IGraphicsCaptureItem, the default interface of GraphicsCaptureItem (windows.graphics.capture.idl).
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    public static bool IsSupported() => GraphicsCaptureSession.IsSupported();

    /// <summary>The monitors, in the order Windows enumerates them.</summary>
    public static unsafe IReadOnlyList<CaptureMonitor> Monitors()
    {
        var monitors = new List<CaptureMonitor>();
        PInvoke.EnumDisplayMonitors(HDC.Null, null, (monitor, _, _, _) =>
        {
            var info = new MONITORINFOEXW { monitorInfo = { cbSize = (uint)sizeof(MONITORINFOEXW) } };
            if (PInvoke.GetMonitorInfo(monitor, (MONITORINFO*)&info))
            {
                var bounds = info.monitorInfo.rcMonitor;
                monitors.Add(new CaptureMonitor(
                    monitor,
                    info.szDevice.ToString(),
                    new ScreenGeometry.Rect(bounds.left, bounds.top, bounds.right - bounds.left, bounds.bottom - bounds.top),
                    (info.monitorInfo.dwFlags & PInvoke.MONITORINFOF_PRIMARY) != 0));
            }
            return true;
        }, 0);
        return monitors;
    }

    /// <summary>
    /// A capture item for the monitor <paramref name="monitor"/> (HMONITOR). Throws <see cref="COMException"/> with
    /// the HRESULT.
    /// </summary>
    public static GraphicsCaptureItem ForMonitor(nint monitor) => CreateItem(monitor, isWindow: false);

    /// <summary>
    /// A capture item for the top-level window <paramref name="window"/> (HWND). Throws <see cref="COMException"/>
    /// with the HRESULT (a closed or invalid window, too).
    /// </summary>
    public static GraphicsCaptureItem ForWindow(nint window) => CreateItem(window, isWindow: true);

    private static unsafe GraphicsCaptureItem CreateItem(nint handle, bool isWindow)
    {
        IGraphicsCaptureItemInterop? interop = null;
        void* item = null;
        try
        {
            interop = ItemInterop();
            var iid = GraphicsCaptureItemIid;
            if (isWindow) interop.CreateForWindow((HWND)handle, &iid, out item);
            else interop.CreateForMonitor((HMONITOR)handle, &iid, out item);
            return GraphicsCaptureItem.FromAbi((nint)item); // C#/WinRT holds its own reference
        }
        catch (Exception e)
        {
            // .NET maps some HRESULTs (E_INVALIDARG for a bad handle) to other exception types; HResult keeps the code.
            var what = isWindow ? "CreateForWindow" : "CreateForMonitor";
            throw new COMException($"GraphicsCaptureItem {what} 0x{handle:X} failed: 0x{e.HResult:X8} {e.Message}", e.HResult);
        }
        finally
        {
            if (item != null) Marshal.Release((nint)item);
            if (interop is not null) Marshal.ReleaseComObject(interop);
        }
    }

    /// <summary>GraphicsCaptureItem's activation factory as IGraphicsCaptureItemInterop (a built-in COM wrapper).</summary>
    private static IGraphicsCaptureItemInterop ItemInterop()
    {
        using var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem", typeof(IGraphicsCaptureItemInterop).GUID);
        return (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factory.ThisPtr);
    }
}
