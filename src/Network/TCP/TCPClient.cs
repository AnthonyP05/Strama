namespace Strama.Network;

using System.Net.Sockets;
using System.Text.Json;
using System.Text;
using Strama.HS;

class TCPClient
{
    public UdpHandshakeInfo Data = new UdpHandshakeInfo();

    static void Main()
    {
        // Initiate Connection Server

        // Define handshake that exchanges UDP endpoints and capabilities
        var tc = new TCPClient();

        using var client = new TcpClient($"{tc.Data.TcpIP}", tc.Data.TcpPort);
        var stream = client.GetStream();

        Console.WriteLine("Connected to server via TCP");

        bool psuedo_connect_button = true;
        
        while (true)
        {

            // Send connect question
            if (psuedo_connect_button)
            {
                var message = "Can i connect via UDP mayhaps?";
                var buffer = Encoding.UTF8.GetBytes(message);
                stream.Write(buffer, 0, buffer.Length);

                var buf = new byte[4096];
                var len = stream.Read(buf, 0, buf.Length);
                var hs = JsonSerializer.Deserialize<HandshakeResponse>(buf.AsSpan(0, len))!;

                if (hs.Accepted)
                {
                    var udpConn = UDPReceiver.StartListener(hs.Config!);
                    udpConn.Wait(); // Waits till this connection closes

                    // Close UDP!
                    var bye = Encoding.UTF8.GetBytes("disconnect");
                    stream.Write(bye, 0, bye.Length);
                }

                break;
            }
        }
    }
}