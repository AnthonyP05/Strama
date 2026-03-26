using Strama.Network;
using System;
using System.Diagnostics;
using System.IO; // Added for File operations
using System.Net;
using System.Net.Sockets;
using System.Text;
using Strama.HS;
using System.Threading.Tasks;

public class UDPReceiver
{
    public static void StartListener(UdpHandshakeInfo data)
    {
        Console.WriteLine($"Starting FFplay to listen for UDP stream on port {data.UdpPort}");   
        
        Task.Run(() =>
        {
            try
            {
                // 1. Generate the SDP file contents using the provided IP and Port
                string sdpContent = $@"v=0
o=- 0 0 IN IP4 {data.UdpIP}
s=No Name
c=IN IP4 {data.UdpIP}
t=0 0
a=tool:libavformat
m=video {data.UdpPort} RTP/AVP 96
a=rtpmap:96 H265/90000
a=fmtp:96 packetization-mode=1";

                // 2. Save it to a temporary file
                string sdpFilePath = Path.Combine(Path.GetTempPath(), "stream.sdp");
                File.WriteAllText(sdpFilePath, sdpContent);
                Console.WriteLine($"Generated SDP file at: {sdpFilePath}");

                var screen = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "ffplay",
                        Arguments = $"-protocol_whitelist file,rtp,udp " +
                                    $"-probesize 32 -analyzeduration 0 " +
                                    $"-fflags nobuffer -flags low_delay " +
                                    $"-i \"{sdpFilePath}\"",
                        UseShellExecute = false,
                        // CreateNoWindow = false, 
                    }
                };

                Console.WriteLine("Starting FFplay to receive stream.");
                screen.Start();

                screen.WaitForExit();
                Console.WriteLine("FFplay process exited.");
                
                // Optional: Clean up the file after ffplay exits
                if (File.Exists(sdpFilePath))
                {
                    File.Delete(sdpFilePath);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("General exception: " + e.Message);
            }
        });
    }
}