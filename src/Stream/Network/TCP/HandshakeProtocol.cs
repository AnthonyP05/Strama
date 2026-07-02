using System.Buffers.Binary;
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
// Every message is a frame: a 4-byte little-endian payload length followed by the
// payload. TCP is a byte stream with no message boundaries — a single ReadAsync can
// legally return half a JSON document (seen in practice over VPN/WAN paths), so the
// old one-read-per-message scheme mis-parsed real peers under fragmentation.
//
// Sequence (requester ↔ accepter):
//   1. requester  → frame: JSON HandshakeRequest { Magic, UdpPort }
//   2. accepter   → frame: JSON HandshakeResponse { Accepted, Config? }
//                   - Config carries the encoder settings the accepter will use,
//                     plus UdpIP/UdpPort (the requester's UDP destination, derived
//                     from the TCP remote endpoint and the requester-supplied port)
//   3. (RTP/UDP stream flows from accepter to requester)
//   4. requester  → frame: "disconnect" UTF-8
//   5. accepter   → frame: "ok" UTF-8
//
// The magic field guards against random TCP traffic landing on our port; the frame
// length sanity check rejects non-Strama (or pre-framing) peers before JSON parsing.
public sealed record HandshakeRequest(string Magic, int UdpPort);

public static class HandshakeProtocol
{
    // v2: length-prefixed frames (v1 wrote bare JSON documents).
    public const string Magic = "Strama-v2";

    // Control messages are small (a config record or a short string). Anything
    // claiming to be bigger is not a Strama peer — reject before allocating.
    private const int MaxFrameBytes = 64 * 1024;

    private static async Task WriteFrameAsync(NetworkStream stream, byte[] payload, CancellationToken ct = default)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(payload, ct);
    }

    /// <summary>
    /// Reads one length-prefixed frame. Returns null if the peer closed the
    /// connection cleanly before sending a frame; throws on truncation mid-frame
    /// or on an implausible length (non-Strama traffic).
    /// </summary>
    private static async Task<byte[]?> ReadFrameAsync(NetworkStream stream, CancellationToken ct = default)
    {
        var header = new byte[4];
        try
        {
            await stream.ReadExactlyAsync(header, ct);
        }
        catch (EndOfStreamException)
        {
            return null; // clean close between frames
        }

        int size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size is < 0 or > MaxFrameBytes)
            throw new InvalidDataException(
                $"Invalid frame length {size} — not a Strama peer (or an older, pre-framing build).");

        var payload = new byte[size];
        await stream.ReadExactlyAsync(payload, ct); // truncation → EndOfStreamException
        return payload;
    }

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
        await WriteFrameAsync(stream, JsonSerializer.SerializeToUtf8Bytes(req), ct);

        byte[]? payload = await ReadFrameAsync(stream, ct)
            ?? throw new IOException("Accepter closed the connection without responding.");

        return JsonSerializer.Deserialize<HandshakeResponse>(payload)
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

        byte[]? payload = await ReadFrameAsync(stream, ct)
            ?? throw new IOException("Requester closed the connection without sending a request.");

        HandshakeRequest? req;
        try
        {
            req = JsonSerializer.Deserialize<HandshakeRequest>(payload);
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
            await WriteFrameAsync(stream, JsonSerializer.SerializeToUtf8Bytes(deny), ct);
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
        await WriteFrameAsync(stream, JsonSerializer.SerializeToUtf8Bytes(ok), ct);
        return effective;
    }

    /// <summary>
    /// Blocks until the peer sends "disconnect" (or the stream closes — returns
    /// "" in that case). Caller should run this on a background task while the
    /// stream is active.
    /// </summary>
    public static async Task<string> WaitForDisconnectAsync(NetworkStream stream, CancellationToken ct = default)
    {
        byte[]? payload = await ReadFrameAsync(stream, ct);
        return payload is null ? "" : Encoding.UTF8.GetString(payload);
    }

    public static async Task SendDisconnectAsync(NetworkStream stream, CancellationToken ct = default)
    {
        await WriteFrameAsync(stream, "disconnect"u8.ToArray(), ct);
    }

    public static async Task AckDisconnectAsync(NetworkStream stream, CancellationToken ct = default)
    {
        await WriteFrameAsync(stream, "ok"u8.ToArray(), ct);
    }
}
