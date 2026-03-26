using System.Net;

namespace Strama.HS;

public record UdpHandshakeInfo
{

    public int UdpPort { get; set; } = 8889;

    public string UdpIP { get; set; } = "127.0.0.1";

    public int TcpPort { get; set; } = 8888;

    public IPAddress TcpIP { get; set; } = IPAddress.Parse("127.0.0.1");//IPAddress.Any;

    public string y_Resolution { get; set; } = "720"; 

    public string x_Resolution { get; set; } = "480";

    public string EncodingType { get; set; } = "H.265";

    public int Framerate { get; set; } = 60;

}