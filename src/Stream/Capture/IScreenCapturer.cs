using Strama.Records;

namespace Strama.Capture;

/// Captures raw BGRA frames from the local display.
/// Implementations are platform-specific; use <see cref="ScreenCapturerFactory"/> to get one.

public interface IScreenCapturer : IDisposable
{
    FrameData? CaptureFrame(int timeoutMs = 100);
}
