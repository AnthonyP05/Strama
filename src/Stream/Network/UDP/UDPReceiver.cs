using System.Diagnostics;
using Strama.Decode;
using Strama.Records;

namespace Strama.Network;

public class UDPReceiver
{
    /// <summary>
    /// Starts decoding the incoming RTP stream using FFmpegFrameDecoder.
    /// Prints decoded fps and resolution to console as a temporary test harness
    /// until the Avalonia GUI is ready.
    /// Returns a Task that completes when the stream ends or is cancelled.
    /// </summary>
    public static Task StartListener(HandshakeConfig data, CancellationToken ct = default)
    {
        return Task.Run(async () =>
        {
            string sdpPath = WriteSdp(data);

            // FFmpeg shared libraries (avcodec, avformat, swscale, etc.) must be present.
            // Put them next to the executable, or set ffmpeg.RootPath explicitly before calling.
            using var decoder = new FFmpegFrameDecoder(sdpPath);
            var decodeTask = Task.Run(() => decoder.Run(ct), ct);

            int frameCount = 0;
            var sw = Stopwatch.StartNew();

            await foreach (var frame in decoder.Frames.ReadAllAsync(ct))
            {
                frameCount++;

                if (sw.ElapsedMilliseconds >= 1000)
                {
                    Console.WriteLine($"[Decode] {frameCount} fps  {frame.Width}x{frame.Height}");
                    frameCount = 0;
                    sw.Restart();
                }

                frame.Dispose();
            }

            await decodeTask;
        }, ct);
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
