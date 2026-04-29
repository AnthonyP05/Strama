namespace Strama.Records;

public record HandshakeConfig
{
    public int UdpPort { get; set; } = 8889;

    public string UdpIP { get; set; } = "127.0.0.1";

    public int TcpPort { get; set; } = 8888;

    public string TcpIP { get; set; } = "127.0.0.1";

    public string CaptureWidth  { get; set; } = "2560";

    public string CaptureHeight { get; set; } = "1440";

    public string OutputWidth   { get; set; } = "1280";

    public string OutputHeight  { get; set; } = "720";
    
    public int Framerate { get; set; } = 150;

    // Target encode bitrate passed to ffmpeg -b:v. Examples: "10M", "5M", "50M"
    public string Bitrate { get; set; } = "10M";

    // Encoder to use. Software: "libx264". AMD GPU: "h264_amf". Nvidia: "h264_nvenc". Intel: "h264_qsv"
    public string Encoder { get; set; } = "libx264";

}

public record HandshakeResponse
{
    public bool Accepted { get; set; }
    public HandshakeConfig? Config { get; set; }
}