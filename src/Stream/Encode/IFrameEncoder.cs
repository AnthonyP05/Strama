namespace Strama.Encode;

/// <summary>
/// Encodes raw BGRA frames and transmits them over the network.
/// Mirrors IFrameDecoder on the client side — same Run(CancellationToken) contract.
/// </summary>
public interface IFrameEncoder : IDisposable
{
    /// <summary>
    /// Captures, encodes, and streams frames until <paramref name="ct"/> is cancelled.
    /// Blocks the calling thread — run via Task.Run(() => encoder.Run(ct)).
    /// </summary>
    void Run(CancellationToken ct = default);
}
