using System.Net;
using System.Net.Sockets;
using Avalonia;
using Strama.Encode;
using Strama.Network;
using Strama.Network.Tcp;
using Strama.Records;

namespace Strama;

// Application entry point.
//
// By default, launches the Avalonia GUI:    Strama
// Headless console fallback (debug only):
//     Strama --console host                 host mode
//     Strama --console <ip>[:<port>]        connect mode
//
// The console mode is the Phase 1 scaffold preserved for regression-testing the
// transport/handshake without involving the GUI. Once the GUI is feature-complete
// it can be removed.
internal static class Program
{
    private const int DefaultTcpPort = 8888;
    private const int DefaultUdpPort = 8889;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "--console")
        {
            // Console mode is the transport regression harness — keep it verbose.
            DebugLog.Enabled = true;
            return RunConsole(args[1..]).GetAwaiter().GetResult();
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Exposed because the Avalonia previewer / designer expects this entry point.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
                  .UsePlatformDetect()
                  .WithInterFont()
                  .LogToTrace();

    // ─── Console mode (Phase 1 scaffold) ──────────────────────────────────────
    private static async Task<int> RunConsole(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] == "host")
                await RunHostAsync();
            else
                await RunConnectAsync(args[0]);
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Fatal] {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException is not null)
                Console.WriteLine($"        {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
            return 1;
        }
    }

    private static async Task RunHostAsync()
    {
        var listener = new TcpListener(IPAddress.Any, DefaultTcpPort);
        listener.Start();
        Console.WriteLine($"[Host] Listening on {DefaultTcpPort} — Ctrl+C to exit");

        while (true)
        {
            using var tcp = await listener.AcceptTcpClientAsync();
            var remote = (IPEndPoint)tcp.Client.RemoteEndPoint!;
            Console.WriteLine($"[Host] Incoming from {remote}");

            try
            {
                var template = new HandshakeConfig();
                var effective = await HandshakeProtocol.AcceptAsync(
                    tcp,
                    approveAsync: peer => Task.FromResult(PromptAllow(peer)),
                    localTemplate: template);

                if (effective is null)
                {
                    Console.WriteLine("[Host] Denied.");
                    continue;
                }

                Console.WriteLine($"[Host] Accepted. Streaming to {effective.UdpIP}:{effective.UdpPort}");
                using var encoderCts = new CancellationTokenSource();
                var encoder    = new RtpFrameEncoder(effective);
                var encodeTask = Task.Run(() => encoder.Run(encoderCts.Token));

                var msg = await HandshakeProtocol.WaitForDisconnectAsync(tcp.GetStream());
                Console.WriteLine($"[Host] Requester said: '{msg}' — stopping encoder");

                encoderCts.Cancel();
                await encodeTask;

                if (msg == "disconnect")
                    await HandshakeProtocol.AckDisconnectAsync(tcp.GetStream());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Host] Session error: {ex.Message}");
            }
        }
    }

    private static async Task RunConnectAsync(string code)
    {
        if (!TryParseCode(code, out IPEndPoint ep))
        {
            Console.WriteLine($"[Connect] Could not parse code '{code}'. Expected 'IP' or 'IP:port'.");
            return;
        }

        Console.WriteLine($"[Connect] Dialing {ep}");
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(ep.Address, ep.Port);
        Console.WriteLine("[Connect] TCP up; sending HandshakeRequest");

        var resp = await HandshakeProtocol.RequestAsync(tcp, DefaultUdpPort);
        if (!resp.Accepted || resp.Config is null)
        {
            Console.WriteLine("[Connect] Host denied or returned no config.");
            return;
        }

        Console.WriteLine($"[Connect] Accepted; receiving on UDP {DefaultUdpPort}");
        using var streamCts = new CancellationTokenSource();
        var session = UDPReceiver.Start(resp.Config, streamCts.Token);

        var statsTask = Task.Run(async () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int n = 0;
            try
            {
                await foreach (var f in session.Frames.ReadAllAsync(streamCts.Token))
                {
                    n++;
                    if (sw.ElapsedMilliseconds >= 1000)
                    {
                        Console.WriteLine($"[Connect] {n} fps {f.Width}x{f.Height}");
                        n  = 0;
                        sw.Restart();
                    }
                    f.Dispose();
                }
            }
            catch (OperationCanceledException) { }
        });

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            streamCts.Cancel();
        };

        await session.Running;
        await statsTask;

        await HandshakeProtocol.SendDisconnectAsync(tcp.GetStream());
        var ackBuf = new byte[16];
        try { _ = await tcp.GetStream().ReadAsync(ackBuf); } catch { }
        Console.WriteLine("[Connect] Disconnected.");
    }

    private static bool PromptAllow(IPEndPoint peer)
    {
        Console.Write($"[Host] Allow {peer} to view your screen? [y/N]: ");
        string? line = Console.ReadLine();
        return line?.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool TryParseCode(string code, out IPEndPoint ep)
    {
        ep = null!;
        int colon = code.IndexOf(':');
        string host = colon < 0 ? code : code[..colon];
        int    port = colon < 0 ? DefaultTcpPort : int.Parse(code[(colon + 1)..]);

        if (!IPAddress.TryParse(host, out var addr)) return false;
        ep = new IPEndPoint(addr, port);
        return true;
    }
}
