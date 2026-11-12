using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace PanelSonidos;

public sealed class MainWindow : Window
{
    sealed record Choice(string? Id, string Label)
    {
        public override string ToString() => Label;
    }

    readonly AppConfig config = AppConfig.Load();
    readonly AudioEngine engine = new();
    readonly GlobalHotkeys hotkeys = new();
    readonly SemaphoreSlim audioGate = new(1, 1);
    readonly Dictionary<string, float[]> cache = new();
    readonly Dictionary<string, string> loadErrors = new();
    readonly HashSet<string> loading = new();
    readonly Dictionary<string, Border> tiles = new();

    readonly WrapPanel grid = new() { Margin = new Thickness(10) };
    readonly TextBlock status = new() { Foreground = Palette.Muted, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock bannerText = new() { Foreground = Palette.Text, TextWrapping = TextWrapping.Wrap };
    readonly Border banner;
    readonly Button stopKeyButton = new();
    readonly ComboBox monitorBox = new() { MinWidth = 260 };
    readonly ComboBox callBox = new() { MinWidth = 260 };
    readonly ComboBox micBox = new() { MinWidth = 260 };
    readonly Slider monitorSlider = new() { Minimum = 0, Maximum = 100, Width = 160 };
    readonly Slider callSlider = new() { Minimum = 0, Maximum = 100, Width = 160 };
    readonly CheckBox micCheck = new() { Content = "Mezclar mi voz" };
    readonly CheckBox autostartCheck = new() { Content = "Iniciar con el sistema" };
    bool fillingUi;
    bool exiting;

    public MainWindow()
    {
        Title = "Panel de sonidos";
        Icon = App.LoadIcon();
        Width = 900;
        Height = 640;
        MinWidth = 560;
        MinHeight = 420;
        Background = Palette.Back;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var add = new Button { Content = "+ Agregar sonidos", Background = Palette.Accent, Foreground = Brushes.White };
        add.Click += async (_, _) => await PickFiles();
        var stopAll = new Button { Content = "Detener todo" };
        stopAll.Click += (_, _) => StopAll();
        stopKeyButton.Click += async (_, _) => await AssignStopKey();

        var header = new DockPanel { Margin = new Thickness(16, 14, 16, 6), LastChildFill = false };
        header.Children.Add(new TextBlock
        {
            Text = "Panel de sonidos",
            FontSize = 22,
            FontWeight = FontWeight.Bold,
            Foreground = Palette.Text,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(add);
        actions.Children.Add(stopAll);
        actions.Children.Add(stopKeyButton);
        DockPanel.SetDock(actions, Dock.Right);
        header.Children.Add(actions);

        banner = new Border
        {
            Background = Palette.Banner,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8),
            Margin = new Thickness(16, 4, 16, 0),
            Child = bannerText,
            IsVisible = false,
        };

        var footer = BuildFooter();
        var scroll = new ScrollViewer { Content = grid };

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(banner, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(banner);
        root.Children.Add(footer);
        root.Children.Add(scroll);
        Content = root;

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = DragDropEffects.Copy);
        AddHandler(DragDrop.DropEvent, OnDrop);

        engine.DeviceFailed += message => Dispatcher.UIThread.Post(() => status.Text = message);
        new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) => RefreshPlaying()).Start();

        FillDevices();
        UpdateStopKeyButton();
        RebuildTiles();
        foreach (var sound in config.Sounds) Load(sound);
        RegisterHotkeys();
        _ = ApplyAudio();
    }

