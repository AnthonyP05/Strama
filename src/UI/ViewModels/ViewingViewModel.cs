using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Strama.UI.Services;

namespace Strama.UI.ViewModels;

public sealed partial class ViewingViewModel : ViewModelBase, IDisposable
{
    private readonly ConnectionManager _conn;

    public FrameRenderer  Renderer { get; }
    public StreamHandle   Stream   { get; }

    [ObservableProperty] private string codecLabel;
    [ObservableProperty] private string sizeLabel;

    public ViewingViewModel(ConnectionManager conn, StreamHandle stream)
    {
        _conn      = conn;
        Stream     = stream;
        Renderer   = new FrameRenderer();
        codecLabel = stream.Config.Encoder;
        sizeLabel  = $"{stream.Config.OutputWidth}×{stream.Config.OutputHeight} @ {stream.Config.Framerate} fps";

        Renderer.Start(stream.Frames);
    }

    [RelayCommand] private void Disconnect() => _conn.Disconnect();

    public void Dispose() => Renderer.Dispose();
}
