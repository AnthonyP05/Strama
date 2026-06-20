using Strama.Records;

namespace Strama.UI.Services;

/// <summary>
/// Strongly-typed mirror of the parts of <see cref="HandshakeConfig"/> that the
/// user adjusts in the GUI. Persisted to JSON via <see cref="SettingsStore"/>.
/// Defaults are intentionally non-overkill: 30 fps / 5 Mbps / 720p / encoder=auto.
/// </summary>
public sealed class ClientSettings
{
    public int    TcpPort       { get; set; } = 8888;
    public int    UdpPort       { get; set; } = 8889;
    public int    BitrateMbps   { get; set; } = 5;
    public int    Framerate     { get; set; } = 30;
    public int    OutputWidth   { get; set; } = 1280;
    public int    OutputHeight  { get; set; } = 720;
    public string Encoder       { get; set; } = "auto";
    public string LastConnect   { get; set; } = "";
    public List<RecentSession> RecentSessions { get; set; } = [];

    /// <summary>
    /// Builds a HandshakeConfig from these settings. This is what the host uses
    /// as its encoder template when accepting an incoming session.
    /// </summary>
    public HandshakeConfig ToHandshakeConfig() => new()
    {
        TcpPort       = TcpPort,
        UdpPort       = UdpPort,
        Framerate     = Framerate,
        Bitrate       = $"{BitrateMbps}M",
        OutputWidth   = OutputWidth.ToString(),
        OutputHeight  = OutputHeight.ToString(),
        CaptureWidth  = "2560",   // unused by GPU path; CPU path picks up output dims
        CaptureHeight = "1440",
        Encoder       = Encoder,
        TcpIP         = "127.0.0.1",
        UdpIP         = "127.0.0.1",
    };

    public ClientSettings Clone() => new()
    {
        TcpPort      = TcpPort,
        UdpPort      = UdpPort,
        BitrateMbps  = BitrateMbps,
        Framerate    = Framerate,
        OutputWidth  = OutputWidth,
        OutputHeight = OutputHeight,
        Encoder      = Encoder,
        LastConnect  = LastConnect,
        RecentSessions = RecentSessions
            .Select(s => new RecentSession
            {
                Address          = s.Address,
                DisplayName      = s.DisplayName,
                LastConnectedUtc = s.LastConnectedUtc,
                IsFavorite       = s.IsFavorite,
            })
            .ToList(),
    };
}

public sealed class RecentSession
{
    public string Address { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public DateTime LastConnectedUtc { get; set; } = DateTime.UtcNow;
    public bool IsFavorite { get; set; }
}
