using System.Diagnostics;
using Strama.Capture;
using Strama.Records;

namespace Strama.Network;

/// <summary>
/// Captures the screen via DxgiScreenCapturer and pipes raw BGRA frames into an
/// ffmpeg subprocess for H.264 encoding and RTP streaming.
/// Replaces UDPSender, which used ffmpeg's built-in gdigrab capturer.
/// </summary>
public static class PipeFrameSender
{
    public static async Task StartAsync(HandshakeConfig data, CancellationToken ct)
    {
        try
        {
            await RunAsync(data, ct);
        }
        catch (OperationCanceledException) { /* clean shutdown when CT fires */ }
    }

    private static async Task RunAsync(HandshakeConfig data, CancellationToken ct)
    {
        using var capturer = ScreenCapturerFactory.Create();
        using var session  = new CaptureSession(capturer);
        var captureTask = Task.Run(() => session.Run(ct), ct);

        // Block until the first frame arrives — we need its actual dimensions
        // to tell ffmpeg how to interpret the raw bytes coming in on stdin.
        var firstFrame = await session.Frames.ReadAsync(ct);
        int w = firstFrame.Width;
        int h = firstFrame.Height;

        Console.WriteLine($"[Capture] First frame: {w}x{h}");

        var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName  = "ffmpeg",
                // rawvideo on stdin replaces -f gdigrab -i desktop
                Arguments = $"-f rawvideo -pix_fmt bgra -s {w}x{h} -r {data.Framerate} -i pipe:0 " +
                            $"-vf scale={data.OutputWidth}:{data.OutputHeight} " +
                            $"-c:v libx264 -b:v 10M -bufsize 20M -tune zerolatency " +
                            $"-g 30 -sc_threshold 0 -preset ultrafast " +
                            $"-f rtp rtp://{data.UdpIP}:{data.UdpPort}",
                UseShellExecute       = false,
                RedirectStandardInput = true,
            }
        };

        ct.Register(() =>
        {
            try
            {
                if (proc.HasExited) return;
                var kill = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName  = "taskkill",
                        Arguments = $"/PID {proc.Id} /T /F",
                        UseShellExecute = false,
                        CreateNoWindow  = true,
                    }
                };
                kill.Start();
                kill.WaitForExit();
            }
            catch { }
        });

        proc.Start();
        var stdin = proc.StandardInput.BaseStream;

        try
        {
            await stdin.WriteAsync(firstFrame.Pixels, ct);

            await foreach (var frame in session.Frames.ReadAllAsync(ct))
                await stdin.WriteAsync(frame.Pixels, ct);
        }
        catch (OperationCanceledException) { /* normal shutdown when CT fires */ }
        finally
        {
            stdin.Close();
            proc.WaitForExit();
        }

        try { await captureTask; }
        catch (OperationCanceledException) { }
    }
}
