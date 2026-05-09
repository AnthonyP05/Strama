using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Strama.UI.Services;

namespace Strama.UI.ViewModels;

public sealed partial class ViewingViewModel : ViewModelBase, IDisposable
{
    private readonly ConnectionManager _conn;
    private readonly Func<long>        _getTotalBytes;
    private readonly CancellationTokenSource _statsCts = new();

    public FrameRenderer  Renderer { get; }
    public StreamHandle   Stream   { get; }

    [ObservableProperty] private string codecLabel;
    [ObservableProperty] private string sizeLabel;
    [ObservableProperty] private string liveBitrateLabel = "—";
    [ObservableProperty] private string avgBitrateLabel  = "—";

    public ViewingViewModel(ConnectionManager conn, StreamHandle stream)
    {
        _conn          = conn;
        Stream         = stream;
        _getTotalBytes = stream.GetTotalNetworkBytes;
        Renderer       = new FrameRenderer();
        codecLabel     = stream.Config.Encoder;
        sizeLabel      = $"{stream.Config.OutputWidth}×{stream.Config.OutputHeight} @ {stream.Config.Framerate} fps";

        Renderer.Start(stream.Frames);
        _ = Task.Run(() => TrackBitrateAsync(_statsCts.Token));
    }

    [RelayCommand] private void Disconnect() => _conn.Disconnect();

    public void Dispose()
    {
        _statsCts.Cancel();
        Renderer.Dispose();
    }

    private async Task TrackBitrateAsync(CancellationToken ct)
    {
        const int WindowSec = 30;

        // Rolling window: each entry is (cumulativeBytes, tickMs) at the sample time.
        var window = new Queue<(long bytes, long tickMs)>();
        long lastBytes  = 0;
        long lastTickMs = Environment.TickCount64;

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(ct))
            {
                long nowMs   = Environment.TickCount64;
                long current = _getTotalBytes();
                long delta   = current - lastBytes;
                long elapsed = nowMs - lastTickMs;
                lastBytes  = current;
                lastTickMs = nowMs;

                window.Enqueue((current, nowMs));
                while (window.Count > 0 && nowMs - window.Peek().tickMs > WindowSec * 1000L)
                    window.Dequeue();

                int liveKbps = elapsed > 0 ? (int)(delta * 8_000L / elapsed / 1000) : 0;

                int avgKbps = 0;
                if (window.Count >= 2)
                {
                    var oldest   = window.Peek();
                    long winMs   = nowMs - oldest.tickMs;
                    if (winMs > 0)
                        avgKbps = (int)((current - oldest.bytes) * 8_000L / winMs / 1000);
                }

                LiveBitrateLabel = FormatKbps(liveKbps);
                AvgBitrateLabel  = FormatKbps(avgKbps);
            }
        }
        catch (OperationCanceledException) { }
    }

    private static string FormatKbps(int kbps)
        => kbps >= 1000 ? $"{kbps / 1000.0:F1} Mbps" : $"{kbps} kbps";
}
