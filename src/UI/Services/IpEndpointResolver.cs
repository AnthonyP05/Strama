using System.Net;

namespace Strama.UI.Services;

/// <summary>
/// v1 peer resolver. The "code" is just the dotted IP — optionally with a
/// :port suffix when non-default. Trivial to read aloud, robust copy-paste,
/// no rendezvous-server dependency.
/// </summary>
public sealed class IpEndpointResolver(int defaultPort = 8888) : IPeerResolver
{
    public int DefaultPort { get; } = defaultPort;

    public string LocalCode(IPEndPoint local)
        => local.Port == DefaultPort
           ? local.Address.ToString()
           : $"{local.Address}:{local.Port}";

    public bool TryResolve(string code, out IPEndPoint endpoint)
    {
        endpoint = null!;
        if (string.IsNullOrWhiteSpace(code)) return false;

        code = code.Trim();
        int colon = code.IndexOf(':');
        string host = colon < 0 ? code : code[..colon];

        int port;
        if (colon < 0)
        {
            port = DefaultPort;
        }
        else if (!int.TryParse(code[(colon + 1)..], out port) || port is < 1 or > 65535)
        {
            return false;
        }

        if (!IPAddress.TryParse(host, out var addr)) return false;

        endpoint = new IPEndPoint(addr, port);
        return true;
    }
}
