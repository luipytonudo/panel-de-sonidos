using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace PanelSonidos;

static class Palette
{
    public static readonly IBrush Back = Brush.Parse("#1e1f24");
    public static readonly IBrush Panel = Brush.Parse("#26282e");
    public static readonly IBrush Tile = Brush.Parse("#30333b");
    public static readonly IBrush Accent = Brush.Parse("#4f8cff");
    public static readonly IBrush Playing = Brush.Parse("#3ccf7a");
    public static readonly IBrush Text = Brush.Parse("#eceef2");
    public static readonly IBrush Muted = Brush.Parse("#a0a5b0");
    public static readonly IBrush Error = Brush.Parse("#ff7b72");
    public static readonly IBrush Banner = Brush.Parse("#2d3a52");
}

public sealed class KeyChoice
{
    /// <summary>Keysym 0 significa "quitar la tecla".</summary>
    public uint Mods, Keysym;
    public string Label = "";
}

/// <summary>Espera a que el usuario pulse una tecla (con o sin Ctrl/Alt/Shift).</summary>
public sealed class KeyCaptureWindow : Window
{
    readonly TextBlock message;

    public KeyCaptureWindow(string title, bool allowRemove)
    {
        Title = title;
        Icon = App.LoadIcon();
        Width = 400;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.Back;

        message = new TextBlock
        {
            Text = "Pulsa la tecla que quieras.\nPuedes combinarla con Ctrl, Alt o Shift.\nEsc para cancelar.",
            Foreground = Palette.Text,
            FontSize = 15,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };

        // Los botones no reciben el foco para que Espacio o Enter lleguen como teclas a asignar.
        var remove = new Button { Content = "Quitar tecla", Focusable = false, IsVisible = allowRemove };
        remove.Click += (_, _) => Close(new KeyChoice());
        var cancel = new Button { Content = "Cancelar", Focusable = false };
        cancel.Click += (_, _) => Close(null);

        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 20,
            Children =
            {
                message,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Spacing = 10,
                    Children = { remove, cancel },
                },
            },
        };

        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
    }

    static bool IsModifier(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
        or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    void OnKey(object? sender, KeyEventArgs e)
    {
        e.Handled = true;
        if (e.Key == Key.Escape)
        {
            Close(null);
            return;
        }
        if (IsModifier(e.Key)) return;

        if (!KeyMap.TryGet(e.Key, out var keysym, out var label))
        {
            message.Text = "Esa tecla no se puede usar. Prueba otra.";
            return;
        }

        uint mods = 0;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) mods |= GlobalHotkeys.ModCtrl;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) mods |= GlobalHotkeys.ModAlt;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) mods |= GlobalHotkeys.ModShift;
        Close(new KeyChoice { Mods = mods, Keysym = keysym, Label = label });
    }
}

public sealed class RenameWindow : Window
{
    public RenameWindow(string current)
    {
        Title = "Renombrar";
        Icon = App.LoadIcon();
        Width = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Palette.Back;

        var box = new TextBox { Text = current };
        var ok = new Button { Content = "Guardar", IsDefault = true };
        ok.Click += (_, _) => Close(box.Text?.Trim());
        var cancel = new Button { Content = "Cancelar", IsCancel = true };
        cancel.Click += (_, _) => Close(null);

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 14,
            Children =
            {
                box,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10,
                    Children = { ok, cancel },
                },
            },
        };

        Opened += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
    }
}
