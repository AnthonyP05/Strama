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


                // If yes go ahead and connect, if not don't connect
                var responseBuffer = new byte[1024];
                var bytesRead = stream.Read(responseBuffer, 0, responseBuffer.Length);
                var response = Encoding.UTF8.GetString(responseBuffer, 0, bytesRead);

                // Server said i can connect, yippie!
                if (response == "Yeah, go ahead")
                {
                    var cfgBuf = new byte[4096];
                    var cfgLen = stream.Read(cfgBuf, 0, cfgBuf.Length);
                    var config = JsonSerializer.Deserialize<UdpHandshakeInfo>(cfgBuf.AsSpan(0, cfgLen))!;
                    UDPReceiver.StartListener(config);

                }
            }
        }
    }
}