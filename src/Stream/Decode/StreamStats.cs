namespace Strama.Decode;

/// <summary>
/// Thread-safe running counter of compressed bytes actually received off the
/// wire by the decoder. The decoder adds each video packet's size as it arrives;
/// the viewer UI samples <see cref="TotalBytes"/> on a timer to derive a live
/// bitrate. Measuring here (not on the encoder) means the readout reflects what
/// genuinely crossed the network, including any loss (#16).
/// </summary>
public sealed class StreamStats
{
    private long _totalBytes;

    /// <summary>Total compressed video bytes received since the stream opened.</summary>
    public long TotalBytes => Interlocked.Read(ref _totalBytes);

    public void AddBytes(int count) => Interlocked.Add(ref _totalBytes, count);
}
