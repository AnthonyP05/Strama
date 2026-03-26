namespace Strama.Network;

using System.Diagnostics;
using Strama.Records;

public class UDPSender
{

    public static void Start(HandshakeConfig data, CancellationToken ct)
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

            ct.Register(() =>
            {
                try
                {
                    if (!screen.HasExited)
                    {
                        // Kill entire process tree, not just the parent
                        var killTree = new Process
                        {
                            StartInfo = new ProcessStartInfo
                            {
                                FileName = "taskkill",
                                Arguments = $"/PID {screen.Id} /T /F",
                                UseShellExecute = false,
                                CreateNoWindow = true
                            }
                        };
                        killTree.Start();
                        killTree.WaitForExit();
                    }
                }
                catch { }
            });

            screen.Start();
            screen.WaitForExit();

        }
        catch (Exception e)
        {
            Console.WriteLine("Send exception: " + e.Message);
        }
    
        
    }
}