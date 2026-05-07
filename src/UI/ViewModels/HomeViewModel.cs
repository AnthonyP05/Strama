using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Strama.UI.Services;

namespace Strama.UI.ViewModels;

public sealed partial class HomeViewModel : ViewModelBase
{
    private readonly ConnectionManager _conn;
    private readonly Func<ClientSettings> _settingsProvider;
    private readonly Action _openSettings;

    [ObservableProperty] private string  localCode      = "—";
    [ObservableProperty] private string  connectInput   = "";
    [ObservableProperty] private string  statusText     = "Idle";
    [ObservableProperty] private string? errorBanner;

    public HomeViewModel(
        ConnectionManager conn,
        Func<ClientSettings> settingsProvider,
        Action openSettings)
    {
        _conn             = conn;
        _settingsProvider = settingsProvider;
        _openSettings     = openSettings;

        LocalCode    = conn.LocalCode;
        StatusText   = conn.LocalEndPoint is null
            ? "Idle"
            : $"Listening on {conn.LocalEndPoint.Port}";
        ConnectInput = settingsProvider().LastConnect;
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        ErrorBanner = null;
        var template = _settingsProvider().ToHandshakeConfig();
        await _conn.RequestConnectionAsync(ConnectInput, template);
    }

    [RelayCommand]
    private void OpenSettings() => _openSettings();

    [RelayCommand]
    private async Task CopyCodeAsync()
    {
        var clipboard = Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow?.Clipboard
                : null;
        if (clipboard is null) return;
        await clipboard.SetTextAsync(LocalCode);
        StatusText = "Copied!";
    }

    private bool CanConnect() => !string.IsNullOrWhiteSpace(ConnectInput);

    partial void OnConnectInputChanged(string value) => ConnectCommand.NotifyCanExecuteChanged();
}
