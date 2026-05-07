using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Strama.Encode;
using Strama.Network;
using Strama.Network.Tcp;
using Strama.Records;

namespace Strama.UI.Services;

public enum ConnectionState
{
    Idle,
    IncomingRequest,     // peer dialed us; awaiting local accept/deny
    ConnectingOutbound,  // we dialed a peer; TCP not yet up
    AwaitingRemoteAccept,// TCP up, request sent, waiting on the remote user
    Hosting,             // we accepted; encoder is running
    Viewing,             // we requested; decoder is running
}

public sealed record IncomingRequest(IPEndPoint PeerEndPoint, DateTime ReceivedAt);

public sealed record StreamHandle(
    ChannelReader<FrameData> Frames,
    HandshakeConfig Config);

/// <summary>
/// Owns the entire connection lifecycle. There is exactly one of these per
/// running app. Both peers always run the inbound listener; whichever one
/// initiates becomes the viewer, the other becomes the host.
///
/// Threading: methods are safe to call from the UI thread. Internal work
/// happens on background tasks; events are raised on the calling thread of
/// whoever fires them — subscribers should marshal to the UI thread themselves
/// (Avalonia's Dispatcher.UIThread.Post).
/// </summary>
public sealed class ConnectionManager : IDisposable
{
    private readonly IPeerResolver        _resolver;
    private readonly int                  _tcpPort;
    private readonly int                  _udpPort;
    private readonly Func<HandshakeConfig> _hostTemplateProvider;
    private readonly object               _gate = new();

    private TcpListener?            _listener;
    private CancellationTokenSource? _listenerCts;
    private CancellationTokenSource? _sessionCts;

    // The currently-pending incoming request. While in IncomingRequest state,
    // these are the kept handles we'll either complete (accept) or close (deny).
    private TcpClient?                       _pendingTcp;
    private TaskCompletionSource<bool>?      _pendingApproval;

    // Active session.
    private TcpClient?         _sessionTcp;
    private RtpFrameEncoder?   _encoder;
    private Task?              _encodeTask;

    public ConnectionState State { get; private set; } = ConnectionState.Idle;
    public IPEndPoint?     LocalEndPoint { get; private set; }
    public string          LocalCode     { get; private set; } = "—";

    public event Action<ConnectionState>? StateChanged;
    public event Action<IncomingRequest>? IncomingRequestReceived;
    public event Action<IPEndPoint>?      HostSessionStarted;  // host side, after accept + encoder up
    public event Action<StreamHandle>?    StreamStarted;       // viewer side, once decoder is up
    public event Action<string?>?         SessionEnded;        // string is the reason or null
    public event Action<string>?          ErrorOccurred;

    public ConnectionManager(
        IPeerResolver resolver,
        int tcpPort,
        int udpPort,
        Func<HandshakeConfig> hostTemplateProvider)
    {
        _resolver             = resolver;
        _tcpPort              = tcpPort;
        _udpPort              = udpPort;
        _hostTemplateProvider = hostTemplateProvider;
    }

    /// <summary>Starts the inbound TCP listener. Call once at app startup.</summary>
    public void StartListening()
    {
        lock (_gate)
        {
            if (_listener != null) return;
            _listener = new TcpListener(IPAddress.Any, _tcpPort);
            _listener.Start();
            LocalEndPoint = (IPEndPoint)_listener.LocalEndpoint;
            LocalCode     = _resolver.LocalCode(new IPEndPoint(NetworkUtilities.GetLocalIPv4(), _tcpPort));
            _listenerCts  = new CancellationTokenSource();
        }
        _ = Task.Run(() => AcceptLoopAsync(_listenerCts!.Token));
    }

    /// <summary>Outbound connect. Resolves <paramref name="code"/>, opens TCP, sends a HandshakeRequest.</summary>
    public async Task RequestConnectionAsync(string code, HandshakeConfig template, CancellationToken ct = default)
    {
        if (!_resolver.TryResolve(code, out var ep))
        {
            ErrorOccurred?.Invoke($"Could not parse code '{code}'.");
            return;
        }

        SetState(ConnectionState.ConnectingOutbound);
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(ep.Address, ep.Port, ct);
        }
        catch (Exception ex)
        {
            tcp.Dispose();
            SetState(ConnectionState.Idle);
            ErrorOccurred?.Invoke($"Could not reach {ep}: {ex.Message}");
            return;
        }

        SetState(ConnectionState.AwaitingRemoteAccept);
        HandshakeResponse resp;
        try
        {
            resp = await HandshakeProtocol.RequestAsync(tcp, _udpPort, ct);
        }
        catch (Exception ex)
        {
            tcp.Dispose();
            SetState(ConnectionState.Idle);
            ErrorOccurred?.Invoke($"Handshake failed: {ex.Message}");
            return;
        }

        if (!resp.Accepted || resp.Config is null)
        {
            tcp.Dispose();
            SetState(ConnectionState.Idle);
            SessionEnded?.Invoke("Connection declined by remote.");
            return;
        }

        // We're the viewer. Start the decoder; remember the TCP socket so we can
        // send "disconnect" later.
        _sessionCts = new CancellationTokenSource();
        var session = UDPReceiver.Start(resp.Config, _sessionCts.Token);
        _sessionTcp = tcp;

