using System.Net;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Strama.UI.Services;

namespace Strama.UI.ViewModels;

public sealed partial class IncomingRequestViewModel : ViewModelBase, IDisposable
{
    private const int TimeoutSeconds = 30;

    private readonly ConnectionManager _conn;
    private readonly DispatcherTimer   _timer;
    private DateTime                   _expiresAt;

    [ObservableProperty] private string peerLabel;
    [ObservableProperty] private int    secondsRemaining = TimeoutSeconds;

    public IncomingRequestViewModel(ConnectionManager conn, IPEndPoint peer)
    {
        _conn      = conn;
        peerLabel  = peer.Address.ToString();
        _expiresAt = DateTime.UtcNow.AddSeconds(TimeoutSeconds);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var remaining = (int)Math.Max(0, (_expiresAt - DateTime.UtcNow).TotalSeconds);
        SecondsRemaining = remaining;
        if (remaining <= 0)
        {
            _timer.Stop();
            _conn.DenyIncoming();
        }
    }

    [RelayCommand] private void Allow() { _timer.Stop(); _conn.AcceptIncoming(); }
    [RelayCommand] private void Deny()  { _timer.Stop(); _conn.DenyIncoming(); }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
    }
}
