using System.Drawing.Drawing2D;

namespace PanelSonidos;

/// <summary>Boton de un sonido: clic para reproducir, chip inferior para asignar tecla, ⋯ para opciones.</summary>
sealed class SoundTile : Control
{
    public readonly SoundEntry Entry;
    public bool PlayingNow;
    public bool HotkeyTaken;
    public bool Broken;
    public event Action? PlayClicked, KeyClicked, MenuClicked;

    readonly float scale;
    readonly Font nameFont, chipFont, menuFont;
    bool hover;

    public SoundTile(SoundEntry entry, float scale)
    {
        Entry = entry;
        this.scale = scale;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Size = new Size(S(196), S(108));
        Margin = new Padding(S(7));
        Cursor = Cursors.Hand;
        nameFont = new Font("Segoe UI Semibold", 11f);
        chipFont = new Font("Segoe UI", 8.5f);
        menuFont = new Font("Segoe UI", 12f, FontStyle.Bold);
    }

    int S(int value) => (int)Math.Round(value * scale);

    Rectangle ChipRect => new(S(14), Height - S(38), Width - S(28), S(26));
    Rectangle MenuRect => new(Width - S(36), S(4), S(32), S(26));

    string ChipText
    {
        get
        {
            if (Entry.Key == 0) return "Asignar tecla";
            var name = KeyNames.Describe(Entry.Mods, Entry.Key);
            return HotkeyTaken ? name + " (ocupada)" : name;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Background);

        var tileColor = hover ? Theme.TileHover : Theme.Tile;
        using (var path = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), S(10)))
        {
            using var fill = new SolidBrush(tileColor);
            g.FillPath(fill, path);
            if (PlayingNow)
            {
                using var border = new Pen(Theme.Playing, S(2));
                g.DrawPath(border, path);
            }
        }

        var nameColor = Broken ? Theme.Warn : Theme.Text;
        var nameText = Broken ? Entry.Name + " (no se pudo cargar)" : Entry.Name;
        var nameArea = new Rectangle(S(14), S(10), Width - S(48), Height - S(54));
        TextRenderer.DrawText(g, nameText, nameFont, nameArea, nameColor, tileColor,
            TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        TextRenderer.DrawText(g, "⋯", menuFont, MenuRect, Theme.Muted, tileColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        using (var chip = Rounded(ChipRect, S(6)))
        using (var fill = new SolidBrush(Theme.Chip))
            g.FillPath(fill, chip);

        var chipColor = HotkeyTaken ? Theme.Warn : Entry.Key == 0 ? Theme.Muted : Theme.Accent;
        TextRenderer.DrawText(g, ChipText, chipFont, ChipRect, chipColor, Theme.Chip,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    static GraphicsPath Rounded(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Right || MenuRect.Contains(e.Location)) MenuClicked?.Invoke();
        else if (ChipRect.Contains(e.Location)) KeyClicked?.Invoke();
        else if (e.Button == MouseButtons.Left) PlayClicked?.Invoke();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            nameFont.Dispose();
            chipFont.Dispose();
            menuFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
