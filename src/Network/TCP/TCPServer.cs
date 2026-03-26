namespace Strama.Network;

using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Strama.HS;

class TcpServer
{

    public UdpHandshakeInfo Data = new UdpHandshakeInfo();

    static void Main()
    {
        // Handle Incoming Connections from Client
        // This will be the person getting connected TO

        var ts = new TcpServer();

        var listener = new TcpListener(IPAddress.Parse(ts.Data.TcpIP), ts.Data.TcpPort);

        // Future update: Only start listening IF a user is trying to connect... 
        // (idk if thats possible because how will it know...?)
        // So maybe... if a user tries to connect via a code (or something idk), 
        // then start udp connection when "connect" is clicked
        listener.Start();

        Console.WriteLine($"Starting TCP server on port {ts.Data.TcpPort}");


        while (true)
        {
            Console.WriteLine("Waiting on a TCP connection...");
            
            var client = listener.AcceptTcpClient(); // Accept incoming connections

            Console.WriteLine("TCP Client connected!");

            client.ReceiveTimeout = 5000; // 5 second timeout to remove hanging connections

            var stream = client.GetStream(); // Gets the established connection stream

            // ONLY: Setup Record and information IF connect button has been clicked/received.

            var buffer = new byte[1024];
            var bytesRead = stream.Read(buffer, 0, buffer.Length);
            var msgFromClient = Encoding.UTF8.GetString(buffer, 0, bytesRead);

            // If connect request comes...
            if (msgFromClient == "Can i connect via UDP mayhaps?")
            {

                // Allow connection (Yes button clicked)
                var response = new HandshakeResponse
                    {
                        Accepted = true,
                        Config   = ts.Data
                    };
                    byte[] responseBytes = JsonSerializer.SerializeToUtf8Bytes(response);
                    stream.Write(responseBytes, 0, responseBytes.Length);

                // BROADCAST BULLSHIT RAHHHHHHHH

                var cts = new CancellationTokenSource();
                var senderTask = Task.Run(() => UDPSender.Start(ts.Data, cts.Token));

                try
                {
                    var disconnectBuffer = new byte[64];
                    var disconnectLength = stream.Read(disconnectBuffer, 0, disconnectBuffer.Length); // Waits for disconnect call
                    var msg = Encoding.UTF8.GetString(disconnectBuffer, 0, disconnectLength);
                    Console.WriteLine($"Client said: {msg}");
                }
                catch (IOException)
                {
                    Console.WriteLine("Client dropped connection.");
                }
                finally
                {
                    Console.WriteLine("Cancelling FFmpeg...");
                    cts.Cancel();
                    senderTask.Wait();
                }

            }

            client.Close();
            Console.WriteLine("Client disconnected.");
             
        }

    }
}