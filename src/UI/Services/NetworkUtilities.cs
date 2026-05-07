using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Strama.UI.Services;

public static class NetworkUtilities
{
    /// <summary>
    /// Returns the most likely LAN IPv4 address for this machine — the address
    /// the user would share as their "code" so a peer on the same network can
    /// connect. Picks the first up, non-loopback, non-virtual interface that has
    /// an IPv4 address. Falls back to 127.0.0.1 if nothing else is up.
    /// </summary>
    public static IPAddress GetLocalIPv4()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)   continue;
                if (nic.Description.Contains("Virtual",  StringComparison.OrdinalIgnoreCase)) continue;
                if (nic.Description.Contains("Hyper-V",  StringComparison.OrdinalIgnoreCase)) continue;
                if (nic.Description.Contains("VMware",   StringComparison.OrdinalIgnoreCase)) continue;
                if (nic.Description.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase)) continue;

                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                        return ua.Address;
                }
            }
        }
        catch { }
        return IPAddress.Loopback;
    }
}
