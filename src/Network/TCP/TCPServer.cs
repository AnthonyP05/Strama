namespace Strama.Network;

using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Strama.HS;

class TcpServer
{

    public UdpHandshakeInfo Data = new UdpHandshakeInfo();

    static void Main()
    {
        // Handle Incoming Connections from Client
        // This will be the person getting connected TO

        var ts = new TcpServer();

        var listener = new TcpListener(ts.Data.TcpIP, ts.Data.TcpPort);

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

            var stream = client.GetStream(); // Gets the established connection stream



            // ONLY: Setup Record and information IF connect button has been clicked/received.

            var buffer = new byte[1024];
            var bytesRead = stream.Read(buffer, 0, buffer.Length);
            var msgFromClient = Encoding.UTF8.GetString(buffer, 0, bytesRead);

            // If connect request comes...
            if (msgFromClient == "Can i connect via UDP mayhaps?")
            {
                // Allow connection (Yes button clicked)
                var message = "Yeah, go ahead";
                var connBuffer = Encoding.UTF8.GetBytes(message);
                stream.Write(connBuffer, 0, connBuffer.Length);

                // Send Config over stream
                byte[] configBytes = JsonSerializer.SerializeToUtf8Bytes(ts.Data);
                stream.Write(configBytes, 0, configBytes.Length);

                // BROADCAST BULLSHIT RAHHHHHHHH

                var cts = new CancellationTokenSource();
                UDPSender.Start(ts.Data, cts.Token);

                // Setting UDP Information for Client
                var record = new HS.UdpHandshakeInfo();


                var psuedo_disconnect_button = false;

                /*
                // While the disconnect button hasn't been clicked
                while(!psuedo_disconnect_button)
                {   
                    Console.WriteLine("Attempting to start UDP Server...");
                    // Start UDP connection
                    UDPSender.Start(ts.Data);
                    
                

                }
                */




                // Don't allow connection (No button clicked)
                /*
                var message = "No, absolutely not";
                var connBuffer = Encoding.UTF8.GetBytes(message);
                stream.Write(connBuffer, 0, connBuffer.Length);



                */
            }

            


            



            

            // Send UDP information 
            // byte[] recordToBytes = JsonSerializer.SerializeToUtf8Bytes(record);
            //stream.Write(recordToBytes, 0, recordToBytes.Length);


            // Insert setting up the quality, type, etc.


            // THEN initiate stream


            /* 

            Need something like this for UDP, where its constantly
            Sending Video bytes

            var buffer = new byte[1024];
            int bytesRead;

            // Read and print data
            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) != 0)
            {
                // Gets message from Client
                var message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                Console.WriteLine($"Received: {message}");

                // Writes message back to client. 
                stream.Write(buffer, 0, bytesRead);
                Console.WriteLine("Message echoed back.");
            }
            */

            client.Close();
            Console.WriteLine("Client disconnected.");
             
        }

    }
}