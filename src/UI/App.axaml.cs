using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Strama.UI.Services;
using Strama.UI.ViewModels;
using Strama.UI.Views;

namespace Strama;

public partial class App : Application
{
    private ConnectionManager? _conn;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Load persisted settings; CLI flags can override TCP/UDP for the
            // two-instances-on-one-machine test scenario.
            var settings    = SettingsStore.Load();
            settings.TcpPort = ResolvePortArg(desktop.Args, "--tcp", settings.TcpPort);
            settings.UdpPort = ResolvePortArg(desktop.Args, "--udp", settings.UdpPort);

            var resolver = new IpEndpointResolver(settings.TcpPort);

            // The provider closes over the same settings object the GUI mutates,
            // so settings changes apply to fresh sessions automatically.
            _conn = new ConnectionManager(resolver, settings.TcpPort, settings.UdpPort,
                                          () => settings.ToHandshakeConfig());
            _conn.StartListening();

            var mainVm = new MainViewModel(_conn, settings);
            desktop.MainWindow = new MainWindow { DataContext = mainVm };
            desktop.ShutdownRequested += (_, _) => _conn.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static int ResolvePortArg(string[]? args, string flag, int fallback)
    {
        if (args is null) return fallback;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == flag && int.TryParse(args[i + 1], out var p) && p is > 0 and < 65536)
                return p;
        return fallback;
    }
}
