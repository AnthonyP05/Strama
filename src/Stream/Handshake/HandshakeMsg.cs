namespace Strama.HS;

public record UdpHandshakeInfo
{
    public int UdpPort { get; set; } = 8889;

    public string UdpIP { get; set; } = "127.0.0.1";

    public int TcpPort { get; set; } = 8888;

    public string TcpIP { get; set; } = "127.0.0.1";

    public string CaptureWidth  { get; set; } = "2560";

    public string CaptureHeight { get; set; } = "1440";

    public string OutputWidth   { get; set; } = "1280";

    public string OutputHeight  { get; set; } = "720";
    
    public string EncodingType { get; set; } = "H.265";

    public int Framerate { get; set; } = 60;

}

public record HandshakeResponse
{
    public bool Accepted { get; set; }
    public UdpHandshakeInfo? Config { get; set; }
}