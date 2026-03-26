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

    public static void Start(UdpHandshakeInfo data)
    {
        using (var client = new UdpClient())
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

                IPEndPoint endPoint = new IPEndPoint(IPAddress.Parse(data.UdpIP), data.UdpPort);

                screen.WaitForExit();
                Console.WriteLine("FFmpeg process exited.");
                // Stopwatch for forcing framerate
                // var stopwatch = new Stopwatch();
                // int targetFrameTimeMs = 1000 / data.Framerate;

                /*
                var y = int.Parse(data.y_Resolution);
                var x = int.Parse(data.x_Resolution);
                // Create a bitmap the size of the screen
                var bitMap = new Bitmap(y, x, PixelFormat.Format32bppArgb);
                var g = Graphics.FromImage(bitMap);

                // Using BitBlt for now, want to switch to Window Capture later...
                while (true)
                {

                    // Copy screen to bitmap (BitBlt)
                    // This is raw BGRA bytes and is stdin pipe
                    g.CopyFromScreen(0,0,0,0, new Size(y, x));

                    // Lock bitmap and get raw bytes
                    var lockedBm = bitMap.LockBits(
                        new Rectangle(0, 0, y, x),
                        ImageLockMode.ReadOnly,
                        PixelFormat.Format32bppArgb
                    );

                    // Gets full byte count for each frame
                    int byteCount = y * x * 4; 
                    var bytes = new byte[byteCount];
                    Marshal.Copy(lockedBm.Scan0, bytes, 0, byteCount);
                    bitMap.UnlockBits(lockedBm);

                    // Write raw frame bytes to Ffmpeg
                    // FFmpeg reads and encodes as H.265
                    // Console.WriteLine("Writing Encoded Frame");
                    //stream.WriteAsync(bytes, 0, bytes.Length);
                    stream.Write(bytes, 0, bytes.Length);

                    // True real-time 60 fps
                    // Thread.Sleep(1000 / data.Framerate);
                    /*
                    stopwatch.Stop();
                    int sleepTime = targetFrameTimeMs - (int)stopwatch.ElapsedMilliseconds;
                    if (sleepTime > 0)
                    {
                        Thread.Sleep(sleepTime);
                    }
                    
                }
                */
            }
            catch (Exception e)
            {
                Console.WriteLine("Send exception: " + e.Message);
            }
        }
        
    }
}