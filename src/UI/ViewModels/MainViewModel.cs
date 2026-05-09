using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Strama.UI.Services;

namespace Strama.UI.ViewModels;

public sealed partial class MainViewModel : ViewModelBase
{
    private readonly ConnectionManager _conn;
    private readonly HomeViewModel     _homeVm;

    [ObservableProperty] private ViewModelBase             currentView;
    [ObservableProperty] private IncomingRequestViewModel? incomingRequest;
    [ObservableProperty] private SettingsViewModel?        settingsModal;

    public ClientSettings Settings { get; private set; }

    public MainViewModel(ConnectionManager conn, ClientSettings settings)
    {
        _conn    = conn;
        Settings = settings;

        _homeVm = new HomeViewModel(
            conn,
            settingsProvider: () => Settings,
            openSettings:     OpenSettings);

        currentView = _homeVm;

        conn.StateChanged            += OnStateChanged;
        conn.IncomingRequestReceived += OnIncomingRequest;
        conn.HostSessionStarted      += OnHostSessionStarted;
        conn.HostEncoderResolved     += OnHostEncoderResolved;
        conn.StreamStarted           += OnStreamStarted;
        conn.SessionEnded            += OnSessionEnded;
        conn.ErrorOccurred           += msg => Post(() => _homeVm.ErrorBanner = msg);
    }

    partial void OnCurrentViewChanged(ViewModelBase? oldValue, ViewModelBase newValue)
    {
        if (oldValue is IDisposable d) d.Dispose();
    }

    partial void OnIncomingRequestChanged(IncomingRequestViewModel? oldValue, IncomingRequestViewModel? newValue)
    {
        oldValue?.Dispose();
    }

    private void OpenSettings()
    {
        SettingsModal = new SettingsViewModel(
            Settings,
            onSave:  () => SettingsStore.Save(Settings),
            onClose: () => SettingsModal = null);
    }

    private void OnStateChanged(ConnectionState state) => Post(() =>
    {
        switch (state)
        {
            case ConnectionState.Idle:
                CurrentView     = _homeVm;
                IncomingRequest = null;
                _homeVm.StatusText = _conn.LocalEndPoint is null
                    ? "Idle"
                    : $"Listening on {_conn.LocalEndPoint.Port}";
                break;

            case ConnectionState.ConnectingOutbound:
                Settings.LastConnect = _homeVm.ConnectInput;
                SettingsStore.Save(Settings);
                CurrentView = new ConnectingViewModel(_conn, _homeVm.ConnectInput);
                break;

            case ConnectionState.AwaitingRemoteAccept:
                if (CurrentView is ConnectingViewModel cvm) cvm.SetWaitingForRemoteAccept();
                break;

            case ConnectionState.Hosting:
            case ConnectionState.Viewing:
            case ConnectionState.IncomingRequest:
                break;
        }
    });

    private void OnIncomingRequest(IncomingRequest req) => Post(() =>
    {
        IncomingRequest = new IncomingRequestViewModel(_conn, req.PeerEndPoint);
    });

    private void OnHostSessionStarted(System.Net.IPEndPoint viewer, string encoder) => Post(() =>
    {
        CurrentView     = new HostingViewModel(_conn, viewer, encoder);
        IncomingRequest = null;
    });

    // Encoder picks its actual codec ("auto" → "h264_amf" etc.) shortly after
    // Run() starts. Update the live HostingViewModel if it's still on screen.
    private void OnHostEncoderResolved(string encoder) => Post(() =>
    {
        if (CurrentView is HostingViewModel hvm) hvm.EncoderLabel = encoder;
    });

    private void OnStreamStarted(StreamHandle handle) => Post(() =>
    {
        CurrentView     = new ViewingViewModel(_conn, handle);
        IncomingRequest = null;
    });

    private void OnSessionEnded(string? reason) => Post(() =>
    {
        IncomingRequest = null;
        if (reason is not null) _homeVm.ErrorBanner = reason;
    });

    private static void Post(Action a) => Dispatcher.UIThread.Post(a);
}
