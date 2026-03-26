using Strama.Network;

using System.Net;
using System.Diagnostics;
using System.Net.Sockets;
using Strama.HS;

public class UDPSender
{

    public static void Start(UdpHandshakeInfo data, CancellationToken ct)
    {
        try
        {         
            var screen = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = $"-f gdigrab " +
                                $"-framerate {data.Framerate} " +
                                $"-offset_x 0 -offset_y 0 " + // Start at top left of primary screen
                                $"-video_size {data.CaptureWidth}x{data.CaptureHeight} " +
                                $"-i desktop " + // Capture the entire desktop
                                $"-vf scale={data.OutputWidth}:{data.OutputHeight} " +
                                $"-c:v libx264 " + 
                                $"-b:v 10M " +
                                $"-bufsize 20M " +
                                $"-tune zerolatency " + 
                                $"-g 30 " + // Keyframe every 30 frames
                                $"-sc_threshold 0 " + // Turning off scene-cut
                                $"-preset ultrafast " +
                                $"-f rtp rtp://{data.UdpIP}:{data.UdpPort}",
                    
                    UseShellExecute = false,
                    // CreateNoWindow = false // Raw frames written
                }
            };

            // Starting screen capture
            Console.WriteLine("Starting screen capture.");
            screen.Start();

            ct.Register(() => { if (!screen.HasExited) screen.Kill(); });

            screen.WaitForExit();
            Console.WriteLine("FFmpeg process exited.");

        }
        catch (Exception e)
        {
            Console.WriteLine("Send exception: " + e.Message);
        }
    
        
    }
}