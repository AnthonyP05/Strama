using System.Threading.Channels;
using Strama.Records;

namespace Strama.Decode;

/// <summary>
/// Decodes a compressed video stream into raw BGRA frames.
/// Mirrors IScreenCapturer on the server side — same ChannelReader pattern,
/// same Run(CancellationToken) threading contract.
/// </summary>
public interface IFrameDecoder : IDisposable
{
    /// <summary>
    /// The read end of the decoded-frame channel. Pass this to the display layer.
    /// </summary>
    ChannelReader<FrameData> Frames { get; }

    /// <summary>
    /// Opens the stream and decodes frames until <paramref name="ct"/> is cancelled.
    /// Blocks the calling thread — run via Task.Run(() => decoder.Run(ct)).
    /// </summary>
    void Run(CancellationToken ct = default);
}
