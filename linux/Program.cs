using System.Net.Sockets;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace PanelSonidos;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Si ya esta abierto, se le pide que muestre su ventana y este se cierra.
        if (SingleInstance.NotifyRunning()) return 0;
        SingleInstance.Listen(() => Dispatcher.UIThread.Post(() => App.Current?.ShowMain()));

        App.StartHidden = args.Contains("--background");
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }
}

/// <summary>Una sola copia abierta, usando un socket local en la carpeta privada del usuario.</summary>
static class SingleInstance
{
    static Socket? server;

    static string SocketPath => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } dir ? dir : Path.GetTempPath(),
        "panel-sonidos.sock");

    public static bool NotifyRunning()
    {
        try
        {
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            client.Connect(new UnixDomainSocketEndPoint(SocketPath));
            client.Send(new byte[] { 1 });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void Listen(Action onShow)
    {
        try
        {
            if (File.Exists(SocketPath)) File.Delete(SocketPath); // quedo de una ejecucion que se cerro de golpe
            server = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            server.Bind(new UnixDomainSocketEndPoint(SocketPath));
            server.Listen(4);
        }
        catch
        {
            server = null;
            return;
        }

        new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var client = server.Accept();
                    onShow();
                }
                catch
                {
                    return;
                }
            }
        }) { IsBackground = true, Name = "instancia" }.Start();
    }

    public static void Stop()
    {
        try
        {
            server?.Dispose();
            File.Delete(SocketPath);
        }
        catch { }
    }
}

public sealed class App : Application
{
    public static bool StartHidden;
    public static new App? Current => Application.Current as App;

    MainWindow? window;

    public static WindowIcon LoadIcon() =>
        new(Assembly.GetExecutingAssembly().GetManifestResourceStream("app.png")!);

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            window = new MainWindow();

            var menu = new NativeMenu();
            var open = new NativeMenuItem("Abrir");
            open.Click += (_, _) => ShowMain();
            var stop = new NativeMenuItem("Detener sonidos");
            stop.Click += (_, _) => window.StopAll();
            var quit = new NativeMenuItem("Salir");
            quit.Click += (_, _) => Quit();
            menu.Items.Add(open);
            menu.Items.Add(stop);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(quit);

            var tray = new TrayIcon { Icon = LoadIcon(), ToolTipText = "Panel de sonidos", Menu = menu };
            tray.Clicked += (_, _) => ShowMain();
            TrayIcon.SetIcons(this, new TrayIcons { tray });

            if (!StartHidden) window.Show();
        }
        base.OnFrameworkInitializationCompleted();
    }

    public void ShowMain()
    {
        if (window == null) return;
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    public void Quit()
    {
        window?.Shutdown();
        SingleInstance.Stop();
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}
