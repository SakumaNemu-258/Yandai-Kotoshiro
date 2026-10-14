using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Kotoshiro;

public sealed class SubtitleOverlay : Form
{
    private const int WmNcHitTest = 0x84, WmNcLeftButtonDown = 0xA1, WmNcLeftButtonDoubleClick = 0xA3, WmNcRightButtonUp = 0xA5;
    private const int HtTransparent = -1, HtClient = 1, HtCaption = 2, HtLeft = 10, HtRight = 11, HtTop = 12, HtTopLeft = 13, HtTopRight = 14, HtBottom = 15, HtBottomLeft = 16, HtBottomRight = 17;
    private const int ExStyleIndex = -20, ExTransparent = 0x20, ExLayered = 0x80000;
    private const int EmGetScrollPos = 0x04DD, EmSetScrollPos = 0x04DE, EmGetFirstVisibleLine = 0xCE, EmLineScroll = 0xB6, EmSetParaFormat = 0x447;
    private const uint ParagraphSpacingMask = 0x40 | 0x80 | 0x100;
    private const int CaptionGapTwips = 45; // 3 px at 96 DPI; no empty subtitle line.
    internal const int MaximumEntries = 300, MaximumHistoryCharacters = 100_000;
    private readonly CaptionHistoryBox history = new()
    {
        Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, ReadOnly = true,
        DetectUrls = false, WordWrap = true, ScrollBars = RichTextBoxScrollBars.ForcedVertical,
        HideSelection = false, ShortcutsEnabled = true, Cursor = Cursors.IBeam
    };
    private readonly Label footer = new()
    {
        Dock = DockStyle.Bottom, Height = 23, AutoEllipsis = true,
        TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(0, 3, 0, 0)
    };
    private readonly Panel grip = new() { Dock = DockStyle.Top, Height = 14, Cursor = Cursors.SizeAll };
    private readonly ContextMenuStrip captionMenu = new();
    private readonly List<CaptionEntry> entries = new();
    private Font? japaneseFont, chineseFont, footerFont;
    private bool passThrough, showJapanese = true, followingLatest = true, updatingHistory;
    private string statusText = "字幕准备就绪";

    private sealed record CaptionEntry(string Source, string Translated);
    private readonly record struct Viewport(int SelectionStart, int SelectionLength, Point Scroll, int AnchorCharacter, int AnchorY, bool Follow);

    public SubtitleOverlay(AppSettings settings)
    {
        Text = "言代Kotoshiro · 字幕";
        Theme.Apply(this);
        BackColor = Theme.Surface;
        FormBorderStyle = FormBorderStyle.None;
        ControlBox = false; MinimizeBox = false; MaximizeBox = false;
        TopMost = true; ShowInTaskbar = false; AutoScaleMode = AutoScaleMode.None;
        MinimumSize = new Size(400, 120);
        Size = new Size(Math.Clamp(settings.OverlayWidth, 400, 2000), Math.Clamp(settings.OverlayHeight, 120, 800));
        StartPosition = FormStartPosition.Manual;
        var screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        var saved = new Rectangle(settings.OverlayX, settings.OverlayY, Width, Height);
        Location = settings.OverlayX != -1 && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(saved))
            ? saved.Location : new Point(screen.Left + Math.Max(0, (screen.Width - Width) / 2), screen.Bottom - Height - 60);
        Padding = new Padding(18, 9, 15, 10);
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        Controls.Add(history); Controls.Add(footer); Controls.Add(grip);

