using System.Runtime.InteropServices;
using Avalonia.Input;
using Avalonia.Threading;

namespace PanelSonidos;

/// <summary>
/// Teclas globales con X11: funcionan aunque la ventana este oculta.
/// Todo lo de X11 ocurre en un hilo propio con su propia conexion a la pantalla.
/// </summary>
public sealed class GlobalHotkeys : IDisposable
{
    public const uint ModShift = 1, ModCtrl = 4, ModAlt = 8;
    const uint LockMask = 2, NumLockMask = 16;
    const int KeyPress = 2, KeyRelease = 3, GrabModeAsync = 1;
    const string X11 = "libX11.so.6";

    [DllImport(X11)] static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport(X11)] static extern int XCloseDisplay(IntPtr display);
    [DllImport(X11)] static extern nint XDefaultRootWindow(IntPtr display);
    [DllImport(X11)] static extern byte XKeysymToKeycode(IntPtr display, nint keysym);
    [DllImport(X11)] static extern int XGrabKey(IntPtr display, int keycode, uint modifiers, nint window, int ownerEvents, int pointerMode, int keyboardMode);
    [DllImport(X11)] static extern int XUngrabKey(IntPtr display, int keycode, uint modifiers, nint window);
    [DllImport(X11)] static extern int XPending(IntPtr display);
    [DllImport(X11)] static extern int XNextEvent(IntPtr display, IntPtr eventReturn);
    [DllImport(X11)] static extern int XSync(IntPtr display, int discard);
    [DllImport(X11)] static extern IntPtr XSetErrorHandler(IntPtr handler);
    [DllImport(X11)] static extern int XkbSetDetectableAutoRepeat(IntPtr display, int detectable, IntPtr supported);

    delegate int ErrorHandler(IntPtr display, IntPtr errorEvent);
    static volatile bool grabFailed;
    static readonly ErrorHandler OnXError = (_, _) => { grabFailed = true; return 0; };
    static readonly IntPtr OnXErrorPointer = Marshal.GetFunctionPointerForDelegate(OnXError);
    static readonly uint[] IgnoredLocks = { 0, LockMask, NumLockMask, LockMask | NumLockMask };

    readonly Thread thread;
    readonly object gate = new();
    volatile bool running = true;
    List<(uint Mods, uint Keysym, Action Action)>? pending;
    TaskCompletionSource<bool[]>? pendingResult;

    /// <summary>Motivo por el que no hay teclas globales, o null si funcionan.</summary>
    public string? Error { get; private set; }

    public static bool IsWayland => Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") == "wayland";

    public GlobalHotkeys()
    {
        using var ready = new ManualResetEventSlim();
        thread = new Thread(() => Loop(ready)) { IsBackground = true, Name = "teclas" };
        thread.Start();
        ready.Wait(3000);
    }

    /// <summary>Reemplaza todas las teclas. Devuelve, para cada una, si se pudo reservar.</summary>
    public bool[] Set(IReadOnlyList<(uint Mods, uint Keysym, Action Action)> keys)
    {
        if (Error != null || !thread.IsAlive) return new bool[keys.Count];
        var result = new TaskCompletionSource<bool[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            pending = keys.ToList();
            pendingResult = result;
        }
        return result.Task.Wait(2000) ? result.Task.Result : new bool[keys.Count];
    }

    void Loop(ManualResetEventSlim ready)
    {
        IntPtr display;
        try
        {
            display = XOpenDisplay(IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Error = "no se pudo usar X11 (" + ex.GetType().Name + ")";
            ready.Set();
            return;
        }
        if (display == IntPtr.Zero)
        {
            Error = "no hay pantalla X11";
            ready.Set();
            return;
        }

        var root = XDefaultRootWindow(display);
        // Sin esto, mantener una tecla pulsada repetiria el sonido sin parar.
        XkbSetDetectableAutoRepeat(display, 1, IntPtr.Zero);
        ready.Set();

        var active = new List<(byte Code, uint Mods, Action Action)>();
        var held = new HashSet<byte>();
        var xEvent = Marshal.AllocHGlobal(256);
        try
        {
            while (running)
            {
                List<(uint Mods, uint Keysym, Action Action)>? keys;
                TaskCompletionSource<bool[]>? result;
                lock (gate)
                {
                    keys = pending;
                    result = pendingResult;
                    pending = null;
                    pendingResult = null;
                }

                if (keys != null)
                {
                    foreach (var key in active) Ungrab(display, root, key.Code, key.Mods);
                    active.Clear();
                    var ok = new bool[keys.Count];
                    for (int i = 0; i < keys.Count; i++)
                    {
                        byte code = XKeysymToKeycode(display, (nint)keys[i].Keysym);
                        ok[i] = code != 0 && Grab(display, root, code, keys[i].Mods);
                        if (ok[i]) active.Add((code, keys[i].Mods, keys[i].Action));
                    }
                    result!.SetResult(ok);
                }

                while (XPending(display) > 0)
                {
                    XNextEvent(display, xEvent);
                    int type = Marshal.ReadInt32(xEvent, 0);
                    if (type != KeyPress && type != KeyRelease) continue;

                    // Posiciones de "state" y "keycode" dentro de XKeyEvent en sistemas de 64 bits.
                    uint state = (uint)Marshal.ReadInt32(xEvent, 80);
                    byte code = (byte)Marshal.ReadInt32(xEvent, 84);
                    if (type == KeyRelease)
                    {
                        held.Remove(code);
                        continue;
                    }
                    if (!held.Add(code)) continue;

                    uint mods = state & (ModShift | ModCtrl | ModAlt);
                    foreach (var key in active)
                        if (key.Code == code && key.Mods == mods)
                            Dispatcher.UIThread.Post(key.Action);
                }

                Thread.Sleep(10);
            }
        }
        finally
        {
            foreach (var key in active) Ungrab(display, root, key.Code, key.Mods);
            Marshal.FreeHGlobal(xEvent);
            XCloseDisplay(display);
        }
    }

    static bool Grab(IntPtr display, nint root, byte code, uint mods)
    {
        // Si otro programa ya tiene la tecla, X11 manda un error; por defecto eso cerraria el programa.
        grabFailed = false;
        var previous = XSetErrorHandler(OnXErrorPointer);
        foreach (var locks in IgnoredLocks)
            XGrabKey(display, code, mods | locks, root, 0, GrabModeAsync, GrabModeAsync);
        XSync(display, 0);
        XSetErrorHandler(previous);

        if (!grabFailed) return true;
        Ungrab(display, root, code, mods);
        return false;
    }

    static void Ungrab(IntPtr display, nint root, byte code, uint mods)
    {
        foreach (var locks in IgnoredLocks) XUngrabKey(display, code, mods | locks, root);
        XSync(display, 0);
    }

    public void Dispose()
    {
        running = false;
        thread.Join(500);
    }
}

