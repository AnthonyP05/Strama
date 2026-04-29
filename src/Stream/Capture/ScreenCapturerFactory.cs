using System.Runtime.InteropServices;
using Strama.Capture.Windows;

namespace Strama.Capture;

public static class ScreenCapturerFactory
{
    /// <param name="monitorIndex">Zero-based monitor index. 0 = primary display.</param>
    public static IScreenCapturer Create(int monitorIndex = 0)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new DxgiScreenCapturer(monitorIndex);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            throw new NotSupportedException("Linux capture (X11/Wayland) is not yet implemented.");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            throw new NotSupportedException("macOS capture (CoreGraphics) is not yet implemented.");

        throw new PlatformNotSupportedException($"Unsupported platform: {RuntimeInformation.OSDescription}");
    }
}
