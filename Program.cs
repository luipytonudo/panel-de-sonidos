namespace PanelSonidos;

static class Program
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);

    [STAThread]
    static void Main(string[] args)
    {
        using var instance = new Mutex(true, @"Local\PanelSonidos_Instancia", out bool isFirst);
        using var showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\PanelSonidos_Mostrar");
        if (!isFirst)
        {
            // Ya esta abierto en segundo plano: se pide a esa copia que muestre su ventana,
            // cediendole el permiso de Windows para ponerse al frente.
            AllowSetForegroundWindow(-1);
            showRequest.Set();
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayContext(showRequest, args.Contains("--background")));
    }
}

/// <summary>La app vive en la bandeja del sistema; la ventana solo aparece cuando se necesita.</summary>
sealed class TrayContext : ApplicationContext
{
    readonly MainForm form = new();
    readonly NotifyIcon tray;

    public TrayContext(EventWaitHandle showRequest, bool autostarted)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Abrir panel", null, (_, _) => ShowWindow());
        menu.Items.Add("Detener sonidos", null, (_, _) => form.StopAll());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Salir", null, (_, _) => Quit());

        tray = new NotifyIcon
        {
            Icon = form.Icon,
            Text = "Panel de sonidos",
            ContextMenuStrip = menu,
            Visible = true,
        };
        tray.DoubleClick += (_, _) => ShowWindow();

        _ = form.Handle;
        form.Initialize();

        ThreadPool.RegisterWaitForSingleObject(showRequest,
            (_, _) => form.BeginInvoke(ShowWindow), null, Timeout.Infinite, false);

        if (form.HasSounds)
        {
            if (!autostarted)
                tray.ShowBalloonTip(2500, "Panel de sonidos", "Activo en segundo plano. Haz doble clic en este icono para abrirlo.", ToolTipIcon.None);
        }
        else if (!autostarted)
        {
            ShowWindow();
        }
    }

    void ShowWindow()
    {
        form.Show();
        if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
        form.Activate();
    }

    void Quit()
    {
        tray.Visible = false;
        tray.Dispose();
        form.Shutdown();
        ExitThread();
    }
}