public static class KeyMap
{
    /// <summary>Traduce una tecla de la ventana al codigo de X11 y a un nombre para mostrar.</summary>
    public static bool TryGet(Key key, out uint keysym, out string label)
    {
        keysym = 0;
        label = "";
        if (key is >= Key.A and <= Key.Z)
        {
            keysym = (uint)('a' + (key - Key.A));
            label = ((char)('A' + (key - Key.A))).ToString();
        }
        else if (key is >= Key.D0 and <= Key.D9)
        {
            keysym = (uint)('0' + (key - Key.D0));
            label = (key - Key.D0).ToString();
        }
        else if (key is >= Key.F1 and <= Key.F24)
        {
            keysym = 0xFFBE + (uint)(key - Key.F1);
            label = "F" + (key - Key.F1 + 1);
        }
        else if (key is >= Key.NumPad0 and <= Key.NumPad9)
        {
            keysym = 0xFFB0 + (uint)(key - Key.NumPad0);
            label = "Num " + (key - Key.NumPad0);
        }
        else
        {
            (keysym, label) = key switch
            {
                Key.Space => (0x20u, "Espacio"),
                Key.Left => (0xFF51u, "Izquierda"),
                Key.Up => (0xFF52u, "Arriba"),
                Key.Right => (0xFF53u, "Derecha"),
                Key.Down => (0xFF54u, "Abajo"),
                Key.Home => (0xFF50u, "Inicio"),
                Key.End => (0xFF57u, "Fin"),
                Key.PageUp => (0xFF55u, "RePág"),
                Key.PageDown => (0xFF56u, "AvPág"),
                Key.Insert => (0xFF63u, "Insert"),
                Key.Delete => (0xFFFFu, "Supr"),
                Key.Pause => (0xFF13u, "Pausa"),
                Key.Multiply => (0xFFAAu, "Num *"),
                Key.Add => (0xFFABu, "Num +"),
                Key.Subtract => (0xFFADu, "Num -"),
                Key.Decimal => (0xFFAEu, "Num ."),
                Key.Divide => (0xFFAFu, "Num /"),
                Key.OemComma => (0x2Cu, ","),
                Key.OemPeriod => (0x2Eu, "."),
                Key.OemMinus => (0x2Du, "-"),
                _ => (0u, ""),
            };
        }
        return keysym != 0;
    }

    public static string Describe(uint mods, string label)
    {
        var text = "";
        if ((mods & GlobalHotkeys.ModCtrl) != 0) text += "Ctrl + ";
        if ((mods & GlobalHotkeys.ModAlt) != 0) text += "Alt + ";
        if ((mods & GlobalHotkeys.ModShift) != 0) text += "Shift + ";
        return text + label;
    }
}
