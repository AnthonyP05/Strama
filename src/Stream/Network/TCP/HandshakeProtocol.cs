using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Strama.Records;

namespace Strama.Network.Tcp;

// Wire format for the TCP control channel — used by both peers in the symmetric
// peer-to-peer model. Either side can RequestAsync (initiate a session) or
// AcceptAsync (respond to one).
//
// Sequence (requester ↔ accepter):
//   1. requester  → JSON HandshakeRequest { Magic, UdpPort }
//   2. accepter   → JSON HandshakeResponse { Accepted, Config? }
//                   - Config carries the encoder settings the accepter will use,
//                     plus UdpIP/UdpPort (the requester's UDP destination, derived
//                     from the TCP remote endpoint and the requester-supplied port)
//   3. (RTP/UDP stream flows from accepter to requester)
//   4. requester  → "disconnect" UTF-8
//   5. accepter   → "ok" UTF-8
//
// The magic field guards against random TCP traffic landing on our port.
public sealed record HandshakeRequest(string Magic, int UdpPort);

public static class HandshakeProtocol
{
    public const string Magic = "Strama-v1";

    /// <summary>
    /// Requester side. Sends a HandshakeRequest, awaits HandshakeResponse, returns it.
    /// On Accepted=true, the response's Config has UdpIP/UdpPort filled in pointing
    /// at this peer (the requester) — pass it to UDPReceiver to set up the decoder.
    /// </summary>
    public static async Task<HandshakeResponse> RequestAsync(
        TcpClient tcp, int udpPort, CancellationToken ct = default)
    {
        var stream = tcp.GetStream();

        var req = new HandshakeRequest(Magic, udpPort);
        byte[] reqBytes = JsonSerializer.SerializeToUtf8Bytes(req);
        await stream.WriteAsync(reqBytes, ct);

        var buf  = new byte[4096];
        int read = await stream.ReadAsync(buf, ct);
        if (read == 0) throw new IOException("Accepter closed the connection without responding.");

        return JsonSerializer.Deserialize<HandshakeResponse>(buf.AsSpan(0, read))
               ?? throw new InvalidDataException("Accepter sent an empty/invalid HandshakeResponse.");
    }

    /// <summary>
    /// Accepter side. Reads a HandshakeRequest, calls <paramref name="approveAsync"/>
    /// with the requester's IPEndPoint to ask the local user. On approval, builds a
    /// HandshakeConfig from <paramref name="localTemplate"/> with UdpIP/UdpPort
    /// pointing at the requester, sends an Accepted=true response, and returns
    /// the config so the caller can spin up the encoder.
    ///
    /// On denial, sends Accepted=false and returns null.
    /// </summary>
    public static async Task<HandshakeConfig?> AcceptAsync(
        TcpClient tcp,
        Func<IPEndPoint, Task<bool>> approveAsync,
        HandshakeConfig localTemplate,
        CancellationToken ct = default)
    {
        var stream = tcp.GetStream();
        var remote = (IPEndPoint)tcp.Client.RemoteEndPoint!;

        var buf  = new byte[4096];
        int read = await stream.ReadAsync(buf, ct);
        if (read == 0) throw new IOException("Requester closed the connection without sending a request.");

        HandshakeRequest? req;
        try
        {
            req = JsonSerializer.Deserialize<HandshakeRequest>(buf.AsSpan(0, read));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Requester sent a non-JSON payload — not a Strama peer.", ex);
        }

        if (req is null || req.Magic != Magic)
            throw new InvalidDataException($"Magic mismatch: expected '{Magic}', got '{req?.Magic ?? "(null)"}'.");

        bool approved = await approveAsync(remote);

        if (!approved)
        {
            var deny = new HandshakeResponse { Accepted = false, Config = null };
            await stream.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(deny), ct);
            return null;
        }

        // Build the effective config: accepter's encoder settings + requester's UDP destination.
        var effective = new HandshakeConfig
        {
            UdpIP         = remote.Address.ToString(),
            UdpPort       = req.UdpPort,
            TcpIP         = localTemplate.TcpIP,
            TcpPort       = localTemplate.TcpPort,
            CaptureWidth  = localTemplate.CaptureWidth,
            CaptureHeight = localTemplate.CaptureHeight,
            OutputWidth   = localTemplate.OutputWidth,
            OutputHeight  = localTemplate.OutputHeight,
            Framerate     = localTemplate.Framerate,
            Bitrate       = localTemplate.Bitrate,
            Encoder       = localTemplate.Encoder,
        };

        var ok = new HandshakeResponse { Accepted = true, Config = effective };
        await stream.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(ok), ct);
        return effective;
    }

    /// <summary>
    /// Blocks until the peer sends "disconnect" (or the stream closes). Caller
    /// should run this on a background task while the stream is active.
    /// </summary>
    public static async Task<string> WaitForDisconnectAsync(NetworkStream stream, CancellationToken ct = default)
    {
        var buf  = new byte[64];
        int read = await stream.ReadAsync(buf, ct);
        return read == 0 ? "" : Encoding.UTF8.GetString(buf, 0, read);
    }

    public static async Task SendDisconnectAsync(NetworkStream stream, CancellationToken ct = default)
    {
        await stream.WriteAsync("disconnect"u8.ToArray(), ct);
    }

    public static async Task AckDisconnectAsync(NetworkStream stream, CancellationToken ct = default)
    {
        await stream.WriteAsync("ok"u8.ToArray(), ct);
    }
}
