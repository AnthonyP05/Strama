using System.ComponentModel;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Strama.Encode;

/// <summary>
/// Creates a D3D11 device bound to a specific DXGI adapter.
///
/// Why this exists: Vortice.Direct3D11.D3D11.D3D11CreateDevice exposes only
/// driver-type-based overloads, which always select the system's default
/// adapter. For cross-adapter scenarios (e.g. capturing from the iGPU while
/// encoding on the dGPU) we need to bind D3D11 to a specific IDXGIAdapter —
/// so this wraps the native d3d11!D3D11CreateDevice directly.
/// </summary>
internal static class D3D11AdapterDevice
{
    private const uint D3D11_SDK_VERSION = 7;

    [DllImport("d3d11.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int D3D11CreateDevice(
        IntPtr pAdapter,
        DriverType driverType,
        IntPtr software,
        uint flags,
        IntPtr pFeatureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out IntPtr ppDevice,
        out FeatureLevel pFeatureLevel,
        out IntPtr ppImmediateContext);

    /// <summary>
    /// Creates a D3D11 device on the given adapter. Passing null falls back to
    /// the default hardware adapter (matches stock D3D11CreateDevice behavior).
    /// </summary>
    public static (ID3D11Device device, ID3D11DeviceContext context) Create(
        IDXGIAdapter? adapter,
        DeviceCreationFlags flags = DeviceCreationFlags.None)
    {
        IntPtr     adapterPtr = adapter?.NativePointer ?? IntPtr.Zero;
        DriverType driverType = adapter is null ? DriverType.Hardware : DriverType.Unknown;

        int hr = D3D11CreateDevice(
            adapterPtr,
            driverType,
            IntPtr.Zero,
            (uint)flags,
            IntPtr.Zero,
            0,
            D3D11_SDK_VERSION,
            out IntPtr devicePtr,
            out _,
            out IntPtr contextPtr);

        if (hr < 0)
            throw new Win32Exception(hr, $"D3D11CreateDevice failed (HRESULT 0x{hr:X8})");

        return (new ID3D11Device(devicePtr), new ID3D11DeviceContext(contextPtr));
    }
}
