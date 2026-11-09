using System.Diagnostics;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace PanelSonidos;

sealed class MainForm : Form
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "PanelSonidos";
    const string CableUrl = "https://vb-audio.com/Cable/";
    const string FileFilter = "Audio|*.mp3;*.wav;*.ogg;*.opus;*.m4a;*.aac;*.wma;*.flac|Todos los archivos|*.*";
    static readonly string[] AudioExtensions = { ".mp3", ".wav", ".ogg", ".opus", ".m4a", ".aac", ".wma", ".flac" };

    readonly AppConfig config = AppConfig.Load();
    readonly AudioEngine engine = new();
    readonly HotkeyManager hotkeys = new();
    readonly Dictionary<string, float[]> loaded = new();
    readonly HashSet<string> failed = new();
    readonly List<SoundTile> tiles = new();
    readonly System.Windows.Forms.Timer playingTimer = new() { Interval = 120 };

    readonly FlowLayoutPanel grid;
    readonly Label emptyHint;
    readonly Panel banner;
    readonly Label bannerText;
    readonly Button bannerButton;
    readonly Button stopKeyButton;
    readonly ComboBox monitorBox, cableBox, micBox;
    readonly TrackBar monitorSlider, callSlider;
    readonly CheckBox micCheck, autostartCheck;
    readonly Label status;

    bool loadingDevices;
    bool exiting;

    public bool HasSounds => config.Sounds.Count > 0;

    public MainForm()
    {
        Text = "Panel de sonidos";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        ClientSize = new Size(940, 660);
        MinimumSize = new Size(760, 520);
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;

        // Encabezado
        var header = new Panel { Dock = DockStyle.Top, Height = 62, BackColor = Theme.Panel };
        var title = new Label { Text = "Panel de sonidos", AutoSize = true, Left = 18, Top = 15, Font = new Font("Segoe UI Semibold", 16f) };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 13, 12, 0) };
        var addButton = Theme.Button("+ Agregar sonidos", primary: true);
        var stopButton = Theme.Button("Detener todo");
        stopKeyButton = Theme.Button("");
        addButton.Click += (_, _) => PickFiles();
        stopButton.Click += (_, _) => StopAll();
        stopKeyButton.Click += (_, _) => AssignStopKey();
        actions.Controls.AddRange(new Control[] { addButton, stopButton, stopKeyButton });
        header.Controls.Add(actions);
        header.Controls.Add(title);

        // Aviso del microfono virtual
        banner = new Panel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(16, 0, 10, 0) };
        bannerText = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Background, Font = new Font("Segoe UI Semibold", 9.5f) };
        bannerButton = Theme.Button("Descargar VB-CABLE");
        bannerButton.Dock = DockStyle.Right;
        bannerButton.Margin = new Padding(0);
        bannerButton.BackColor = Theme.Background;
        bannerButton.ForeColor = Theme.Text;
        bannerButton.Click += (_, _) => Process.Start(new ProcessStartInfo(CableUrl) { UseShellExecute = true });
        banner.Controls.Add(bannerText);
        banner.Controls.Add(bannerButton);

        // Cuadricula
        grid = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(12), BackColor = Theme.Background, AllowDrop = true };
        emptyHint = new Label
        {
            Text = "Arrastra aquí tus audios o pulsa  + Agregar sonidos",
            AutoSize = true,
            ForeColor = Theme.Muted,
            Font = new Font("Segoe UI", 12f),
            Margin = new Padding(12, 40, 0, 0),
        };

        // Ajustes
        var footer = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 4, BackColor = Theme.Panel, Padding = new Padding(14, 10, 14, 8) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));

        monitorBox = Combo();
        cableBox = Combo();
        micBox = Combo();
        monitorSlider = Slider(config.MonitorVolume);
        callSlider = Slider(config.CallVolume);

        micCheck = new CheckBox { Text = "Mezclar mi voz", Checked = config.MicEnabled, AutoSize = true, Margin = new Padding(0, 7, 14, 0) };
        autostartCheck = new CheckBox { Text = "Iniciar con Windows", Checked = IsAutostartEnabled(), AutoSize = true, Margin = new Padding(0, 7, 14, 0) };
        var refresh = Theme.Button("Actualizar audio");
        refresh.Margin = new Padding(0, 2, 0, 2);
        refresh.Click += (_, _) => { LoadDevices(); ApplyAudio(); };

        var options = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
        options.Controls.AddRange(new Control[] { micCheck, autostartCheck, refresh });

        status = new Label { AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(3, 6, 3, 0) };

        footer.Controls.Add(Caption("Escuchar en"), 0, 0);
        footer.Controls.Add(monitorBox, 1, 0);
        footer.Controls.Add(Caption("Volumen para mí"), 2, 0);
        footer.Controls.Add(monitorSlider, 3, 0);
        footer.Controls.Add(Caption("Enviar a la llamada"), 0, 1);
        footer.Controls.Add(cableBox, 1, 1);
        footer.Controls.Add(Caption("Volumen en llamada"), 2, 1);
        footer.Controls.Add(callSlider, 3, 1);
        footer.Controls.Add(Caption("Mi micrófono"), 0, 2);
        footer.Controls.Add(micBox, 1, 2);
        footer.Controls.Add(options, 2, 2);
        footer.SetColumnSpan(options, 2);
        footer.Controls.Add(status, 0, 3);
        footer.SetColumnSpan(status, 4);

        Controls.Add(grid);
        Controls.Add(banner);
        Controls.Add(header);
        Controls.Add(footer);

        // Eventos
        monitorBox.SelectedIndexChanged += (_, _) => DeviceChoiceChanged();
        cableBox.SelectedIndexChanged += (_, _) => DeviceChoiceChanged();
        micBox.SelectedIndexChanged += (_, _) => DeviceChoiceChanged();
        micCheck.CheckedChanged += (_, _) => { config.MicEnabled = micCheck.Checked; SaveConfig(); ApplyAudio(); };
        monitorSlider.ValueChanged += (_, _) => VolumeChanged();
        callSlider.ValueChanged += (_, _) => VolumeChanged();
        autostartCheck.CheckedChanged += (_, _) => SetAutostart(autostartCheck.Checked);

        DragEnter += OnFileDragEnter;
        DragDrop += OnFileDrop;
        grid.DragEnter += OnFileDragEnter;
        grid.DragDrop += OnFileDrop;

        playingTimer.Tick += (_, _) => RefreshPlaying();
        playingTimer.Start();
        engine.DeviceFailed += message => BeginInvoke(() => ShowStatus(message, true));
    }

    static Label Caption(string text) => new() { Text = text, AutoSize = true, ForeColor = Theme.Muted, Anchor = AnchorStyles.Left, Margin = new Padding(3, 3, 12, 3) };

    static ComboBox Combo() => new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        FlatStyle = FlatStyle.Flat,
        BackColor = Theme.Tile,
        ForeColor = Theme.Text,
        Dock = DockStyle.Fill,
        Margin = new Padding(3, 3, 22, 3),
    };

    static TrackBar Slider(int value) => new()
    {
        Minimum = 0,
        Maximum = 100,
        TickStyle = TickStyle.None,
        Value = Math.Clamp(value, 0, 100),
        AutoSize = false,
        Height = 28,
        Dock = DockStyle.Fill,
        BackColor = Theme.Panel,
    };

    public void Initialize()
    {
        LoadDevices();
        ApplyAudio();
        RebuildTiles();
        RegisterHotkeys();
        UpdateStopKeyText();
        _ = LoadSoundsAsync();
    }

    // ---------- Audio ----------

    void LoadDevices()
    {
        loadingDevices = true;
        try
        {
            var outputs = AudioEngine.ListDevices(DataFlow.Render);
            var inputs = AudioEngine.ListDevices(DataFlow.Capture).Where(d => !AudioEngine.IsCableOutput(d.Name)).ToList();

            Fill(monitorBox, new AudioDevice("", "Predeterminado de Windows"), outputs.Where(d => !AudioEngine.IsCableInput(d.Name)), config.MonitorDeviceId ?? "");
            Fill(micBox, new AudioDevice("", "Predeterminado de Windows"), inputs, config.MicDeviceId ?? "");

            var cable = config.CableDeviceId;
            if (cable == null || (cable != "" && outputs.All(d => d.Id != cable)))
                cable = (outputs.FirstOrDefault(d => d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase))
                    ?? outputs.FirstOrDefault(d => AudioEngine.IsCableInput(d.Name)))?.Id ?? "";
            Fill(cableBox, new AudioDevice("", "Ninguno (solo lo escucho yo)"), outputs, cable);
        }
        finally
        {
            loadingDevices = false;
        }
    }

    static void Fill(ComboBox box, AudioDevice first, IEnumerable<AudioDevice> devices, string selectedId)
    {
        box.Items.Clear();
        box.Items.Add(first);
        foreach (var device in devices) box.Items.Add(device);
        box.SelectedItem = box.Items.Cast<AudioDevice>().FirstOrDefault(d => d.Id == selectedId) ?? first;
    }

    static string SelectedId(ComboBox box) => (box.SelectedItem as AudioDevice)?.Id ?? "";

    void DeviceChoiceChanged()
    {
        if (loadingDevices) return;
        config.MonitorDeviceId = SelectedId(monitorBox) is { Length: > 0 } monitor ? monitor : null;
        config.MicDeviceId = SelectedId(micBox) is { Length: > 0 } mic ? mic : null;
        config.CableDeviceId = SelectedId(cableBox);
        SaveConfig();
        ApplyAudio();
    }

    void ApplyAudio()
    {
        var cableId = SelectedId(cableBox);
        var errors = engine.Configure(
            SelectedId(monitorBox) is { Length: > 0 } monitor ? monitor : null,
            cableId.Length > 0 ? cableId : null,
            SelectedId(micBox) is { Length: > 0 } mic ? mic : null,
            config.MicEnabled,
            monitorSlider.Value / 100f,
            callSlider.Value / 100f);

        UpdateBanner();
        if (errors.Length > 0) ShowStatus(errors, true);
        else if (!engine.HasCable) ShowStatus("Los sonidos solo se escuchan en este PC.", false);
        else if (engine.MicActive) ShowStatus("Listo: tu voz y los sonidos salen por el micrófono virtual.", false);
        else ShowStatus("Listo: los sonidos salen por el micrófono virtual, sin tu voz.", false);
    }

    void UpdateBanner()
    {
        bool cableInstalled = cableBox.Items.Cast<AudioDevice>().Any(d => AudioEngine.IsCableInput(d.Name));
        bannerButton.Visible = !cableInstalled;
        if (!cableInstalled)
        {
            banner.BackColor = Theme.Accent;
            bannerText.Text = "Para sonar en llamadas falta instalar VB-CABLE (gratis). Después reinicia y pulsa Actualizar audio.";
        }
        else if (engine.HasCable)
        {
            banner.BackColor = Theme.Playing;
            bannerText.Text = "En Discord elige como dispositivo de entrada: CABLE Output. Desactiva allí la supresión de ruido.";
        }
        else
        {
            banner.BackColor = Theme.Accent;
            bannerText.Text = "VB-CABLE está instalado. Elige CABLE Input en \"Enviar a la llamada\" para que te escuchen.";
        }
    }

    void VolumeChanged()
    {
        config.MonitorVolume = monitorSlider.Value;
        config.CallVolume = callSlider.Value;
        engine.SetVolumes(monitorSlider.Value / 100f, callSlider.Value / 100f);
        SaveConfig();
    }

    void ShowStatus(string message, bool isError)
    {
        status.Text = message;
        status.ForeColor = isError ? Theme.Warn : Theme.Muted;
    }

    // ---------- Sonidos ----------

    static string PathOf(SoundEntry entry) => Path.Combine(AppConfig.SoundsFolder, entry.File);

    async Task LoadSoundsAsync()
    {
        foreach (var entry in config.Sounds.ToList())
        {
            try
            {
                loaded[entry.Id] = await Task.Run(() => AudioEngine.Decode(PathOf(entry)));
            }
            catch
            {
                failed.Add(entry.Id);
                var tile = tiles.FirstOrDefault(t => t.Entry == entry);
                if (tile != null) { tile.Broken = true; tile.Invalidate(); }
            }
        }
    }

    void PickFiles()
    {
        using var dialog = new OpenFileDialog { Filter = FileFilter, Multiselect = true, Title = "Elige tus sonidos" };
        if (dialog.ShowDialog(this) == DialogResult.OK) _ = AddFilesAsync(dialog.FileNames);
    }

    void OnFileDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy;
    }

    void OnFileDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] files)
            _ = AddFilesAsync(files.Where(f => AudioExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())).ToArray());
    }

    async Task AddFilesAsync(string[] paths)
    {
        var problems = new List<string>();
        Directory.CreateDirectory(AppConfig.SoundsFolder);

        foreach (var path in paths)
        {
            try
            {
                var samples = await Task.Run(() => AudioEngine.Decode(path));
                var entry = new SoundEntry { Name = Path.GetFileNameWithoutExtension(path) };
                // Se guarda una copia para que el sonido siga funcionando si el original se mueve.
                entry.File = entry.Id + Path.GetExtension(path);
                File.Copy(path, PathOf(entry));
                config.Sounds.Add(entry);
                loaded[entry.Id] = samples;
            }
            catch (Exception ex)
            {
                problems.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        SaveConfig();
        RebuildTiles();
        RegisterHotkeys();
        if (problems.Count > 0)
            MessageBox.Show(this, "No se pudieron agregar:\n\n" + string.Join("\n", problems), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    void Play(SoundEntry entry)
    {
        if (loaded.TryGetValue(entry.Id, out var samples)) engine.Play(entry.Id, samples, entry.Volume);
    }

    public void StopAll() => engine.StopAll();

    void RebuildTiles()
    {
        grid.SuspendLayout();
        foreach (var tile in tiles) tile.Dispose();
        tiles.Clear();
        grid.Controls.Clear();

        float scale = DeviceDpi / 96f;
        foreach (var entry in config.Sounds)
        {
            var tile = new SoundTile(entry, scale) { Broken = failed.Contains(entry.Id) };
            tile.PlayClicked += () => Play(entry);
            tile.KeyClicked += () => AssignKey(entry);
            tile.MenuClicked += () => ShowTileMenu(tile);
            tiles.Add(tile);
            grid.Controls.Add(tile);
        }
        if (tiles.Count == 0) grid.Controls.Add(emptyHint);
        grid.ResumeLayout();
    }

    void ShowTileMenu(SoundTile tile)
    {
        var entry = tile.Entry;
        var menu = new ContextMenuStrip();
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Items.Add("Reproducir", null, (_, _) => Play(entry));
        menu.Items.Add("Asignar tecla…", null, (_, _) => AssignKey(entry));
        menu.Items.Add("Renombrar…", null, (_, _) => Rename(entry));

        var volume = new ToolStripMenuItem("Volumen de este sonido");
        foreach (var percent in new[] { 25, 50, 75, 100, 150, 200 })
        {
            var item = new ToolStripMenuItem(percent + " %") { Checked = Math.Abs(entry.Volume * 100 - percent) < 1 };
            item.Click += (_, _) => { entry.Volume = percent / 100f; SaveConfig(); };
            volume.DropDownItems.Add(item);
        }
        menu.Items.Add(volume);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Eliminar", null, (_, _) => Remove(entry));
        menu.Show(Cursor.Position);
    }

    void Rename(SoundEntry entry)
    {
        using var dialog = new RenameForm(entry.Name);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Value.Length == 0) return;
        entry.Name = dialog.Value;
        SaveConfig();
        tiles.First(t => t.Entry == entry).Invalidate();
    }

    void Remove(SoundEntry entry)
    {
        var answer = MessageBox.Show(this, $"¿Eliminar \"{entry.Name}\" del panel?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;

        engine.StopAll();
        config.Sounds.Remove(entry);
        loaded.Remove(entry.Id);
        failed.Remove(entry.Id);
        try { File.Delete(PathOf(entry)); } catch (IOException) { }
        SaveConfig();
        RebuildTiles();
        RegisterHotkeys();
    }

    void RefreshPlaying()
    {
        if (!Visible) return;
        foreach (var tile in tiles)
        {
            bool playing = engine.IsPlaying(tile.Entry.Id);
            if (playing == tile.PlayingNow) continue;
            tile.PlayingNow = playing;
            tile.Invalidate();
        }
    }

    // ---------- Teclas ----------

    void RegisterHotkeys()
    {
        hotkeys.Clear();
        foreach (var tile in tiles)
        {
            var entry = tile.Entry;
            tile.HotkeyTaken = entry.Key != 0 && !hotkeys.Register(entry.Mods, entry.Key, () => Play(entry));
            tile.Invalidate();
        }

        if (config.StopKey != 0 && !hotkeys.Register(config.StopMods, config.StopKey, StopAll))
            ShowStatus("La tecla de detener ya la usa otro programa.", true);
    }

    bool CaptureKey(string label, out uint mods, out uint key)
    {
        mods = key = 0;
        // Sin esto, una tecla ya asignada dispararia su sonido en vez de llegar al dialogo.
        hotkeys.Clear();
        try
        {
            using var dialog = new KeyCaptureForm(label);
            if (dialog.ShowDialog(this) != DialogResult.OK) return false;
            mods = dialog.Mods;
            key = dialog.Key;
            return true;
        }
        finally
        {
            RegisterHotkeys();
        }
    }

    void AssignKey(SoundEntry entry)
    {
        if (!CaptureKey(entry.Name, out var mods, out var key)) return;
        if (!ReleaseIfUsed(mods, key, entry)) return;
        entry.Mods = mods;
        entry.Key = key;
        SaveConfig();
        RegisterHotkeys();
    }

    void AssignStopKey()
    {
        if (!CaptureKey("Detener todos los sonidos", out var mods, out var key)) return;
        if (!ReleaseIfUsed(mods, key, null)) return;
        config.StopMods = mods;
        config.StopKey = key;
        SaveConfig();
        RegisterHotkeys();
        UpdateStopKeyText();
    }

    /// <summary>Si la tecla ya esta en uso dentro del panel, pregunta antes de reasignarla.</summary>
    bool ReleaseIfUsed(uint mods, uint key, SoundEntry? target)
    {
        if (key == 0) return true;

        var other = config.Sounds.FirstOrDefault(s => s != target && s.Key == key && s.Mods == mods);
        bool usedByStop = target != null && config.StopKey == key && config.StopMods == mods;
        if (other == null && !usedByStop) return true;

        var owner = other?.Name ?? "Detener todo";
        var answer = MessageBox.Show(this, $"{KeyNames.Describe(mods, key)} ya está asignada a \"{owner}\". ¿Quieres moverla aquí?",
            Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return false;

        if (other != null) { other.Key = 0; other.Mods = 0; }
        if (usedByStop) { config.StopKey = 0; config.StopMods = 0; UpdateStopKeyText(); }
        return true;
    }

    void UpdateStopKeyText() =>
        stopKeyButton.Text = config.StopKey == 0 ? "Tecla para detener" : "Detener: " + KeyNames.Describe(config.StopMods, config.StopKey);

    // ---------- Ventana ----------

    static bool IsAutostartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) != null;
    }

    static void SetAutostart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\" --background");
        else key.DeleteValue(RunValue, false);
    }

    void SaveConfig()
    {
        try
        {
            config.Save();
        }
        catch (Exception ex)
        {
            ShowStatus("No se pudo guardar la configuración: " + ex.Message, true);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // La X solo esconde la ventana: las teclas siguen funcionando desde la bandeja.
        if (!exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    public void Shutdown()
    {
        exiting = true;
        playingTimer.Dispose();
        hotkeys.Dispose();
        engine.Dispose();
        Close();
    }
}
