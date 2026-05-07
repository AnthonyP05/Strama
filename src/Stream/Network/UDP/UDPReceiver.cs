using System.Threading.Channels;
using Strama.Decode;
using Strama.Records;

namespace Strama.Network;

public static class UDPReceiver
{
    public sealed record Session(Task Running, ChannelReader<FrameData> Frames);

    /// <summary>
    /// Writes the SDP file describing the incoming RTP stream and starts the
    /// in-process FFmpeg decoder. Returns a Session whose Frames reader the caller
    /// consumes on its own thread (UI dispatcher in the GUI; print loop in
    /// the headless console). Cancel <paramref name="ct"/> to stop decoding —
    /// the Running task will then complete and Frames will end its enumeration.
    /// </summary>
    public static Session Start(HandshakeConfig data, CancellationToken ct = default)
    {
        string sdpPath = WriteSdp(data);

        // FFmpeg shared libraries (avcodec, avformat, swscale, etc.) must be present.
        // Put them next to the executable, or set ffmpeg.RootPath explicitly before calling.
        var decoder    = new FFmpegFrameDecoder(sdpPath);
        var decodeTask = Task.Run(() => decoder.Run(ct), ct);

        return new Session(decodeTask, decoder.Frames);
    }

    private static string WriteSdp(HandshakeConfig data)
    {
        string content = $"""
            v=0
            o=- 0 0 IN IP4 {data.UdpIP}
            s=No Name
            c=IN IP4 {data.UdpIP}
            t=0 0
            a=tool:libavformat
            m=video {data.UdpPort} RTP/AVP 96
            a=rtpmap:96 H264/90000
            a=fmtp:96 packetization-mode=1
            """;
        string path = Path.Combine(Path.GetTempPath(), "stream.sdp");
        File.WriteAllText(path, content);
        Console.WriteLine($"[Decode] SDP written to {path}");
        return path;
    }
}