    Control BuildFooter()
    {
        fillingUi = true;
        monitorSlider.Value = config.MonitorVolume;
        callSlider.Value = config.CallVolume;
        micCheck.IsChecked = config.MicEnabled;
        autostartCheck.IsChecked = File.Exists(AutostartFile);
        fillingUi = false;

        monitorBox.SelectionChanged += (_, _) =>
        {
            if (fillingUi || monitorBox.SelectedItem is not Choice c) return;
            config.MonitorDevice = c.Id;
            SaveAndApply();
        };
        callBox.SelectionChanged += (_, _) =>
        {
            if (fillingUi || callBox.SelectedItem is not Choice c) return;
            config.CableDevice = c.Id;
            SaveAndApply();
        };
        micBox.SelectionChanged += (_, _) =>
        {
            if (fillingUi || micBox.SelectedItem is not Choice c) return;
            config.MicDevice = c.Id;
            SaveAndApply();
        };
        micCheck.IsCheckedChanged += (_, _) =>
        {
            if (fillingUi) return;
            config.MicEnabled = micCheck.IsChecked == true;
            SaveAndApply();
        };
        autostartCheck.IsCheckedChanged += (_, _) =>
        {
            if (!fillingUi) SetAutostart(autostartCheck.IsChecked == true);
        };
        monitorSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty || fillingUi) return;
            config.MonitorVolume = (int)monitorSlider.Value;
            engine.SetVolumes(config.MonitorVolume / 100f, config.CallVolume / 100f);
            config.Save();
        };
        callSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty || fillingUi) return;
            config.CallVolume = (int)callSlider.Value;
            engine.SetVolumes(config.MonitorVolume / 100f, config.CallVolume / 100f);
            config.Save();
        };

        var refresh = new Button { Content = "Actualizar audio" };
        refresh.Click += (_, _) =>
        {
            FillDevices();
            _ = ApplyAudio();
        };

        static TextBlock Label(string text) => new()
        {
            Text = text,
            Foreground = Palette.Muted,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };

        var table = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
            RowSpacing = 8,
        };
        void Row(int row, string label, Control box, Control? extra)
        {
            var l = Label(label);
            Grid.SetRow(l, row);
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);
            table.Children.Add(l);
            table.Children.Add(box);
            if (extra == null) return;
            extra.Margin = new Thickness(16, 0, 0, 0);
            Grid.SetRow(extra, row);
            Grid.SetColumn(extra, 2);
            table.Children.Add(extra);
        }
        Row(0, "Yo escucho en", monitorBox, Volume(monitorSlider));
        Row(1, "Enviar a la llamada", callBox, Volume(callSlider));
        Row(2, "Mi micrófono", micBox, micCheck);

        var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        options.Children.Add(autostartCheck);
        options.Children.Add(refresh);
        // En algunos escritorios (GNOME sin extension) no hay bandeja: hace falta otra forma de salir.
        var quit = new Button { Content = "Salir del panel" };
        quit.Click += (_, _) => App.Current?.Quit();
        options.Children.Add(quit);

        return new Border
        {
            Background = Palette.Panel,
            Padding = new Thickness(16, 12),
            Child = new StackPanel { Spacing = 10, Children = { table, options, status } },
        };

        static Control Volume(Slider slider) => new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { new TextBlock { Text = "Volumen", Foreground = Palette.Muted, VerticalAlignment = VerticalAlignment.Center }, slider },
        };
    }

    // ---------- Dispositivos ----------

    void FillDevices()
    {
        var sinks = PulseDevices.SafeList("sinks").Where(d => d.Id != PulseDevices.VirtualSink).ToList();
        var sources = PulseDevices.SafeList("sources")
            .Where(d => !d.Id.EndsWith(".monitor") && d.Id != PulseDevices.VirtualMic).ToList();

        fillingUi = true;
        Fill(monitorBox, new[] { new Choice(null, "Salida predeterminada del sistema") }
            .Concat(sinks.Select(d => new Choice(d.Id, d.Name))), config.MonitorDevice);
        Fill(callBox, new[]
            {
                new Choice(null, "Micrófono del panel (recomendado)"),
                new Choice("", "No enviar a la llamada"),
            }.Concat(sinks.Select(d => new Choice(d.Id, "Salida: " + d.Name))), config.CableDevice);
        Fill(micBox, new[] { new Choice(null, "Micrófono predeterminado del sistema") }
            .Concat(sources.Select(d => new Choice(d.Id, d.Name))), config.MicDevice);
        fillingUi = false;
    }

    static void Fill(ComboBox box, IEnumerable<Choice> items, string? selected)
    {
        var list = items.ToList();
        // Si el dispositivo guardado no esta conectado ahora, se muestra igual para no perder la eleccion.
        if (list.All(c => c.Id != selected)) list.Add(new Choice(selected, "(desconectado) " + selected));
        box.ItemsSource = list;
        box.SelectedItem = list.First(c => c.Id == selected);
    }

    void SaveAndApply()
    {
        config.Save();
        _ = ApplyAudio();
    }

    async Task ApplyAudio()
    {
        await audioGate.WaitAsync();
        try
        {
            status.Text = "Preparando el audio…";
            var monitor = config.MonitorDevice;
            var callSetting = config.CableDevice;
            var mic = config.MicDevice;
            var useMic = config.MicEnabled;
            float monitorVolume = config.MonitorVolume / 100f, callVolume = config.CallVolume / 100f;

            var (problems, usesVirtual) = await Task.Run(() =>
            {
                var list = new List<string>();
                string? call = callSetting == "" ? null : callSetting;
                bool virtualMic = false;
                if (callSetting == null)
                {
                    var error = PulseDevices.EnsureVirtualMic();
                    if (error == null)
                    {
                        call = PulseDevices.VirtualSink;
                        virtualMic = true;
                    }
                    else list.Add("No se pudo crear el micrófono del panel: " + error);
                }

                if (useMic && mic == null && PulseDevices.DefaultSource() == PulseDevices.VirtualMic)
                {
                    list.Add("Tu micrófono predeterminado es el del panel; elige tu micrófono real en «Mi micrófono».");
                    useMic = false;
                }

                var errors = engine.Configure(monitor, call, mic, useMic, monitorVolume, callVolume);
                if (errors != "") list.Add(errors);
                return (string.Join("  ", list), virtualMic);
            });

            FillDevices(); // el microfono virtual puede ser nuevo
            status.Foreground = problems == "" ? Palette.Muted : Palette.Error;
            status.Text = problems == "" ? "Listo. Clic en un sonido para escucharlo; ⋯ para más opciones." : problems;
            UpdateBanner(usesVirtual);
        }
        finally
        {
            audioGate.Release();
        }
    }

    void UpdateBanner(bool usesVirtual)
    {
        var lines = new List<string>();
        if (usesVirtual && engine.HasCable)
            lines.Add($"En Discord: Ajustes → Voz y video → Dispositivo de entrada → «{PulseDevices.VirtualMicLabel}». "
                + (engine.MicActive ? "Tu voz ya va mezclada con los sonidos." : "Activa «Mezclar mi voz» para que también te escuchen."));
        if (hotkeys.Error != null)
            lines.Add("Las teclas globales no funcionan: " + hotkeys.Error + ". Puedes usar los sonidos con clic.");
        else if (GlobalHotkeys.IsWayland)
            lines.Add("Estás en Wayland: las teclas solo se detectan mientras tienes enfocada una ventana X11 (Discord lo es). "
                + "Para que funcionen siempre, inicia sesión en «Xorg/X11».");
        bannerText.Text = string.Join("\n", lines);
        banner.IsVisible = lines.Count > 0;
    }

    // ---------- Sonidos ----------

    static string FullPath(SoundEntry sound) => Path.Combine(AppConfig.SoundsFolder, sound.File);

    void Load(SoundEntry sound)
    {
        if (!loading.Add(sound.Id)) return;
        loadErrors.Remove(sound.Id);
        var path = FullPath(sound);
        Task.Run(() => SoundDecoder.Decode(path)).ContinueWith(task => Dispatcher.UIThread.Post(() =>
        {
            loading.Remove(sound.Id);
            if (task.IsCompletedSuccessfully) cache[sound.Id] = task.Result;
            else loadErrors[sound.Id] = task.Exception?.GetBaseException() switch
            {
                FileNotFoundException or DirectoryNotFoundException => "no se encuentra el archivo",
                { } ex => ex.Message,
                null => "error desconocido",
            };
            RebuildTiles();
        }));
    }

    void Play(SoundEntry sound)
    {
        if (cache.TryGetValue(sound.Id, out var samples)) engine.Play(sound.Id, samples, sound.Volume);
        else if (loading.Contains(sound.Id)) status.Text = $"«{sound.Name}» todavía se está cargando.";
        else status.Text = $"«{sound.Name}» no se pudo cargar.";
    }

    public void StopAll() => engine.StopAll();

    async Task PickFiles()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Elige sonidos",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Audio (mp3, wav, ogg, opus)")
                {
                    Patterns = SoundDecoder.Extensions.Select(e => "*" + e).ToArray(),
                },
            },
        });
        AddFiles(files.Select(f => f.TryGetLocalPath()).OfType<string>());
    }

    void OnDrop(object? sender, DragEventArgs e)
    {
#pragma warning disable CS0618 // la API nueva de arrastrar no existe en todas las versiones 11.x
        var files = e.Data.GetFiles();
#pragma warning restore CS0618
        if (files != null) AddFiles(files.Select(f => f.TryGetLocalPath()).OfType<string>());
    }

    void AddFiles(IEnumerable<string> paths)
    {
        int added = 0, skipped = 0;
        foreach (var path in paths)
        {
            if (!SoundDecoder.Extensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
            {
                skipped++;
                continue;
            }
            try
            {
                // Se guarda una copia para que el sonido siga funcionando aunque se borre el original.
                Directory.CreateDirectory(AppConfig.SoundsFolder);
                var name = Path.GetFileNameWithoutExtension(path);
                var extension = Path.GetExtension(path);
                var file = name + extension;
                for (int i = 2; File.Exists(Path.Combine(AppConfig.SoundsFolder, file)); i++) file = $"{name} ({i}){extension}";
                File.Copy(path, Path.Combine(AppConfig.SoundsFolder, file));

                var sound = new SoundEntry { Name = name, File = file };
                config.Sounds.Add(sound);
                Load(sound);
                added++;
            }
            catch (Exception ex)
            {
                status.Text = $"No se pudo copiar {Path.GetFileName(path)}: {ex.Message}";
            }
        }

        if (added > 0)
        {
            config.Save();
            RebuildTiles();
            status.Foreground = Palette.Muted;
            status.Text = added == 1 ? "Sonido agregado." : $"{added} sonidos agregados.";
        }
        if (skipped > 0) status.Text += $" {skipped} archivo(s) ignorados: solo mp3, wav, ogg u opus.";
    }

    void Remove(SoundEntry sound)
    {
        engine.StopAll();
        config.Sounds.Remove(sound);
        cache.Remove(sound.Id);
        config.Save();
        if (config.Sounds.All(s => s.File != sound.File))
        {
            try { File.Delete(FullPath(sound)); } catch { }
        }
        RebuildTiles();
        RegisterHotkeys();
    }

    async Task Rename(SoundEntry sound)
    {
        var name = await new RenameWindow(sound.Name).ShowDialog<string?>(this);
        if (string.IsNullOrWhiteSpace(name)) return;
        sound.Name = name;
        config.Save();
        RebuildTiles();
    }

    // ---------- Teclas ----------

    async Task<KeyChoice?> CaptureKey(string title, bool allowRemove)
    {
        // Mientras la ventana espera la tecla, las teclas globales se sueltan; si no, X11 se las quedaria.
        hotkeys.Set(Array.Empty<(uint, uint, Action)>());
        try
        {
            return await new KeyCaptureWindow(title, allowRemove).ShowDialog<KeyChoice?>(this);
        }
        finally
        {
            RegisterHotkeys();
        }
    }

    async Task AssignKey(SoundEntry sound)
    {
        var choice = await CaptureKey($"Tecla para «{sound.Name}»", sound.Key != 0);
        if (choice == null) return;
        if (choice.Keysym != 0) FreeKey(choice, sound);
        sound.Mods = choice.Mods;
        sound.Key = choice.Keysym;
        sound.KeyLabel = choice.Label;
        config.Save();
        RebuildTiles();
        RegisterHotkeys();
    }

    async Task AssignStopKey()
    {
        var choice = await CaptureKey("Tecla para detener todos los sonidos", config.StopKey != 0);
        if (choice == null) return;
        if (choice.Keysym != 0) FreeKey(choice, null);
        config.StopMods = choice.Mods;
        config.StopKey = choice.Keysym;
        config.StopKeyLabel = choice.Label;
        config.Save();
        UpdateStopKeyButton();
        RebuildTiles();
        RegisterHotkeys();
    }

    /// <summary>Una tecla solo puede hacer una cosa: se quita de donde estuviera antes.</summary>
    void FreeKey(KeyChoice choice, SoundEntry? newOwner)
    {
        var text = KeyMap.Describe(choice.Mods, choice.Label);
        foreach (var other in config.Sounds.Where(s => s != newOwner && s.Key == choice.Keysym && s.Mods == choice.Mods))
        {
            other.Key = 0;
            status.Text = $"{text} se quitó de «{other.Name}».";
        }
        if (newOwner != null && config.StopKey == choice.Keysym && config.StopMods == choice.Mods)
        {
            config.StopKey = 0;
            UpdateStopKeyButton();
            status.Text = $"{text} ya no detiene los sonidos.";
        }
    }

    void RegisterHotkeys()
    {
        var keys = new List<(uint, uint, Action)>();
        var names = new List<string>();
        foreach (var sound in config.Sounds.Where(s => s.Key != 0))
        {
            var target = sound;
            keys.Add((sound.Mods, sound.Key, () => Play(target)));
            names.Add(KeyMap.Describe(sound.Mods, sound.KeyLabel));
        }
        if (config.StopKey != 0)
        {
            keys.Add((config.StopMods, config.StopKey, engine.StopAll));
            names.Add(KeyMap.Describe(config.StopMods, config.StopKeyLabel));
        }
        if (keys.Count == 0 || hotkeys.Error != null) return;

        var ok = hotkeys.Set(keys);
        var taken = names.Where((_, i) => !ok[i]).ToList();
        if (taken.Count > 0)
        {
            status.Foreground = Palette.Error;
            status.Text = "Otro programa ya usa estas teclas: " + string.Join(", ", taken) + ". Elige otras.";
        }
    }

    void UpdateStopKeyButton() => stopKeyButton.Content = config.StopKey != 0
        ? "Detener con: " + KeyMap.Describe(config.StopMods, config.StopKeyLabel)
        : "Tecla para detener…";

    // ---------- Cuadros de sonido ----------

    void RebuildTiles()
    {
        grid.Children.Clear();
        tiles.Clear();
        if (config.Sounds.Count == 0)
        {
            grid.Children.Add(new TextBlock
            {
                Text = "Todavía no hay sonidos.\nPulsa «+ Agregar sonidos» o arrastra archivos mp3, wav, ogg u opus aquí.",
                Foreground = Palette.Muted,
                FontSize = 15,
                Margin = new Thickness(10, 30),
            });
            return;
        }
        foreach (var sound in config.Sounds) grid.Children.Add(MakeTile(sound));
    }

    Control MakeTile(SoundEntry sound)
    {
        var name = new TextBlock
        {
            Text = sound.Name,
            Foreground = Palette.Text,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 2,
        };

        loadErrors.TryGetValue(sound.Id, out var error);
        string? note = loading.Contains(sound.Id) ? "Cargando…"
            : error != null ? "No se pudo cargar: " + error
            : null;

        var keyButton = new Button
        {
            Content = sound.Key != 0 ? KeyMap.Describe(sound.Mods, sound.KeyLabel) : "Asignar tecla",
            FontSize = 12,
            Padding = new Thickness(8, 3),
        };
        keyButton.Click += async (_, _) => await AssignKey(sound);

        var more = new Button { Content = "⋯", FontSize = 14, Padding = new Thickness(10, 1), Flyout = BuildMenu(sound) };

        var bottom = new DockPanel();
        DockPanel.SetDock(more, Dock.Right);
        bottom.Children.Add(more);
        bottom.Children.Add(keyButton);
        keyButton.HorizontalAlignment = HorizontalAlignment.Left;

        var body = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        body.Children.Add(bottom);
        if (note != null)
        {
            var noteText = new TextBlock
            {
                Text = note,
                FontSize = 11,
                Foreground = error != null ? Palette.Error : Palette.Muted,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            ToolTip.SetTip(noteText, note);
            DockPanel.SetDock(noteText, Dock.Bottom);
            body.Children.Add(noteText);
        }
        body.Children.Add(name);

        var tile = new Border
        {
            Width = 190,
            Height = 112,
            Margin = new Thickness(6),
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(8),
            Background = Palette.Tile,
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = body,
        };
        ToolTip.SetTip(tile, sound.Name);
        tile.Tapped += (_, e) =>
        {
            if ((e.Source as Visual)?.FindAncestorOfType<Button>(true) != null) return; // fue en un boton del cuadro
            Play(sound);
        };
        tiles[sound.Id] = tile;
        return tile;
    }

    MenuFlyout BuildMenu(SoundEntry sound)
    {
        MenuItem Item(string text, Action action)
        {
            var item = new MenuItem { Header = text };
            item.Click += (_, _) => action();
            return item;
        }

        var volume = new MenuItem { Header = "Volumen de este sonido" };
        foreach (var percent in new[] { 25, 50, 75, 100, 125, 150 })
        {
            var item = Item((Math.Abs(sound.Volume * 100 - percent) < 1 ? "● " : "    ") + percent + " %", () =>
            {
                sound.Volume = percent / 100f;
                config.Save();
                RebuildTiles();
            });
            volume.Items.Add(item);
        }

        var menu = new MenuFlyout();
        menu.Items.Add(Item("Reproducir", () => Play(sound)));
        menu.Items.Add(Item("Cambiar tecla…", () => _ = AssignKey(sound)));
        if (sound.Key != 0)
            menu.Items.Add(Item("Quitar tecla", () =>
            {
                sound.Key = 0;
                config.Save();
                RebuildTiles();
                RegisterHotkeys();
            }));
        menu.Items.Add(Item("Renombrar…", () => _ = Rename(sound)));
        menu.Items.Add(volume);
        if (loadErrors.ContainsKey(sound.Id)) menu.Items.Add(Item("Reintentar cargar", () => { Load(sound); RebuildTiles(); }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Eliminar", () => Remove(sound)));
        return menu;
    }

    void RefreshPlaying()
    {
        if (!IsVisible) return;
        foreach (var (id, tile) in tiles)
            tile.BorderBrush = engine.IsPlaying(id) ? Palette.Playing : Brushes.Transparent;
    }

    // ---------- Inicio automatico ----------

    static string AutostartFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "autostart", "panel-sonidos.desktop");

    void SetAutostart(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                File.Delete(AutostartFile);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(AutostartFile)!);
            File.WriteAllText(AutostartFile,
                "[Desktop Entry]\n" +
                "Type=Application\n" +
                "Name=Panel de sonidos\n" +
                $"Exec=\"{Environment.ProcessPath}\" --background\n" +
                "X-GNOME-Autostart-enabled=true\n");
        }
        catch (Exception ex)
        {
            status.Text = "No se pudo cambiar el inicio automático: " + ex.Message;
        }
    }

    // ---------- Cerrar ----------

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // La X solo esconde la ventana: el panel sigue escuchando las teclas. Para salir, el menu de la bandeja.
        if (!exiting)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    public void Shutdown()
    {
        exiting = true;
        hotkeys.Dispose();
        engine.Dispose();
        Close();
    }
}
