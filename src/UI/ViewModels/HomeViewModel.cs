using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Strama.UI.Services;
using System.Collections.ObjectModel;

namespace Strama.UI.ViewModels;

public sealed partial class HomeViewModel : ViewModelBase
{
    private const int MaxRecentSessions = 8;

    private readonly ConnectionManager _conn;
    private readonly Func<ClientSettings> _settingsProvider;
    private readonly Action _openSettings;

    [ObservableProperty] private string localCode = "-";
    [ObservableProperty] private string connectInput = "";
    [ObservableProperty] private string statusText = "Idle";
    [ObservableProperty] private string? errorBanner;
    [ObservableProperty] private string? menuBanner;

    public ObservableCollection<RecentSessionItem> RecentSessions { get; } = [];

    public HomeViewModel(
        ConnectionManager conn,
        Func<ClientSettings> settingsProvider,
        Action openSettings)
    {
        _conn = conn;
        _settingsProvider = settingsProvider;
        _openSettings = openSettings;

        LocalCode = conn.LocalCode;
        StatusText = conn.LocalEndPoint is null
            ? "Idle"
            : $"Listening on {conn.LocalEndPoint.Port}";
        ConnectInput = settingsProvider().LastConnect;

        foreach (var session in settingsProvider().RecentSessions
                     .OrderByDescending(s => s.LastConnectedUtc)
                     .Take(MaxRecentSessions))
        {
            RecentSessions.Add(RecentSessionItem.FromModel(session, ConnectAddressAsync));
        }
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        ErrorBanner = null;
        MenuBanner = null;

        var target = ConnectInput.Trim();
        if (target.Length == 0) return;

        ConnectInput = target;
        AddRecentSession(target);

        var template = _settingsProvider().ToHandshakeConfig();
        await _conn.RequestConnectionAsync(target, template);
    }

    private async Task ConnectAddressAsync(string address)
    {
        ConnectInput = address;
        await ConnectAsync();
    }

    [RelayCommand]
    private void OpenSettings() => _openSettings();

    [RelayCommand]
    private void ShowAddressBook() => MenuBanner = "Address book is not implemented yet.";

    [RelayCommand]
    private void ShowSessionRecordings() => MenuBanner = "Session recordings are not implemented yet.";

    [RelayCommand]
    private void ShowHelp() => MenuBanner = "Enter a peer code or IP address, then connect.";

    [RelayCommand]
    private void ShowAbout() => MenuBanner = "Strama remote desktop preview";

    [RelayCommand]
    private void ClearRecent()
    {
        RecentSessions.Clear();

        var settings = _settingsProvider();
        settings.RecentSessions.Clear();
        SettingsStore.Save(settings);

        StatusText = "Recent sessions cleared";
    }

    [RelayCommand]
    private void Exit()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    [RelayCommand]
    private async Task CopyCodeAsync()
    {
        var clipboard = Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow?.Clipboard
                : null;
        if (clipboard is null) return;
        await clipboard.SetTextAsync(LocalCode);
        StatusText = "Copied";
    }

    private bool CanConnect() => !string.IsNullOrWhiteSpace(ConnectInput);

    partial void OnConnectInputChanged(string value) => ConnectCommand.NotifyCanExecuteChanged();

    private void AddRecentSession(string address)
    {
        var settings = _settingsProvider();
        var existing = settings.RecentSessions.FirstOrDefault(s =>
            string.Equals(s.Address, address, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            existing = new RecentSession
            {
                Address = address,
                DisplayName = BuildDisplayName(address),
            };
            settings.RecentSessions.Add(existing);
        }

        existing.LastConnectedUtc = DateTime.UtcNow;
        existing.DisplayName = string.IsNullOrWhiteSpace(existing.DisplayName)
            ? BuildDisplayName(address)
            : existing.DisplayName;

        settings.RecentSessions = settings.RecentSessions
            .OrderByDescending(s => s.LastConnectedUtc)
            .Take(MaxRecentSessions)
            .ToList();

        SettingsStore.Save(settings);

        var item = RecentSessions.FirstOrDefault(s =>
            string.Equals(s.Address, address, StringComparison.OrdinalIgnoreCase));
        if (item is not null) RecentSessions.Remove(item);

        RecentSessions.Insert(0, RecentSessionItem.FromModel(existing, ConnectAddressAsync));
        while (RecentSessions.Count > MaxRecentSessions)
            RecentSessions.RemoveAt(RecentSessions.Count - 1);
    }

    private static string BuildDisplayName(string address)
    {
        var host = address.Split(':', 2)[0].Trim();
        return string.IsNullOrWhiteSpace(host) ? "Remote session" : host;
    }
}

public sealed class RecentSessionItem
{
    private readonly Func<string, Task> _connect;

    private RecentSessionItem(Func<string, Task> connect)
    {
        _connect = connect;
        ConnectCommand = new AsyncRelayCommand(ConnectAsync);
    }

    public string Address { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string LastSeenText { get; init; } = "";
    public bool IsFavorite { get; init; }
    public IAsyncRelayCommand ConnectCommand { get; }

    public static RecentSessionItem FromModel(RecentSession session, Func<string, Task> connect) => new(connect)
    {
        Address = session.Address,
        DisplayName = string.IsNullOrWhiteSpace(session.DisplayName)
            ? session.Address
            : session.DisplayName,
        LastSeenText = FormatLastSeen(session.LastConnectedUtc),
        IsFavorite = session.IsFavorite,
    };

    private Task ConnectAsync() => _connect(Address);

    private static string FormatLastSeen(DateTime utc)
    {
        var elapsed = DateTime.UtcNow - utc;
        if (elapsed.TotalMinutes < 1) return "just now";
        if (elapsed.TotalHours < 1) return $"{(int)elapsed.TotalMinutes} min ago";
        if (elapsed.TotalDays < 1) return $"{(int)elapsed.TotalHours} hr ago";
        if (elapsed.TotalDays < 7) return $"{(int)elapsed.TotalDays} days ago";
        return utc.ToLocalTime().ToString("MMM d");
    }
}
