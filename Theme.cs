using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Kotoshiro;

public static class Theme
{
    private sealed record Palette(Color Background, Color Surface, Color SurfaceRaised, Color Border,
        Color Text, Color Muted, Color Accent, Color Selection, Color Primary, Color PrimaryText);
    private static readonly Palette Dark = new(
        Color.FromArgb(23, 23, 23), Color.FromArgb(33, 33, 33), Color.FromArgb(43, 43, 43),
        Color.FromArgb(57, 57, 57), Color.FromArgb(236, 236, 236), Color.FromArgb(164, 164, 164),
        Color.FromArgb(114, 171, 247), Color.FromArgb(53, 53, 53), Color.FromArgb(235, 235, 235), Color.FromArgb(20, 20, 20));
    private static readonly Palette Light = new(
        Color.FromArgb(247, 247, 247), Color.White, Color.FromArgb(243, 243, 243),
        Color.FromArgb(228, 228, 228), Color.FromArgb(30, 30, 30), Color.FromArgb(106, 106, 106),
        Color.FromArgb(50, 112, 218), Color.FromArgb(232, 232, 232), Color.FromArgb(36, 36, 36), Color.FromArgb(251, 251, 251));
    private static readonly ConditionalWeakTable<Form, object> StyledForms = new();
    private static readonly Regex RtfColor = new(@"\\red(\d+)\\green(\d+)\\blue(\d+);", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static Palette palette = Dark;

    public static string Current { get; private set; } = "dark";
    public static bool IsDark => Current == "dark";
    public static Color Background => palette.Background;
    public static Color Surface => palette.Surface;
    public static Color SurfaceRaised => palette.SurfaceRaised;
    public static Color Border => palette.Border;
    public static Color Text => palette.Text;
    public static Color Muted => palette.Muted;
    public static Color Accent => palette.Accent;
    public static Color Selection => palette.Selection;
    public static Color Primary => palette.Primary;
    public static Color PrimaryText => palette.PrimaryText;

    public static string Normalize(string? theme) => theme?.Trim().ToLowerInvariant() is "light" or "white" or "白色" ? "light" : "dark";
    public static void Use(string? theme)
    {
        Current = Normalize(theme);
        palette = IsDark ? Dark : Light;
    }

    public static void Apply(Control control)
    {
        control.BackColor = Background; control.ForeColor = Text;
        control.Font = new Font("Microsoft YaHei UI", 10);
        if (control is Form form) { form.Icon = Branding.ApplicationIcon; AttachTitleBar(form); }
    }

    /// <summary>Refreshes existing controls without rebuilding them or resetting their values.</summary>
    public static void Refresh(Control control)
    {
        // Captions rebuild their styled history while keeping the viewed entry
        // and follow state; generic RTF changes can fire selection events.
        if (control is SubtitleOverlay overlay) { overlay.ApplyTheme(); return; }
        var menus = new HashSet<ContextMenuStrip>();
        RefreshTree(control, menus);
        foreach (var menu in menus) RefreshMenu(menu);
        control.Invalidate(true);
    }

    public static void RefreshOpenForms()
    {
        foreach (var form in Application.OpenForms.Cast<Form>().ToArray())
            if (!form.IsDisposed) Refresh(form);
    }

    private static void RefreshTree(Control control, HashSet<ContextMenuStrip> menus)
    {
        // Keep video black and QR images' white scan margin intact.
        if (control is not PictureBox && !(control is Panel && control.BackColor.ToArgb() == Color.Black.ToArgb()))
        {
            if (control is RichTextBox rich) RefreshRichText(rich);
            else if (control is RoundedButton { IsPrimary: true })
            {
                control.BackColor = Primary; control.ForeColor = PrimaryText;
            }
            else
            {
                control.BackColor = MapBackground(control.BackColor, control);
                control.ForeColor = MapForeground(control.ForeColor);
            }
        }
        if (control is Form form) AttachTitleBar(form);
        if (control is ToolStrip strip) RefreshMenu(strip);
        if (control.ContextMenuStrip is { } menu) menus.Add(menu);
        foreach (Control child in control.Controls) RefreshTree(child, menus);
    }

    private static Color MapBackground(Color color, Control control)
    {
        if (color.A == 0) return color;
        var mapped = MapPalette(color);
        if (mapped.HasValue) return mapped.Value;
        if (control is TextBoxBase or ComboBox or NumericUpDown)
        {
            if (color.ToArgb() == Color.White.ToArgb() || color.IsSystemColor)
                return control is RichTextBox ? Surface : SurfaceRaised;
        }
        if (color.IsSystemColor) return control.Parent?.BackColor ?? Background;
        return color;
    }

    private static Color MapForeground(Color color)
    {
        // White is a surface role in the light palette, but a raw white glyph is text.
        if (color.IsSystemColor || color.ToArgb() == Color.Black.ToArgb() || color.ToArgb() == Color.White.ToArgb()) return Text;
        var mapped = MapPalette(color);
        if (mapped.HasValue) return mapped.Value;
        return color;
    }

    private static Color? MapPalette(Color color)
    {
        int rgb = color.ToArgb();
        foreach (var from in new[] { Dark, Light })
        {
            if (rgb == from.Background.ToArgb()) return Background;
            if (rgb == from.Surface.ToArgb()) return Surface;
            if (rgb == from.SurfaceRaised.ToArgb()) return SurfaceRaised;
            if (rgb == from.Border.ToArgb()) return Border;
            if (rgb == from.Text.ToArgb()) return Text;
            if (rgb == from.Muted.ToArgb()) return Muted;
            if (rgb == from.Accent.ToArgb()) return Accent;
            if (rgb == from.Selection.ToArgb()) return Selection;
            if (rgb == from.Primary.ToArgb()) return Primary;
            if (rgb == from.PrimaryText.ToArgb()) return PrimaryText;
        }
        return null;
    }

    private static void RefreshRichText(RichTextBox rich)
    {
        Color foreground = MapForeground(rich.ForeColor);
        Color background = MapBackground(rich.BackColor, rich);
        if (rich.TextLength == 0) { rich.BackColor = background; rich.ForeColor = foreground; return; }
        string oldRtf = rich.Rtf ?? "";
        string newRtf = RtfColor.Replace(oldRtf, match =>
        {
            var oldColor = Color.FromArgb(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value));
            var next = MapForeground(oldColor);
            return $"\\red{next.R}\\green{next.G}\\blue{next.B};";
        });
        bool foregroundChanged = rich.ForeColor.ToArgb() != foreground.ToArgb();
        if (newRtf == oldRtf && !foregroundChanged) { rich.BackColor = background; return; }
        int start = rich.SelectionStart, length = rich.SelectionLength;
        var scroll = new NativePoint();
        if (rich.IsHandleCreated) SendMessage(rich.Handle, 0x4DD, IntPtr.Zero, ref scroll);
        // RichTextBox.ForeColor replaces every run. Restore the converted RTF afterwards.
        rich.BackColor = background; rich.ForeColor = foreground;
        rich.Rtf = newRtf;
        rich.Select(Math.Min(start, rich.TextLength), Math.Min(length, Math.Max(0, rich.TextLength - start)));
        if (rich.IsHandleCreated) SendMessage(rich.Handle, 0x4DE, IntPtr.Zero, ref scroll);
    }

