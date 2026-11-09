namespace PanelSonidos;

/// <summary>Espera a que el usuario pulse la tecla o combinacion que quiere usar.</summary>
sealed class KeyCaptureForm : Form
{
    public uint Mods { get; private set; }
    public uint Key { get; private set; }

    readonly Label preview;

    public KeyCaptureForm(string soundName)
    {
        Text = "Asignar tecla";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 10f);
        ClientSize = new Size(420, 190);

        var title = new Label { Text = soundName, Dock = DockStyle.Top, Height = 40, TextAlign = ContentAlignment.BottomCenter, ForeColor = Theme.Muted, AutoEllipsis = true };
        preview = new Label { Text = "Pulsa una tecla o combinación", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI Semibold", 14f) };

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var cancel = Theme.Button("Cancelar");
        var clear = Theme.Button("Quitar tecla");
        cancel.TabStop = clear.TabStop = false;
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; };
        clear.Click += (_, _) => { Mods = 0; Key = 0; DialogResult = DialogResult.OK; };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(clear);

        Controls.Add(preview);
        Controls.Add(title);
        Controls.Add(buttons);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var code = keyData & Keys.KeyCode;
        if (code == Keys.Escape)
        {
            DialogResult = DialogResult.Cancel;
            return true;
        }

        if (code is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin or Keys.None)
            return true;

        Key = (uint)code;
        Mods = 0;
        if (keyData.HasFlag(Keys.Control)) Mods |= HotkeyManager.ModCtrl;
        if (keyData.HasFlag(Keys.Alt)) Mods |= HotkeyManager.ModAlt;
        if (keyData.HasFlag(Keys.Shift)) Mods |= HotkeyManager.ModShift;
        DialogResult = DialogResult.OK;
        return true;
    }
}

sealed class RenameForm : Form
{
    readonly TextBox box;

    public string Value => box.Text.Trim();

    public RenameForm(string current)
    {
        Text = "Renombrar sonido";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 10f);
        ClientSize = new Size(380, 112);

        box = new TextBox { Text = current, Left = 14, Top = 16, Width = 352, BackColor = Theme.Tile, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle };
        var ok = Theme.Button("Guardar");
        ok.Left = 266; ok.Top = 62; ok.Width = 100;
        ok.DialogResult = DialogResult.OK;
        Controls.Add(box);
        Controls.Add(ok);
        AcceptButton = ok;
    }
}

static class Theme
{
    public static readonly Color Background = Color.FromArgb(21, 23, 28);
    public static readonly Color Panel = Color.FromArgb(29, 32, 39);
    public static readonly Color Tile = Color.FromArgb(38, 42, 51);
    public static readonly Color TileHover = Color.FromArgb(47, 52, 63);
    public static readonly Color Chip = Color.FromArgb(21, 23, 28);
    public static readonly Color Accent = Color.FromArgb(242, 153, 74);
    public static readonly Color Playing = Color.FromArgb(46, 184, 134);
    public static readonly Color Warn = Color.FromArgb(240, 98, 98);
    public static readonly Color Text = Color.FromArgb(236, 238, 242);
    public static readonly Color Muted = Color.FromArgb(160, 167, 180);

    public static Button Button(string text, bool primary = false)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Accent : Tile,
            ForeColor = primary ? Background : Text,
            Padding = new Padding(10, 3, 10, 3),
            Cursor = Cursors.Hand,
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }
}
