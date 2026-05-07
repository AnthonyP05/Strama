using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Strama.UI.Services;

namespace Strama.UI.ViewModels;

public sealed partial class ConnectingViewModel : ViewModelBase
{
    private readonly ConnectionManager _conn;

    [ObservableProperty] private string targetCode = "—";
    [ObservableProperty] private string statusText = "Connecting…";

    public ConnectingViewModel(ConnectionManager conn, string target)
    {
        _conn      = conn;
        TargetCode = target;
    }

    [RelayCommand]
    private void Cancel() => _conn.Disconnect();

    public void SetWaitingForRemoteAccept()
    {
        StatusText = "Waiting for the remote user to accept…";
    }
}