    public static void RefreshMenu(ToolStrip strip)
    {
        strip.BackColor = Surface; strip.ForeColor = Text;
        strip.Renderer = new ToolStripProfessionalRenderer(new ThemeMenuColors()) { RoundedEdges = true };
        foreach (ToolStripItem item in strip.Items)
        {
            item.BackColor = Surface; item.ForeColor = Text;
            if (item is ToolStripDropDownItem drop && drop.HasDropDownItems) RefreshMenu(drop.DropDown);
        }
        strip.Invalidate();
    }

    private static void AttachTitleBar(Form form)
    {
        if (!StyledForms.TryGetValue(form, out _))
        {
            StyledForms.Add(form, new object());
            form.HandleCreated += (_, _) => UpdateTitleBar(form);
        }
        if (form.IsHandleCreated) UpdateTitleBar(form);
    }

    private static void UpdateTitleBar(Form form)
    {
        int enabled = IsDark ? 1 : 0;
        if (DwmSetWindowAttribute(form.Handle, 20, ref enabled, sizeof(int)) != 0)
            _ = DwmSetWindowAttribute(form.Handle, 19, ref enabled, sizeof(int));
    }

    public static Button Button(string text, bool primary = false) => new RoundedButton(primary)
    {
        Text = text, AutoSize = true, MinimumSize = new Size(100, 40),
        FlatStyle = FlatStyle.Flat, BackColor = primary ? Primary : SurfaceRaised,
        ForeColor = primary ? PrimaryText : Text, Padding = new Padding(14, 5, 14, 5),
        Cursor = Cursors.Hand, Margin = new Padding(0, 0, 8, 8), UseVisualStyleBackColor = false
    };
    public static Panel Card(int radius = 16) => new RoundedPanel(radius)
    {
        BackColor = Surface, Padding = new Padding(20), Margin = new Padding(0, 0, 0, 16)
    };
    public static TextBox Input(string text = "") => new()
    {
        Text = text, BorderStyle = BorderStyle.FixedSingle, BackColor = SurfaceRaised,
        ForeColor = Text, Dock = DockStyle.Fill
    };
    public static Label Label(string text, bool muted = false) => new()
    {
        Text = text, ForeColor = muted ? Muted : Text, BackColor = Color.Transparent,
        AutoSize = true, MaximumSize = new Size(640, 0), Margin = new Padding(0, 8, 8, 8)
    };
    public static ComboBox Combo(bool editable = false)
    {
        var combo = new ComboBox
        {
            DropDownStyle = editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList,
            DrawMode = editable ? DrawMode.Normal : DrawMode.OwnerDrawFixed, ItemHeight = 28,
            BackColor = SurfaceRaised, ForeColor = Text, Dock = DockStyle.Fill,
            IntegralHeight = false, DropDownHeight = 300
        };
        if (!editable) combo.DrawItem += (_, e) =>
        {
            using var brush = new SolidBrush((e.State & DrawItemState.Selected) != 0 ? Selection : SurfaceRaised);
            e.Graphics.FillRectangle(brush, e.Bounds);
            string value = e.Index >= 0 && e.Index < combo.Items.Count ? combo.GetItemText(combo.Items[e.Index]) ?? "" : combo.Text;
            TextRenderer.DrawText(e.Graphics, value, combo.Font,
                new Rectangle(e.Bounds.X + 8, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 16), e.Bounds.Height),
                combo.Enabled ? Text : Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };
        return combo;
    }

