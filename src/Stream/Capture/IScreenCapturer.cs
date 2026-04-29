using Strama.Records;

namespace Strama.Capture;

/// <summary>
/// Captures raw BGRA frames from the local display.
/// Implementations are platform-specific; use <see cref="ScreenCapturerFactory"/> to get one.
/// </summary>
public interface IScreenCapturer : IDisposable
{
    /// <summary>
    /// Returns the next changed frame, or null if no new frame appeared within
    /// <paramref name="timeoutMs"/>. Throws if the session is lost (lock screen, UAC, etc.)
    /// — the caller should dispose and recreate the capturer.
    /// </summary>
    FrameData? CaptureFrame(int timeoutMs = 100);
}
