using System.Net;

namespace Strama.UI.Services;

/// <summary>
/// Maps between displayable "codes" and IPEndPoints. v1 implementation is
/// IP-as-code; a rendezvous-server-backed implementation can be plugged in later
/// without changing ConnectionManager or any view.
/// </summary>
public interface IPeerResolver
{
    /// <summary>
    /// Builds the displayable code for this machine, given its local listener
    /// endpoint. Used to populate the "Your code" badge in the home view.
    /// </summary>
    string LocalCode(IPEndPoint local);

    /// <summary>
    /// Parses a code typed by the user into an IPEndPoint. Returns false if the
    /// code is malformed.
    /// </summary>
    bool TryResolve(string code, out IPEndPoint endpoint);
}