    internal static GraphicsPath RoundedPath(RectangleF bounds, float radius)
    {
        float diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();
        if (diameter <= 0) { path.AddRectangle(bounds); return path; }
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure(); return path;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, ref NativePoint point);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);

    private sealed class ThemeMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Surface;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Border;
        public override Color MenuItemSelected => Selection;
        public override Color MenuItemSelectedGradientBegin => Selection;
        public override Color MenuItemSelectedGradientEnd => Selection;
        public override Color MenuItemPressedGradientBegin => Selection;
        public override Color MenuItemPressedGradientMiddle => Selection;
        public override Color MenuItemPressedGradientEnd => Selection;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Surface;
    }
}

internal sealed class RoundedPanel : Panel
{
    private readonly int radius;
    public RoundedPanel(int radius)
    {
        this.radius = radius;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Background);
        if (Width < 2 || Height < 2) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.RoundedPath(new RectangleF(.5f, .5f, Width - 1, Height - 1), radius);
        using var fill = new SolidBrush(BackColor); using var edge = new Pen(Theme.Border);
        e.Graphics.FillPath(fill, path); e.Graphics.DrawPath(edge, path);
    }
}

internal sealed class RoundedButton : Button
{
    private bool hovered, pressed;
    internal bool IsPrimary { get; }
    public RoundedButton(bool primary = false)
    {
        IsPrimary = primary;
        FlatAppearance.BorderSize = 0;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Background);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Color fillColor = !Enabled ? Theme.SurfaceRaised : pressed ? ControlPaint.Dark(BackColor, .12f) : hovered ? ControlPaint.Light(BackColor, .1f) : BackColor;
        using var path = Theme.RoundedPath(new RectangleF(.5f, .5f, Width - 1, Height - 1), 9);
        using var fill = new SolidBrush(fillColor); e.Graphics.FillPath(fill, path);
        if (!IsPrimary) { using var border = new Pen(Theme.Border); e.Graphics.DrawPath(border, path); }
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Enabled ? ForeColor : Theme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (Focused && ShowFocusCues)
        {
            using var focus = new Pen(Enabled && IsPrimary ? Theme.PrimaryText : Theme.Accent) { DashStyle = DashStyle.Dot };
            using var inside = Theme.RoundedPath(new RectangleF(4, 4, Width - 8, Height - 8), 6); e.Graphics.DrawPath(focus, inside);
        }
    }
}

internal sealed class AudioLevelMeter : Control
{
    private int value;
    public int Maximum { get; set; } = 100;
    public int Value { get => value; set { this.value = Math.Clamp(value, 0, Math.Max(1, Maximum)); Invalidate(); } }
    public AudioLevelMeter() => SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Surface);
        if (Width < 2 || Height < 2) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var track = Theme.RoundedPath(new RectangleF(0, 0, Width, Height), Height / 2f);
        using var background = new SolidBrush(Theme.SurfaceRaised); e.Graphics.FillPath(background, track);
        float activeWidth = Width * value / (float)Math.Max(1, Maximum);
        if (activeWidth < 1) return;
        using var active = Theme.RoundedPath(new RectangleF(0, 0, activeWidth, Height), Height / 2f);
        using var color = new SolidBrush(Theme.Accent); e.Graphics.FillPath(color, active);
    }
}