        var copySelection = captionMenu.Items.Add("复制选中文字", null, (_, _) => { if (history.SelectionLength > 0) history.Copy(); });
        captionMenu.Items.Add("复制全部历史", null, (_, _) => { if (history.TextLength > 0) Clipboard.SetText(history.Text.TrimEnd()); });
        captionMenu.Items.Add("回到最新字幕", null, (_, _) => FollowLatest());
        captionMenu.Items.Add("清空历史", null, (_, _) => ClearHistory());
        captionMenu.Items.Add(new ToolStripSeparator());
        captionMenu.Items.Add("隐藏字幕（Esc）", null, (_, _) => Hide());
        captionMenu.Opening += (_, _) => copySelection.Enabled = history.SelectionLength > 0;
        ContextMenuStrip = history.ContextMenuStrip = footer.ContextMenuStrip = grip.ContextMenuStrip = captionMenu;
        history.ViewportChanged += (_, _) => UpdateFollowingState();
        history.SelectionChanged += (_, _) => UpdateFollowingState();
        history.Resize += (_, _) => { if (followingLatest && !updatingHistory) FollowLatest(); };
        grip.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RoundedPath(new RectangleF(Math.Max(0, (grip.Width - 38) / 2f), 3, 38, 4), 2);
            using var color = new SolidBrush(Theme.Muted); e.Graphics.FillPath(color, path);
        };
        foreach (var control in new Control[] { grip, footer })
        {
            control.MouseDown += DragCaption;
            control.MouseMove += (_, e) => control.Cursor = CursorForHit(HitTest(PointToClient(control.PointToScreen(e.Location))));
        }
        MouseDown += DragCaption;
        MouseMove += (_, e) => Cursor = CursorForHit(HitTest(e.Location));
        Cursor = Cursors.SizeAll;
        Apply(settings); UpdateRoundedRegion();
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
    }

    public void Apply(AppSettings settings)
    {
        var viewport = CaptureViewport();
        if (showJapanese != settings.ShowJapanese)
        {
            int start = RemapCharacter(viewport.SelectionStart, showJapanese, settings.ShowJapanese);
            int end = RemapCharacter(viewport.SelectionStart + viewport.SelectionLength, showJapanese, settings.ShowJapanese);
            viewport = viewport with { SelectionStart = start, SelectionLength = Math.Max(0, end - start), AnchorCharacter = RemapCharacter(viewport.AnchorCharacter, showJapanese, settings.ShowJapanese) };
        }
        showJapanese = settings.ShowJapanese;
        var oldJapanese = japaneseFont; var oldChinese = chineseFont; var oldFooter = footerFont;
        japaneseFont = new Font("Microsoft YaHei UI", Math.Clamp(settings.FontSize - 10, 10, 32));
        chineseFont = new Font("Microsoft YaHei UI", Math.Clamp(settings.FontSize, 14, 48), FontStyle.Bold);
        footerFont = new Font("Microsoft YaHei UI", 9);
        // The final insertion paragraph is small; actual caption paragraphs
        // have explicit fonts and native RichEdit spacing below.
        history.Font = footerFont; footer.Font = footerFont;
        ApplyThemeColors(); RebuildHistory(viewport, 0);
        oldJapanese?.Dispose(); oldChinese?.Dispose(); oldFooter?.Dispose();
        Opacity = Math.Clamp(settings.OverlayOpacity, 40, 100) / 100.0;
        if (IsHandleCreated) ApplyExtendedStyle();
    }

    public void ApplyTheme()
    {
        var viewport = CaptureViewport();
        ApplyThemeColors(); RebuildHistory(viewport, 0);
    }

    private void ApplyThemeColors()
    {
        BackColor = history.BackColor = footer.BackColor = grip.BackColor = Theme.Surface;
        history.ForeColor = Theme.Text; footer.ForeColor = Theme.Muted;
        captionMenu.BackColor = Theme.Surface; captionMenu.ForeColor = Theme.Text;
        Theme.RefreshMenu(captionMenu);
        grip.Invalidate(); Invalidate();
    }

    public void SetStatus(string status)
    {
        statusText = status.Replace('\r', ' ').Replace('\n', ' ').Trim();
        UpdateFooter();
    }

    public void SetCaption(string source, string translated)
    {
        if (string.IsNullOrWhiteSpace(translated)) return;
        var viewport = CaptureViewport();
        int removedCharacters = 0;
        entries.Add(new CaptionEntry(NormalizeCaption(source, 20_000), NormalizeCaption(translated, 60_000)));
        int length = entries.Sum(StoredLength);
        while (entries.Count > MaximumEntries || length > MaximumHistoryCharacters)
        {
            removedCharacters += RenderedLength(entries[0]); length -= StoredLength(entries[0]); entries.RemoveAt(0);
        }
        if (removedCharacters > 0) RebuildHistory(viewport, removedCharacters);
        else
        {
            updatingHistory = true;
            try { AppendEntry(entries[^1]); RestoreViewport(viewport, 0, false); }
            finally { updatingHistory = false; }
        }
        statusText = "字幕持续更新";
        UpdateFooter();
    }

    private static string NormalizeCaption(string text, int limit)
    {
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        return text.Length <= limit ? text : text[..limit] + "…";
    }
    private int RenderedLength(CaptionEntry entry) => entry.Translated.Length + 1 + (showJapanese && entry.Source.Length > 0 ? entry.Source.Length + 1 : 0);
    private static int StoredLength(CaptionEntry entry) => entry.Translated.Length + entry.Source.Length + 3;

    private int RemapCharacter(int character, bool fromJapanese, bool toJapanese)
    {
        int oldStart = 0, newStart = 0;
        foreach (var entry in entries)
        {
            int oldSource = fromJapanese && entry.Source.Length > 0 ? entry.Source.Length + 1 : 0;
            int newSource = toJapanese && entry.Source.Length > 0 ? entry.Source.Length + 1 : 0;
            int oldLength = oldSource + entry.Translated.Length + 1;
            if (character < oldStart + oldLength)
            {
                int offset = Math.Max(0, character - oldStart);
                return newStart + (offset < oldSource ? toJapanese ? offset : 0 : newSource + offset - oldSource);
            }
            oldStart += oldLength; newStart += newSource + entry.Translated.Length + 1;
        }
        return newStart;
    }

    private void AppendEntry(CaptionEntry entry)
    {
        int sourceStart = history.TextLength;
        history.Select(sourceStart, 0);
        if (showJapanese && entry.Source.Length > 0)
        {
            history.SelectionColor = Theme.Muted; history.SelectionFont = japaneseFont ?? history.Font;
            history.AppendText(entry.Source + "\n");
            SetParagraphSpacing(sourceStart, entry.Source.Length, japaneseFont ?? history.Font, 0);
        }
        int translatedStart = history.TextLength;
        history.Select(translatedStart, 0);
        history.SelectionColor = Theme.Text; history.SelectionFont = chineseFont ?? history.Font;
        history.AppendText(entry.Translated + "\n");
        SetParagraphSpacing(translatedStart, entry.Translated.Length, chineseFont ?? history.Font, 0);
        int finalParagraph = translatedStart + entry.Translated.LastIndexOf('\n') + 1;
        SetParagraphSpacing(finalParagraph, entry.Translated.Length - (finalParagraph - translatedStart), chineseFont ?? history.Font, CaptionGapTwips);
        history.Select(history.TextLength, 0);
        history.SelectionFont = footerFont ?? history.Font;
        SetParagraphSpacing(history.TextLength, 0, footerFont ?? history.Font, 0);
    }

    private void SetParagraphSpacing(int start, int length, Font font, int after)
    {
        // Newline font size alone cannot remove paragraph leading in RichEdit.
        // Its default CJK line grid is taller than the font's actual line box.
        // Use the measured line height plus 1 px, with zero paragraph leading;
        // only the final Chinese paragraph gets a small inter-caption gap.
        history.Select(start, length);
        int dpi = history.DeviceDpi;
        int lineHeight = (int)Math.Ceiling(font.GetHeight(dpi)) + Math.Max(1, dpi / 96);
        var format = new ParagraphFormat
        {
            Size = (uint)Marshal.SizeOf<ParagraphFormat>(), Mask = ParagraphSpacingMask,
            Tabs = new int[32], SpaceBefore = 0, SpaceAfter = after,
            LineSpacing = (int)Math.Ceiling(lineHeight * 1440.0 / dpi), LineSpacingRule = 4
        };
        SendMessageParagraph(history.Handle, EmSetParaFormat, IntPtr.Zero, ref format);
    }

    private void RebuildHistory(Viewport viewport, int removedCharacters)
    {
        updatingHistory = true;
        try
        {
            history.Clear();
            foreach (var entry in entries) AppendEntry(entry);
            RestoreViewport(viewport, removedCharacters, true);
        }
        finally { updatingHistory = false; }
        UpdateFooter();
    }

    private Viewport CaptureViewport()
    {
        if (!history.IsHandleCreated) return new(history.SelectionStart, history.SelectionLength, Point.Empty, 0, 0, followingLatest);
        var scroll = Point.Empty; SendMessagePoint(history.Handle, EmGetScrollPos, IntPtr.Zero, ref scroll);
        int anchor = history.GetCharIndexFromPosition(new Point(1, 1));
        return new(history.SelectionStart, history.SelectionLength, scroll, anchor,
            history.GetPositionFromCharIndex(anchor).Y, followingLatest && IsAtBottom() && history.SelectionLength == 0);
    }

    private void RestoreViewport(Viewport viewport, int removedCharacters, bool rebuilt)
    {
        followingLatest = viewport.Follow;
        if (viewport.Follow)
        {
            ScrollToLatest();
        }
        else
        {
            int selection = Math.Clamp(viewport.SelectionStart - removedCharacters, 0, history.TextLength);
            history.Select(selection, Math.Clamp(viewport.SelectionLength, 0, history.TextLength - selection));
            if (history.IsHandleCreated)
            {
                var scroll = viewport.Scroll;
                if (rebuilt)
                {
                    int anchor = Math.Clamp(viewport.AnchorCharacter - removedCharacters, 0, history.TextLength);
                    int line = history.GetLineFromCharIndex(anchor);
                    int firstLine = (int)SendMessage(history.Handle, EmGetFirstVisibleLine, IntPtr.Zero, IntPtr.Zero);
                    SendMessage(history.Handle, EmLineScroll, IntPtr.Zero, (IntPtr)(line - firstLine));
                    SendMessagePoint(history.Handle, EmGetScrollPos, IntPtr.Zero, ref scroll);
                    scroll.Y += history.GetPositionFromCharIndex(anchor).Y - viewport.AnchorY;
                }
                SendMessagePoint(history.Handle, EmSetScrollPos, IntPtr.Zero, ref scroll);
            }
        }
    }

    private bool IsAtBottom()
    {
        if (!history.IsHandleCreated) return true;
        var info = new ScrollInfo { Size = (uint)Marshal.SizeOf<ScrollInfo>(), Mask = 0x7 };
        return !GetScrollInfo(history.Handle, 1, ref info) || info.Position >= info.Maximum - Math.Max(1, (int)info.Page) - 2;
    }

    private void UpdateFollowingState()
    {
        if (updatingHistory) return;
        followingLatest = IsAtBottom() && history.SelectionLength == 0;
        UpdateFooter();
    }

    private void UpdateFooter() => footer.Text = (followingLatest ? statusText : "浏览历史 · 新字幕保留在底部") + "   ·   拖动上边移动 / 右侧滑条查看";

    public void FollowLatest()
    {
        updatingHistory = true;
        try { followingLatest = true; ScrollToLatest(); }
        finally { updatingHistory = false; }
        UpdateFooter();
    }

    private void ScrollToLatest()
    {
        history.Select(history.TextLength, 0); history.ScrollToCaret();
        if (history.IsHandleCreated) SendMessage(history.Handle, 0x115, (IntPtr)7, IntPtr.Zero);
    }

    public void ClearHistory()
    {
        updatingHistory = true;
        try { entries.Clear(); history.Clear(); followingLatest = true; }
        finally { updatingHistory = false; }
        statusText = "历史已清空"; UpdateFooter();
    }

    internal RichTextBox HistoryForTest => history;
    internal int EntryCountForTest => entries.Count;
    internal bool FollowingLatestForTest => followingLatest;

    internal Bitmap CapturePreview()
    {
        // RichEdit does not draw its text through Form.DrawToBitmap. Print our own
        // window directly, including its current viewport and native scrollbar.
        var image = new Bitmap(Width, Height);
        using var graphics = Graphics.FromImage(image);
        var dc = graphics.GetHdc();
        bool captured;
        try { captured = PrintWindow(Handle, dc, 2); }
        finally { graphics.ReleaseHdc(dc); }
        if (!captured) { image.Dispose(); throw new InvalidOperationException("字幕窗口预览生成失败。"); }
        return image;
    }

    public void SetPassThrough(bool enabled)
    {
        passThrough = enabled;
        Cursor = grip.Cursor = footer.Cursor = enabled ? Cursors.Default : Cursors.SizeAll;
        history.Cursor = enabled ? Cursors.Default : Cursors.IBeam;
        if (enabled) captionMenu.Close();
        if (IsHandleCreated) ApplyExtendedStyle();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyExtendedStyle(); UpdateRoundedRegion();
    }

    private void ApplyExtendedStyle()
    {
        int style = GetWindowLong(Handle, ExStyleIndex);
        int updated = passThrough ? style | ExTransparent | ExLayered : style & ~ExTransparent;
        SetWindowLong(Handle, ExStyleIndex, updated);
        if ((updated & ExLayered) != 0) SetLayeredWindowAttributes(Handle, 0, (byte)Math.Round(Opacity * 255), 2);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e); UpdateRoundedRegion();
    }

    private float CornerRadius => Math.Min(22f * DeviceDpi / 96f, Math.Min(ClientSize.Width, ClientSize.Height) / 2f);

    private static GraphicsPath RoundedPath(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath(); float diameter = radius * 2;
        if (diameter <= 0) { path.AddRectangle(bounds); return path; }
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure(); return path;
    }

    private void UpdateRoundedRegion()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        using var path = RoundedPath(new RectangleF(0, 0, ClientSize.Width, ClientSize.Height), CornerRadius);
        var old = Region; Region = new Region(path); old?.Dispose(); Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (ClientSize.Width <= 1 || ClientSize.Height <= 1) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedPath(new RectangleF(.5f, .5f, ClientSize.Width - 1, ClientSize.Height - 1), Math.Max(1, CornerRadius - .5f));
        using var border = new Pen(Theme.Border); e.Graphics.DrawPath(border, path);
    }

    internal int HitTest(Point point)
    {
        if (passThrough) return HtTransparent;
        int edge = Math.Max(7, 8 * DeviceDpi / 96), corner = Math.Max(edge * 2, 24 * DeviceDpi / 96);
        bool left = point.X < edge, right = point.X >= ClientSize.Width - edge;
        bool top = point.Y < edge, bottom = point.Y >= ClientSize.Height - edge;
        if ((left && point.Y < corner) || (top && point.X < corner)) return HtTopLeft;
        if ((right && point.Y < corner) || (top && point.X >= ClientSize.Width - corner)) return HtTopRight;
        if ((left && point.Y >= ClientSize.Height - corner) || (bottom && point.X < corner)) return HtBottomLeft;
        if ((right && point.Y >= ClientSize.Height - corner) || (bottom && point.X >= ClientSize.Width - corner)) return HtBottomRight;
        if (left) return HtLeft; if (right) return HtRight;
        if (top) return HtTop; if (bottom) return HtBottom;
        return HtClient;
    }

    private static Cursor CursorForHit(int hit) => hit switch
    {
        HtLeft or HtRight => Cursors.SizeWE, HtTop or HtBottom => Cursors.SizeNS,
        HtTopLeft or HtBottomRight => Cursors.SizeNWSE, HtTopRight or HtBottomLeft => Cursors.SizeNESW,
        HtTransparent => Cursors.Default, _ => Cursors.SizeAll
    };

    private void DragCaption(object? sender, MouseEventArgs e)
    {
        if (passThrough || e.Button != MouseButtons.Left || sender is not Control control) return;
        var screenPoint = control.PointToScreen(e.Location); int hit = HitTest(PointToClient(screenPoint));
        ReleaseCapture(); int packedPoint = (screenPoint.Y << 16) | (screenPoint.X & 0xffff);
        SendMessage(Handle, WmNcLeftButtonDown, (IntPtr)(hit == HtClient ? HtCaption : hit), (IntPtr)packedPoint);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmNcHitTest)
        {
            long position = m.LParam.ToInt64();
            var point = PointToClient(new Point(unchecked((short)(position & 0xffff)), unchecked((short)((position >> 16) & 0xffff))));
            m.Result = (IntPtr)HitTest(point); return;
        }
        if (m.Msg == WmNcLeftButtonDoubleClick) { m.Result = IntPtr.Zero; return; }
        if (m.Msg == WmNcRightButtonUp && !passThrough) { captionMenu.Show(Cursor.Position); m.Result = IntPtr.Zero; return; }
        base.WndProc(ref m);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Hide(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    public void RememberBounds(AppSettings settings)
    {
        settings.OverlayX = Left; settings.OverlayY = Top; settings.OverlayWidth = Width; settings.OverlayHeight = Height;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { captionMenu.Dispose(); japaneseFont?.Dispose(); chineseFont?.Dispose(); footerFont?.Dispose(); }
        base.Dispose(disposing);
    }

    private sealed class CaptionHistoryBox : RichTextBox
    {
        public event EventHandler? ViewportChanged;
        protected override void WndProc(ref Message m)
        {
            int message = m.Msg;
            base.WndProc(ref m);
            if (message is 0x115 or 0x20A or 0x101 or EmSetScrollPos or EmLineScroll) ViewportChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScrollInfo { public uint Size, Mask; public int Minimum, Maximum; public uint Page; public int Position, TrackPosition; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ParagraphFormat
    {
        public uint Size, Mask;
        public ushort Numbering, Reserved;
        public int StartIndent, RightIndent, Offset;
        public ushort Alignment;
        public short TabCount;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public int[] Tabs;
        public int SpaceBefore, SpaceAfter, LineSpacing;
        public short Style;
        public byte LineSpacingRule, OutlineLevel;
        public ushort ShadingWeight, ShadingStyle, NumberingStart, NumberingStyle, NumberingTab, BorderSpace, BorderWidth, Borders;
    }
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr handle, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr handle, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr handle, uint colorKey, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr handle, IntPtr dc, uint flags);
    [DllImport("user32.dll")] private static extern bool GetScrollInfo(IntPtr handle, int bar, ref ScrollInfo info);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr SendMessagePoint(IntPtr handle, int message, IntPtr wParam, ref Point lParam);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr SendMessageParagraph(IntPtr handle, int message, IntPtr wParam, ref ParagraphFormat lParam);
}