        SetState(ConnectionState.Viewing);
        StreamStarted?.Invoke(new StreamHandle(session.Frames, resp.Config));

        _ = MonitorViewerSessionAsync(tcp, session.Running);
    }

    /// <summary>Accepts the currently-pending incoming request.</summary>
    public void AcceptIncoming()
    {
        lock (_gate)
        {
            if (State != ConnectionState.IncomingRequest || _pendingApproval is null) return;
            _pendingApproval.TrySetResult(true);
        }
    }

    /// <summary>Denies the currently-pending incoming request.</summary>
    public void DenyIncoming()
    {
        lock (_gate)
        {
            if (State != ConnectionState.IncomingRequest || _pendingApproval is null) return;
            _pendingApproval.TrySetResult(false);
        }
    }

    /// <summary>Cleanly tears down whatever session is active.</summary>
    public void Disconnect()
    {
        Task.Run(async () =>
        {
            try
            {
                if (State == ConnectionState.Viewing && _sessionTcp is not null)
                {
                    try { await HandshakeProtocol.SendDisconnectAsync(_sessionTcp.GetStream()); } catch { }
                }
                _sessionCts?.Cancel();
                if (_encodeTask is not null) { try { await _encodeTask; } catch { } }
                _sessionTcp?.Dispose();
                _sessionTcp = null;
                _encoder    = null;
                _encodeTask = null;
                SetState(ConnectionState.Idle);
                SessionEnded?.Invoke(null);
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"Disconnect error: {ex.Message}");
            }
        });
    }

    // ─── Internals ────────────────────────────────────────────────────────────

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await _listener!.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"Listener error: {ex.Message}");
                continue;
            }

            // Reject if we're already in a session — accepting would orphan it.
            if (State != ConnectionState.Idle)
            {
                tcp.Dispose();
                continue;
            }

            _ = HandleIncomingAsync(tcp, ct);
        }
    }

    private async Task HandleIncomingAsync(TcpClient tcp, CancellationToken ct)
    {
        try
        {
            // Pull the latest user-configured template at accept time so settings
            // changes apply to fresh sessions without needing a manager rebuild.
            var template = _hostTemplateProvider();
            template.TcpPort = _tcpPort;
            var effective = await HandshakeProtocol.AcceptAsync(
                tcp,
                approveAsync: peer =>
                {
                    var tcs = new TaskCompletionSource<bool>();
                    lock (_gate)
                    {
                        _pendingTcp      = tcp;
                        _pendingApproval = tcs;
                    }
                    SetState(ConnectionState.IncomingRequest);
                    IncomingRequestReceived?.Invoke(new IncomingRequest(peer, DateTime.UtcNow));
                    return tcs.Task;
                },
                localTemplate: template,
                ct: ct);

            lock (_gate)
            {
                _pendingTcp      = null;
                _pendingApproval = null;
            }

            if (effective is null)
            {
                tcp.Dispose();
                SetState(ConnectionState.Idle);
                SessionEnded?.Invoke(null);
                return;
            }

            // We accepted. Spin up the encoder and remember the TCP socket so we
            // can wait for the requester's "disconnect".
            _sessionCts = new CancellationTokenSource();
            _encoder    = new RtpFrameEncoder(effective);
            _encodeTask = Task.Run(() => _encoder.Run(_sessionCts.Token));
            _sessionTcp = tcp;

            var viewerEp = (IPEndPoint)tcp.Client.RemoteEndPoint!;
            SetState(ConnectionState.Hosting);
            HostSessionStarted?.Invoke(viewerEp);

            _ = MonitorHostSessionAsync(tcp);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"Incoming handshake failed: {ex.Message}");
            tcp.Dispose();
            SetState(ConnectionState.Idle);
        }
    }

    private async Task MonitorHostSessionAsync(TcpClient tcp)
    {
        try
        {
            var msg = await HandshakeProtocol.WaitForDisconnectAsync(tcp.GetStream());
            _sessionCts?.Cancel();
            if (_encodeTask is not null) { try { await _encodeTask; } catch { } }

            if (msg == "disconnect")
            {
                try { await HandshakeProtocol.AckDisconnectAsync(tcp.GetStream()); } catch { }
            }

            tcp.Dispose();
            _sessionTcp = null;
            _encoder    = null;
            _encodeTask = null;
            SetState(ConnectionState.Idle);
            SessionEnded?.Invoke(null);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"Host session error: {ex.Message}");
            SetState(ConnectionState.Idle);
        }
    }

    private async Task MonitorViewerSessionAsync(TcpClient tcp, Task decoderRunning)
    {
        try
        {
            await decoderRunning;
            tcp.Dispose();
            _sessionTcp = null;
            SetState(ConnectionState.Idle);
            SessionEnded?.Invoke(null);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"Viewer session error: {ex.Message}");
            SetState(ConnectionState.Idle);
        }
    }

    private void SetState(ConnectionState next)
    {
        if (State == next) return;
        State = next;
        StateChanged?.Invoke(next);
    }

    public void Dispose()
    {
        _listenerCts?.Cancel();
        _listener?.Stop();
        _sessionCts?.Cancel();
        _sessionTcp?.Dispose();
    }
}
