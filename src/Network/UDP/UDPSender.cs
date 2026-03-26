using Strama.Network;

using System.Net;
using System.Diagnostics;
using System.Net.Sockets;
using System.Drawing;
using Strama.HS;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;


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
                                $"-video_size 2560x1440 " +
                                $"-i desktop " + // Capture the entire desktop
                                $"-vf scale={data.y_Resolution}:{data.x_Resolution} " + // Scales the full captured screen down
                                $"-c:v libx265 " + 
                                $"-b:v 10M " +
                                $"-bufsize 20M " +
                                $"-tune zerolatency " + 
                                //$"-crf 18 " + // Quality setting, lower is better quality (and higher bitrate)
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