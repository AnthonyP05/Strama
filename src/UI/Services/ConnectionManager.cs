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
    HandshakeConfig Config,
    Func<long> GetTotalNetworkBytes);

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
    private int                _tearingDown;     // Interlocked flag — guards against double-teardown

    public ConnectionState State { get; private set; } = ConnectionState.Idle;
    public IPEndPoint?     LocalEndPoint { get; private set; }
    public string          LocalCode     { get; private set; } = "—";

    public event Action<ConnectionState>?      StateChanged;
    public event Action<IncomingRequest>?      IncomingRequestReceived;
    public event Action<IPEndPoint, string>?   HostSessionStarted;     // (viewerEp, configuredEncoder — may be "auto")
    public event Action<string>?               HostEncoderResolved;    // fired once the encoder picks the actual codec
    public event Action<StreamHandle>?         StreamStarted;          // viewer side, once decoder is up
    public event Action<string?>?              SessionEnded;           // string is the reason or null
    public event Action<string>?               ErrorOccurred;

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
            int port = _tcpPort;
            while (true)
            {
                try
                {
                    _listener = new TcpListener(IPAddress.Any, port);
                    _listener.Start();
                    break;
                }
                catch (SocketException)
                {
                    _listener = null;
                    port++;
                }
            }
            LocalEndPoint = (IPEndPoint)_listener.LocalEndpoint;
            LocalCode     = _resolver.LocalCode(new IPEndPoint(NetworkUtilities.GetLocalIPv4(), port));
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
        int udpPort = FindFreeUdpPort();
        try
        {
            resp = await HandshakeProtocol.RequestAsync(tcp, udpPort, ct);
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
        StreamStarted?.Invoke(new StreamHandle(session.Frames, resp.Config, session.GetTotalNetworkBytes));

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
    public void Disconnect() => _ = TearDownSessionAsync(reason: null, sendDisconnectMessage: true);

    // Single source of truth for session cleanup. Both monitor tasks and the
    // user-initiated Disconnect() funnel through here, and the Interlocked flag
    // guarantees the body runs once even if multiple paths fire concurrently.
    private async Task TearDownSessionAsync(string? reason, bool sendDisconnectMessage)
    {
        if (Interlocked.Exchange(ref _tearingDown, 1) == 1) return;

        try
        {
            // Viewer-initiated disconnect: politely tell the host first. Best
            // effort — if the socket is already half-closed we just continue.
            if (sendDisconnectMessage && State == ConnectionState.Viewing && _sessionTcp is not null)
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
            SessionEnded?.Invoke(reason);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke($"Teardown error: {ex.Message}");
        }
        finally
        {
            _tearingDown = 0;
        }
    }

    // Binds a UDP socket to port 0 to let the OS pick a free port, then reads
    // back the assigned port. Using a fresh port each session prevents the stuck
    // decoder from a prior session (blocked in av_read_frame) from blocking the
    // OS bind for the new session's receive socket.
    private static int FindFreeUdpPort()
    {
        using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Bind(new IPEndPoint(IPAddress.Any, 0));
        return ((IPEndPoint)s.LocalEndPoint!).Port;
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
            // Wire up BEFORE starting — Run() fires this almost immediately, and
            // we don't want to miss the only emission for this session.
            _encoder.EncoderResolved += OnEncoderResolved;
            _encodeTask = Task.Run(() => _encoder.Run(_sessionCts.Token));
            _sessionTcp = tcp;

            var viewerEp = (IPEndPoint)tcp.Client.RemoteEndPoint!;
            SetState(ConnectionState.Hosting);
            HostSessionStarted?.Invoke(viewerEp, effective.Encoder);

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
            // Block until the viewer either sends "disconnect" or closes its
            // socket (returns empty string in that case).
            var msg = await HandshakeProtocol.WaitForDisconnectAsync(tcp.GetStream());
            if (msg == "disconnect")
            {
                try { await HandshakeProtocol.AckDisconnectAsync(tcp.GetStream()); } catch { }
            }
        }
        catch { /* socket aborted — proceed to teardown */ }

        await TearDownSessionAsync(reason: null, sendDisconnectMessage: false);
    }

    private async Task MonitorViewerSessionAsync(TcpClient tcp, Task decoderRunning)
    {
        // Race the decoder finishing against the host closing its TCP socket.
        // Without this the viewer would stay in ViewingView indefinitely when
        // the host clicks Disconnect: the host's TCP closes, but UDP just goes
        // silent, so the decoder never errors out and the UI never hears about it.
        var tcpClosed = Task.Run(async () =>
        {
            try { await HandshakeProtocol.WaitForDisconnectAsync(tcp.GetStream()); }
            catch { /* socket aborted is fine — same outcome */ }
        });

        try
        {
            await Task.WhenAny(decoderRunning, tcpClosed);
        }
        catch { /* swallowed — the teardown below covers cleanup */ }

        await TearDownSessionAsync(reason: null, sendDisconnectMessage: false);
    }

    private void OnEncoderResolved(string encoder) => HostEncoderResolved?.Invoke(encoder);

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
