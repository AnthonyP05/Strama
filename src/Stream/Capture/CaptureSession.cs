using System.Threading.Channels;
using Strama.Records;

namespace Strama.Capture;

/// <summary>
/// Runs a capture loop on a dedicated thread and exposes captured frames
/// as a bounded channel. If the consumer (encoder) falls behind, the oldest
/// queued frame is dropped so latency stays low.
/// </summary>
public sealed class CaptureSession : IDisposable
{
    private readonly IScreenCapturer _capturer;
    private readonly Channel<FrameData> _channel;

    /// <summary>
    /// The read end of the frame channel. Pass this to the encoder.
    /// </summary>
    public ChannelReader<FrameData> Frames => _channel.Reader;

    /// <param name="capturer">Platform capturer obtained from <see cref="ScreenCapturerFactory"/>.</param>
    /// <param name="capacity">Max frames queued before old ones are dropped. 2 is usually enough.</param>
    public CaptureSession(IScreenCapturer capturer, int capacity = 2)
    {
        _capturer = capturer;
        _channel = Channel.CreateBounded<FrameData>(
            new BoundedChannelOptions(capacity)
            {
                // When the channel is full, evict the oldest frame and write the new one.
                // This keeps the encoder working on the most recent frame, not a stale one.
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
            },
            // Dropped frames must be disposed or their pooled/native buffers leak.
            // itemDropped is a CreateBounded parameter, not a BoundedChannelOptions property.
            itemDropped: static frame => frame.Dispose());
    }

    /// <summary>
    /// Captures frames in a tight loop until <paramref name="ct"/> is cancelled.
    /// This method blocks — call it via <c>Task.Run(() => session.Run(ct))</c>.
    ///
    /// If the desktop session changes (lock screen, UAC, resolution change), DXGI
    /// throws AccessLost. Dispose this session and create a new one to recover.
    /// </summary>
    public void Run(CancellationToken ct = default)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Returns null on timeout (no screen change) — just loop again.
                var frame = _capturer.CaptureFrame();
                if (frame is not null)
                    _channel.Writer.TryWrite(frame.Value);
            }
        }
        finally
        {
            // Signal the reader that no more frames are coming.
            _channel.Writer.TryComplete();
        }
    }

    public void Dispose() => _capturer.Dispose();
}
