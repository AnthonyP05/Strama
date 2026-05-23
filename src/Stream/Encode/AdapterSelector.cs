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
    /// Returns true if the named encoder has a chance of running on this system:
    /// software encoders always return true, hardware encoders require a matching
    /// adapter to be present.
    /// </summary>
    public static bool IsHardwareAvailable(string encoderName)
    {
        var vendor = VendorForEncoder(encoderName);
        if (vendor is null) return true;
        return HasAdapter(vendor.Value);
    }
}
