using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Strama.UI.Services;

namespace Strama.UI.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly ClientSettings _settings;
    private readonly Action _onSave;
    private readonly Action _onClose;

    public ObservableCollection<int>            FpsOptions      { get; } = new() { 15, 30, 60, 120 };
    public ObservableCollection<ResolutionOpt> ResolutionOptions { get; } = new()
    {
        new(1280, 720,  "1280 × 720"),
        new(1600, 900,  "1600 × 900"),
        new(1920, 1080, "1920 × 1080"),
        new(0,    0,    "Native (no resize)"),
    };
    public ObservableCollection<string> EncoderOptions { get; } = new()
    {
        "auto", "h264_amf", "h264_nvenc", "h264_qsv", "libx264",
    };

    [ObservableProperty] private int    bitrateMbps;
    [ObservableProperty] private int    selectedFps;
    [ObservableProperty] private ResolutionOpt? selectedResolution;
    [ObservableProperty] private string selectedEncoder;
    [ObservableProperty] private int    tcpPort;

    public bool IsResolutionEnabled =>
        SelectedEncoder is "auto" or "libx264";

    public string ResolutionLockedHint =>
        IsResolutionEnabled
            ? ""
            : "GPU encoders stream at native capture resolution. Switch to libx264 to scale.";

    public SettingsViewModel(ClientSettings settings, Action onSave, Action onClose)
    {
        _settings = settings;
        _onSave   = onSave;
        _onClose  = onClose;

        bitrateMbps        = settings.BitrateMbps;
        selectedFps        = settings.Framerate;
        selectedEncoder    = settings.Encoder;
        tcpPort            = settings.TcpPort;
        selectedResolution = ResolutionOptions.FirstOrDefault(r =>
                                r.Width == settings.OutputWidth && r.Height == settings.OutputHeight)
                             ?? ResolutionOptions[0];
    }

    partial void OnSelectedEncoderChanged(string value)
    {
        OnPropertyChanged(nameof(IsResolutionEnabled));
        OnPropertyChanged(nameof(ResolutionLockedHint));
    }

    [RelayCommand]
    private void Save()
    {
        // Mutate the existing settings object in place so any captured reference
        // (e.g. the ConnectionManager's host-template provider) sees the update
        // without needing to be rewired.
        _settings.BitrateMbps = BitrateMbps;
        _settings.Framerate   = SelectedFps;
        _settings.Encoder     = SelectedEncoder;
        _settings.TcpPort     = TcpPort;
        if (SelectedResolution is { Width: > 0, Height: > 0 } r)
        {
            _settings.OutputWidth  = r.Width;
            _settings.OutputHeight = r.Height;
        }
        else
        {
            // Native — the GPU path ignores OutputWidth/Height anyway; the CPU
            // path falls back to capture dimensions when these are zero.
            _settings.OutputWidth  = 0;
            _settings.OutputHeight = 0;
        }

        _onSave();
        _onClose();
    }

    [RelayCommand] private void Cancel() => _onClose();
}

public sealed record ResolutionOpt(int Width, int Height, string Label);
