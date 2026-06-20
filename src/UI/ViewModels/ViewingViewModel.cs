using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Strama.UI.Services;

namespace Strama.UI.ViewModels;

public sealed partial class ViewingViewModel : ViewModelBase, IDisposable
{
    // Number of 1-second samples kept for the moving average → 30 s window (#16).
    private const int AvgWindowSeconds = 30;

    private readonly ConnectionManager _conn;
    private readonly DispatcherTimer   _statsTimer;
    private readonly Queue<double>     _kbpsSamples = new(AvgWindowSeconds);
    private long _lastBytes;

    public FrameRenderer  Renderer { get; }
    public StreamHandle   Stream   { get; }

    [ObservableProperty] private string codecLabel;
    [ObservableProperty] private string sizeLabel;
    [ObservableProperty] private string bitrateLabel = "— kbps";

    public ViewingViewModel(ConnectionManager conn, StreamHandle stream)
    {
        _conn      = conn;
        Stream     = stream;
        Renderer   = new FrameRenderer();
        codecLabel = stream.Config.Encoder;
        sizeLabel  = FormatSize(stream.Config);

        Renderer.Start(stream.Frames);

        // Sample the decoder's received-byte counter once a second on the UI thread,
        // deriving an instantaneous kbps and a 30-second moving average.
        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statsTimer.Tick += OnStatsTick;
        _statsTimer.Start();
    }

    private void OnStatsTick(object? sender, EventArgs e)
    {
        long total = Stream.Stats.TotalBytes;
        long delta = total - _lastBytes;
        _lastBytes = total;
        if (delta < 0) delta = 0;

        double kbps = delta * 8.0 / 1000.0;
        _kbpsSamples.Enqueue(kbps);
        while (_kbpsSamples.Count > AvgWindowSeconds) _kbpsSamples.Dequeue();
        double avg = _kbpsSamples.Average();

        BitrateLabel = $"{kbps:F0} kbps · avg {avg:F0}";

        // Once frames are flowing, prefer the actual decoded dimensions. This also
        // fixes the "0×0" shown when the host streams at Native resolution (#14/#16).
        if (Renderer.CurrentWidth > 0 && Renderer.CurrentHeight > 0)
            SizeLabel = $"{Renderer.CurrentWidth}×{Renderer.CurrentHeight} @ {Stream.Config.Framerate} fps";
    }

    private static string FormatSize(Strama.Records.HandshakeConfig cfg)
    {
        bool native = !int.TryParse(cfg.OutputWidth, out int w) || w <= 0
                   || !int.TryParse(cfg.OutputHeight, out int h) || h <= 0;
        return native
            ? $"native @ {cfg.Framerate} fps"
            : $"{cfg.OutputWidth}×{cfg.OutputHeight} @ {cfg.Framerate} fps";
    }

    [RelayCommand] private void Disconnect() => _conn.Disconnect();

    public void Dispose()
    {
        _statsTimer.Stop();
        _statsTimer.Tick -= OnStatsTick;
        Renderer.Dispose();
    }
}
