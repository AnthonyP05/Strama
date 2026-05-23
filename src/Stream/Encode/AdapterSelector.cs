using System.Runtime.InteropServices;
using Vortice.DXGI;

namespace Strama.Encode;

/// <summary>
/// Maps FFmpeg hardware-encoder names to DXGI vendor IDs and pre-flights
/// whether a matching adapter exists on this system.
///
/// Why this exists: the gyan.dev FFmpeg builds compile in h264_amf, h264_nvenc,
/// and h264_qsv regardless of host hardware, so <c>avcodec_find_encoder_by_name</c>
/// returning non-null says nothing about whether the encoder can actually open.
/// Without this pre-flight, "auto" picks h264_amf on every Windows machine —
/// including NVIDIA-only and Intel-only systems — and then crashes at
/// <c>avcodec_open2</c> inside the AMD driver.
/// </summary>
public static class AdapterSelector
{
    public const uint VendorAmd    = 0x1002;
    public const uint VendorNvidia = 0x10DE;
    public const uint VendorIntel  = 0x8086;

    /// <summary>
    /// Returns the GPU vendor associated with an FFmpeg encoder name, or null
    /// for software encoders.
    /// </summary>
    public static uint? VendorForEncoder(string encoderName) => encoderName switch
    {
        "h264_amf"   => VendorAmd,
        "h264_nvenc" => VendorNvidia,
        "h264_qsv"   => VendorIntel,
        _            => null,
    };

    /// <summary>
    /// Returns true if a DXGI adapter from the given vendor exists on this
    /// system. Always false on non-Windows platforms (DXGI is Windows-only).
    /// </summary>
    public static bool HasAdapter(uint vendorId)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint i = 0; ; i++)
        {
            var hr = factory.EnumAdapters1(i, out var adapter);
            if (hr.Failure) break;
            using (adapter)
            {
                if (adapter.Description1.VendorId == vendorId)
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Returns true if a DXGI adapter from the given vendor exists AND owns at
    /// least one display output. On hybrid laptops, the discrete GPU often has
    /// no outputs (the iGPU drives the display), so its encoder cannot drive
    /// DuplicateOutput off the same device. Cross-adapter capture is a future
    /// step — until then, "available" means "same-adapter capture works".
    /// </summary>
    public static bool HasAdapterWithOutput(uint vendorId)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint i = 0; ; i++)
        {
            var hr = factory.EnumAdapters1(i, out var adapter);
            if (hr.Failure) break;
            using (adapter)
            {
                if (adapter.Description1.VendorId != vendorId) continue;
                if (adapter.EnumOutputs(0, out var output).Success)
                {
                    output.Dispose();
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Returns true if the named encoder has a chance of running on this system.
    /// Software encoders always return true; hardware encoders require a matching
    /// adapter to exist (regardless of whether it owns a display — the
    /// cross-adapter path bridges the gap).
    /// </summary>
    public static bool IsHardwareAvailable(string encoderName)
    {
        var vendor = VendorForEncoder(encoderName);
        if (vendor is null) return true;
        return HasAdapter(vendor.Value);
    }

    /// <summary>
    /// Finds the first DXGI adapter matching the given vendor. Caller owns the
    /// returned reference and must dispose it. Returns null if no match.
    /// </summary>
    public static IDXGIAdapter1? FindAdapterByVendor(uint vendorId)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return null;

        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint i = 0; ; i++)
        {
            var hr = factory.EnumAdapters1(i, out var adapter);
            if (hr.Failure) break;
            if (adapter.Description1.VendorId == vendorId)
                return adapter;
            adapter.Dispose();
        }
        return null;
    }

    /// <summary>
    /// Finds the adapter that owns the primary display output (output 0). This
    /// is the adapter that must drive DuplicateOutput for screen capture.
    /// On hybrid laptops, this is the integrated GPU, not the discrete one.
    /// </summary>
    public static IDXGIAdapter1? GetAdapterOwningPrimaryOutput()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return null;

        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint i = 0; ; i++)
        {
            var hr = factory.EnumAdapters1(i, out var adapter);
            if (hr.Failure) break;
            if (adapter.EnumOutputs(0, out var output).Success)
            {
                output.Dispose();
                return adapter;
            }
            adapter.Dispose();
        }
        return null;
    }

    /// <summary>
    /// True iff two adapter handles refer to the same physical GPU. Compared by
    /// LUID, which is stable across factory instances.
    /// </summary>
    public static bool SameAdapter(IDXGIAdapter1 a, IDXGIAdapter1 b)
    {
        var aLuid = a.Description1.Luid;
        var bLuid = b.Description1.Luid;
        return aLuid.LowPart == bLuid.LowPart && aLuid.HighPart == bLuid.HighPart;
    }
}
