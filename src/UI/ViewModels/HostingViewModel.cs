using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Strama.UI.Services;

namespace Strama.UI.ViewModels;

public sealed partial class HostingViewModel : ViewModelBase
{
    private readonly ConnectionManager _conn;

    [ObservableProperty] private string peerLabel;
    [ObservableProperty] private string statsText = "Streaming…";

    public HostingViewModel(ConnectionManager conn, IPEndPoint viewer)
    {
        _conn     = conn;
        peerLabel = viewer.Address.ToString();
    }

    [RelayCommand] private void Disconnect() => _conn.Disconnect();
}
