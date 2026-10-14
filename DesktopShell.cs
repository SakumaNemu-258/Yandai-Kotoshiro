using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Kotoshiro;

/// <summary>A compact, neutral desktop frame. Existing content keeps its own layout and events.</summary>
public sealed class DesktopShell : IDisposable, IMessageFilter
{
    private static readonly ConditionalWeakTable<Form, DesktopShell> Shells = new();
    private readonly Form form;
    private readonly ShellWindow native;
    private readonly ShellHeader header;
    private readonly TableLayoutPanel layout;
    private readonly bool followsTitle;
    private readonly bool resizable;
    private bool disposed;

    public Panel Content { get; }
    public Panel Header => header;
    internal Rectangle BrandingIconBounds => HeaderBoundsToForm(header.BrandingIconBounds);
    internal RectangleF BrandingGlyphDrawBounds
    {
        get
        {
            RectangleF bounds = header.BrandingGlyphDrawBounds;
            if (bounds.IsEmpty || !header.IsHandleCreated) return RectangleF.Empty;
            Point origin = form.PointToClient(header.PointToScreen(Point.Empty));
            bounds.Offset(origin.X, origin.Y); return bounds;
        }
    }
    private Rectangle HeaderBoundsToForm(Rectangle bounds)
    {
        if (bounds.IsEmpty || !header.IsHandleCreated) return Rectangle.Empty;
        return new Rectangle(form.PointToClient(header.PointToScreen(bounds.Location)), bounds.Size);
    }

    private DesktopShell(Form form, string? title)
    {
        this.form = form;
        followsTitle = title is null;
        resizable = form.FormBorderStyle is FormBorderStyle.Sizable or FormBorderStyle.SizableToolWindow;
        Size client = form.ClientSize;
        form.SuspendLayout();
        // Code-built forms have no designer-generated DPI baseline. Keep an
        // explicitly chosen scaling mode, and give inherited/unscaled forms
        // the same 96-DPI layout baseline used by their control dimensions.
        if (form.AutoScaleMode is AutoScaleMode.None or AutoScaleMode.Inherit)
        {
            form.AutoScaleDimensions = new SizeF(96, 96);
            form.AutoScaleMode = AutoScaleMode.Dpi;
        }
        Control[] content = form.Controls.Cast<Control>().ToArray();
        Content = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Theme.Surface };
        header = new ShellHeader(form, title ?? form.Text) { Dock = DockStyle.Fill, Margin = Padding.Empty };
        layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(1),
            ColumnCount = 1, RowCount = 2, BackColor = Theme.Border
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        foreach (Control control in content) { form.Controls.Remove(control); Content.Controls.Add(control); }
        layout.Controls.Add(header, 0, 0); layout.Controls.Add(Content, 0, 1);
        form.FormBorderStyle = FormBorderStyle.None;
        form.Controls.Add(layout); form.ClientSize = client;
        form.ResumeLayout(true);
        native = new ShellWindow(this);
        form.HandleCreated += HandleCreated;
        form.HandleDestroyed += HandleDestroyed;
        form.TextChanged += TitleChanged;
        form.SizeChanged += SizeChanged;
        form.FormClosed += FormClosed;
        form.Disposed += FormDisposed;
        form.DpiChanged += DpiChanged;
        form.Shown += Shown;
        if (form.IsHandleCreated) { native.Connect(form.Handle); ScheduleDpiRefresh(); }
        Application.AddMessageFilter(this);
    }

    public static DesktopShell Attach(Form form, string? title = null)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (Shells.TryGetValue(form, out var existing)) return existing;
        var shell = new DesktopShell(form, title); Shells.Add(form, shell); return shell;
    }

    public static DesktopShell? For(Form form) => Shells.TryGetValue(form, out var shell) ? shell : null;

    internal static Bitmap CapturePreview(Form form)
    {
        if (!form.IsHandleCreated) throw new InvalidOperationException("Window must have a native handle before capture.");
        if (!GetWindowRect(form.Handle, out var bounds)) throw new InvalidOperationException("Cannot read native window bounds.");
        var bitmap = new Bitmap(Math.Max(1, bounds.Right - bounds.Left), Math.Max(1, bounds.Bottom - bounds.Top));
        try
        {
            using var graphics = Graphics.FromImage(bitmap); IntPtr dc = graphics.GetHdc();
            try { if (!PrintWindow(form.Handle, dc, 2)) throw new InvalidOperationException("Native whole-window capture failed."); }
            finally { graphics.ReleaseHdc(dc); }
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    internal static string VerifyForPreview(Form form, string? capturePath = null)
    {
        var shell = For(form) ?? throw new InvalidOperationException("Window shell was not attached.");
        if (!form.IsHandleCreated || !form.Visible) throw new InvalidOperationException("Window must be shown for native frame verification.");
        FormWindowState oldState = form.WindowState;
        Rectangle oldBounds = oldState == FormWindowState.Normal ? form.Bounds : form.RestoreBounds;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        void SetState(FormWindowState state) { form.WindowState = state; Application.DoEvents(); }
        try
        {
            SetState(FormWindowState.Normal); form.Bounds = oldBounds; Application.DoEvents();
            Check(form.FormBorderStyle == FormBorderStyle.None, "Native caption was not removed.");
            Check((GetWindowLongPtr(form.Handle, -16).ToInt64() & 0x00C00000L) == 0, "Native caption style was restored.");
            Check(GetWindowRect(form.Handle, out var windowRect), "Cannot inspect native window geometry.");
            Check(GetClientRect(form.Handle, out var clientRect), "Cannot inspect native client geometry.");
            var clientOrigin = new NativePoint(); Check(ClientToScreen(form.Handle, ref clientOrigin), "Cannot inspect client origin.");
            Check(clientOrigin.X == windowRect.Left && clientOrigin.Y == windowRect.Top && clientRect.Right == windowRect.Right - windowRect.Left && clientRect.Bottom == windowRect.Bottom - windowRect.Top,
                "An extra native non-client frame or caption remains.");
            using (var capture = CapturePreview(form))
            {
                int y = Math.Clamp(shell.Header.Top + 4, 0, capture.Height - 1);
                foreach (float fraction in new[] { .45f, .55f, .65f })
                {
                    Color pixel = capture.GetPixel(Math.Clamp((int)(capture.Width * fraction), 0, capture.Width - 1), y);
                    Check(Math.Abs(pixel.R - Theme.Background.R) + Math.Abs(pixel.G - Theme.Background.G) + Math.Abs(pixel.B - Theme.Background.B) <= 9,
                        "Whole-window capture contains native caption paint above the custom header.");
                }
                if (shell.header.HasBranding)
                {
                    Rectangle icon = shell.BrandingIconBounds;
                    Rectangle title = shell.HeaderBoundsToForm(shell.header.TitleTextBounds);
                    Point buttons = form.PointToClient(shell.header.PointToScreen(new Point(shell.header.ButtonsLeft, 0)));
                    int expectedSize = Math.Max(1, (int)Math.Round(20f * shell.header.DeviceDpi / 96f));
                    int expectedGap = Math.Max(1, (int)Math.Round(6f * shell.header.DeviceDpi / 96f));
                    Check(!icon.IsEmpty && icon.Width == expectedSize && icon.Height == expectedSize,
                        "Main title branding icon is missing or does not follow DPI.");
                    Check(icon.Left == title.Right + expectedGap && icon.Right < buttons.X,
                        "Branding icon does not follow the title or overlaps window buttons.");
                    Check(new Rectangle(Point.Empty, capture.Size).Contains(icon), "Branding icon is outside the captured window.");
                    RectangleF glyphBounds = shell.BrandingGlyphDrawBounds;
                    Rectangle source = Branding.HeaderIconSourceBounds;
                    Check(!glyphBounds.IsEmpty && ((RectangleF)icon).Contains(glyphBounds), "Header mark does not fit inside its icon slot.");
                    Check(source.Width > 0 && source.Height > 0 && Math.Abs(glyphBounds.Width / glyphBounds.Height - (float)source.Width / source.Height) < .005f,
                        "Header mark lost its source aspect ratio.");
                    Check(Math.Abs(glyphBounds.X + glyphBounds.Width / 2 - (icon.X + icon.Width / 2f)) < .01f &&
                        Math.Abs(glyphBounds.Y + glyphBounds.Height / 2 - (icon.Y + icon.Height / 2f)) < .01f,
                        "Header mark is not centered in its icon slot.");
                    int line = 0, background = 0;
                    for (int iy = icon.Top; iy < icon.Bottom; iy++)
                        for (int ix = icon.Left; ix < icon.Right; ix++)
                        {
                            Color pixel = capture.GetPixel(ix, iy);
                            int delta = Math.Abs(pixel.R - Theme.Background.R) + Math.Abs(pixel.G - Theme.Background.G) + Math.Abs(pixel.B - Theme.Background.B);
                            if (delta <= 9) background++;
                            bool neutral = Math.Max(pixel.R, Math.Max(pixel.G, pixel.B)) - Math.Min(pixel.R, Math.Min(pixel.G, pixel.B)) <= 12;
                            bool expectedLine = Theme.IsDark
                                ? pixel.R > Theme.Background.R + 45 && pixel.G > Theme.Background.G + 45 && pixel.B > Theme.Background.B + 45
                                : pixel.R < Theme.Background.R - 45 && pixel.G < Theme.Background.G - 45 && pixel.B < Theme.Background.B - 45;
                            if (neutral && expectedLine) line++;
                        }
                    Check(line > 0 && background > 0, "Header mark does not contain its theme's contrasting line and transparent background.");
                    foreach (Point corner in new[] { icon.Location, new Point(icon.Right - 1, icon.Top), new Point(icon.Left, icon.Bottom - 1), new Point(icon.Right - 1, icon.Bottom - 1) })
                    {
                        Color pixel = capture.GetPixel(corner.X, corner.Y);
                        Check(Math.Abs(pixel.R - Theme.Background.R) + Math.Abs(pixel.G - Theme.Background.G) + Math.Abs(pixel.B - Theme.Background.B) <= 9,
                            "Header mark has an opaque backing plate at its corners.");
                    }
                }
                else Check(shell.BrandingIconBounds.IsEmpty && shell.BrandingGlyphDrawBounds.IsEmpty, "Non-brand title contains an unexpected branding icon.");
                if (capturePath is not null) capture.Save(capturePath);
            }
            Check(shell.Content.Top >= shell.Header.Bottom && shell.Content.Bottom <= form.ClientSize.Height,
                "Window content overlaps its title bar or frame.");
            if (shell.resizable)
            {
                Point[] points = [new(1, 1), new(form.Width / 2, 1), new(form.Width - 2, 1), new(1, form.Height / 2), new(form.Width - 2, form.Height / 2),
                    new(1, form.Height - 2), new(form.Width / 2, form.Height - 2), new(form.Width - 2, form.Height - 2)];
                int[] expected = [13, 12, 14, 10, 11, 16, 15, 17];
                for (int i = 0; i < points.Length; i++)
                {
                    Point screen = form.PointToScreen(points[i]);
                    long packed = ((long)(ushort)screen.Y << 16) | (ushort)screen.X;
                    Check(SendMessage(form.Handle, 0x84, IntPtr.Zero, (IntPtr)packed).ToInt32() == expected[i], "A native resize edge is unavailable.");
                }
            }
            if (form.MaximizeBox)
            {
                SetState(FormWindowState.Maximized);
                Check(form.Bounds == Screen.FromControl(form).WorkingArea, "Maximize does not match the monitor working area.");
                Check(shell.ResizeHitTest(form.PointToScreen(new Point(1, 1))) == 1, "Maximized window is still resizable.");
                SetState(FormWindowState.Normal); Check(form.Bounds == oldBounds, "Maximize restore changed window bounds.");
            }
            if (form.MinimizeBox)
            {
                SetState(FormWindowState.Minimized); Check(form.WindowState == FormWindowState.Minimized, "Minimize failed.");
                SetState(FormWindowState.Normal); Check(form.Bounds == oldBounds, "Minimize restore changed window bounds.");
            }
            return "PASS: whole-window PrintWindow capture and native geometry contain no extra caption; transparent header mark follows title, preserves aspect ratio and avoids buttons; custom title bar; eight native resize edges; maximize uses monitor working area; minimize and maximize restore bounds.\n";
        }
        finally
        {
            SetState(FormWindowState.Normal); form.Bounds = oldBounds; SetState(oldState);
        }
    }

    private void HandleCreated(object? sender, EventArgs e) { native.Connect(form.Handle); ScheduleDpiRefresh(); }
    private void HandleDestroyed(object? sender, EventArgs e) => native.Disconnect();
    private void TitleChanged(object? sender, EventArgs e) { if (followsTitle) header.Title = form.Text; }
    private void SizeChanged(object? sender, EventArgs e) => header.Invalidate(true);
    private void FormClosed(object? sender, FormClosedEventArgs e) => Dispose();
    private void FormDisposed(object? sender, EventArgs e) => Dispose();
    private void Shown(object? sender, EventArgs e) => ScheduleDpiRefresh();
    private void DpiChanged(object? sender, DpiChangedEventArgs e) => ScheduleDpiRefresh();
    private void ScheduleDpiRefresh()
    {
        if (disposed || !form.IsHandleCreated || form.IsDisposed) return;
        // WinForms finishes its automatic control scaling after raising the
        // DPI event. Set absolute shell sizes on the next UI turn to avoid
        // scaling the title row twice while moving between monitors.
        form.BeginInvoke(() =>
        {
            if (disposed || form.IsDisposed) return;
            int dpi = form.DeviceDpi;
            layout.RowStyles[0].Height = 42f * dpi / 96f;
            layout.Padding = new Padding(Math.Max(1, (int)Math.Round(dpi / 96f)));
            foreach (Control button in header.Controls) button.Width = (int)Math.Round(46f * dpi / 96f);
            header.Invalidate(true);
        });
    }

    // Also used by child controls at the edge: the border remains draggable even
    // when a Dock.Fill content panel has its own native handle.
    internal int ResizeHitTest(Point screenPoint)
    {
        if (disposed || form.WindowState != FormWindowState.Normal || !resizable) return 1;
        var point = form.PointToClient(screenPoint);
        if (!form.ClientRectangle.Contains(point)) return 1;
        int grip = Math.Max(5, (int)Math.Round(6f * form.DeviceDpi / 96f));
        bool left = point.X < grip, right = point.X >= form.ClientSize.Width - grip;
        bool top = point.Y < grip, bottom = point.Y >= form.ClientSize.Height - grip;
        if (top && left) return 13;
        if (top && right) return 14;
        if (bottom && left) return 16;
        if (bottom && right) return 17;
        if (left) return 10;
        if (right) return 11;
        if (top) return 12;
        if (bottom) return 15;
        return 1;
    }

    public bool PreFilterMessage(ref Message m)
    {
        if (disposed || !form.IsHandleCreated || !form.Visible || m.Msg is not (0x20 or 0x200 or 0x201 or 0x203)) return false;
        if (GetAncestor(m.HWnd, 2) != form.Handle) return false;
        int hit = ResizeHitTest(Cursor.Position);
        if (hit == 1) return false;
        if (m.Msg is 0x20 or 0x200)
        {
            Cursor.Current = hit is 13 or 17 ? Cursors.SizeNWSE : hit is 14 or 16 ? Cursors.SizeNESW : hit is 10 or 11 ? Cursors.SizeWE : Cursors.SizeNS;
            return true;
        }
        ReleaseCapture(); SendMessage(form.Handle, 0xA1, (IntPtr)hit, IntPtr.Zero); return true;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; Application.RemoveMessageFilter(this); native.Disconnect();
        form.HandleCreated -= HandleCreated; form.HandleDestroyed -= HandleDestroyed;
        form.TextChanged -= TitleChanged; form.SizeChanged -= SizeChanged;
        form.FormClosed -= FormClosed; form.DpiChanged -= DpiChanged;
        form.Disposed -= FormDisposed;
        form.Shown -= Shown;
        Shells.Remove(form);
    }

    private sealed class ShellWindow(DesktopShell owner) : NativeWindow
    {
        public void Connect(IntPtr handle)
        {
            if (Handle == handle) return;
            Disconnect(); AssignHandle(handle);
            // A resize frame can make DWM/DefWindowProc paint a cached native
            // caption even though NCCALCSIZE returned the full client area.
            // The shell owns that entire area, including its active state.
            int disabled = 1; _ = DwmSetWindowAttribute(handle, 2, ref disabled, sizeof(int));
            long style = GetWindowLongPtr(handle, -16).ToInt64();
            style &= ~0x00C00000L; // Caption is painted inside the client area.
            style |= 0x00080000L; // Keep Alt+Space and normal window commands.
            if (owner.form.MinimizeBox) style |= 0x00020000L;
            if (owner.form.MaximizeBox) style |= 0x00010000L;
            if (owner.resizable) style |= 0x00040000L;
            _ = SetWindowLongPtr(handle, -16, (IntPtr)style);
            _ = SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, 0x0027);
        }
        public void Disconnect() { if (Handle != IntPtr.Zero) ReleaseHandle(); }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x83) { m.Result = IntPtr.Zero; return; } // Full window is client space.
            if (m.Msg == 0x85) { m.Result = IntPtr.Zero; return; } // Native non-client paint must not cover the header.
            if (m.Msg == 0x86) { m.Result = (IntPtr)1; return; } // Accept activation without drawing a native caption.
            if (m.Msg == 0x7C && m.WParam.ToInt64() == -16)
            {
                base.WndProc(ref m);
                // Form.Text, WindowState and handle recreation can ask WinForms
                // to update native styles. Never let that restore WS_CAPTION.
                var styles = Marshal.PtrToStructure<StyleStruct>(m.LParam);
                styles.NewStyle &= ~0x00C00000U;
                Marshal.StructureToPtr(styles, m.LParam, false); return;
            }
            if (m.Msg == 0x31E)
            {
                int disabled = 1; _ = DwmSetWindowAttribute(Handle, 2, ref disabled, sizeof(int));
            }
            if (m.Msg == 0x84)
            {
                long location = m.LParam.ToInt64();
                var point = new Point(unchecked((short)(location & 0xFFFF)), unchecked((short)((location >> 16) & 0xFFFF)));
                int hit = owner.ResizeHitTest(point);
                if (hit != 1) { m.Result = (IntPtr)hit; return; }
            }
            if (m.Msg == 0x24)
            {
                base.WndProc(ref m);
                var info = Marshal.PtrToStructure<MinMaxInfo>(m.LParam);
                var screen = Screen.FromHandle(Handle);
                info.MaxPosition = new(screen.WorkingArea.X - screen.Bounds.X, screen.WorkingArea.Y - screen.Bounds.Y);
                info.MaxSize = new(screen.WorkingArea.Width, screen.WorkingArea.Height);
                info.MinTrackSize = new(owner.form.MinimumSize.Width, owner.form.MinimumSize.Height);
                if (!owner.form.MaximumSize.IsEmpty) info.MaxTrackSize = new(owner.form.MaximumSize.Width, owner.form.MaximumSize.Height);
                Marshal.StructureToPtr(info, m.LParam, false); m.Result = IntPtr.Zero; return;
            }
            base.WndProc(ref m);
        }
    }

    private sealed class ShellHeader : Panel
    {
        private readonly Form form;
        private readonly WindowButton maximize;
        private string title;
        public string Title { get => title; set { title = value; Invalidate(); } }
        internal bool HasBranding => title.Contains("Kotoshiro", StringComparison.OrdinalIgnoreCase);
        internal Rectangle BrandingIconBounds { get; private set; }
        internal RectangleF BrandingGlyphDrawBounds { get; private set; }
        internal Rectangle TitleTextBounds { get; private set; }
        internal int ButtonsLeft => Controls.Cast<Control>().Where(x => x.Visible).Select(x => x.Left).DefaultIfEmpty(Width).Min();
        public ShellHeader(Form form, string title)
        {
            this.form = form; this.title = title;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Background;
            var close = new WindowButton(form, WindowAction.Close) { Dock = DockStyle.Right, AccessibleName = "关闭窗口" };
            maximize = new WindowButton(form, WindowAction.Maximize) { Dock = DockStyle.Right, AccessibleName = "最大化或还原窗口", Visible = form.MaximizeBox };
            var minimize = new WindowButton(form, WindowAction.Minimize) { Dock = DockStyle.Right, AccessibleName = "最小化窗口", Visible = form.MinimizeBox };
            Controls.Add(minimize); Controls.Add(maximize); Controls.Add(close);
            MouseDown += (_, e) =>
            {
                if (e.Button != MouseButtons.Left || e.Clicks > 1) return;
                ReleaseCapture(); SendMessage(form.Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
            };
            MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left && form.MaximizeBox) ToggleMaximize(); };
        }
        private void ToggleMaximize() => form.WindowState = form.WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Background);
            int padding = Math.Max(12, 15 * DeviceDpi / 96);
            int available = Math.Max(0, ButtonsLeft - padding * 2);
            int iconSize = Math.Max(1, (int)Math.Round(20f * DeviceDpi / 96f));
            int gap = Math.Max(1, (int)Math.Round(6f * DeviceDpi / 96f));
            bool showBranding = HasBranding && available > iconSize + gap;
            int textLimit = Math.Max(0, available - (showBranding ? iconSize + gap : 0));
            var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            int naturalWidth = TextRenderer.MeasureText(e.Graphics, title, Font, new Size(int.MaxValue, Height), flags).Width;
            TitleTextBounds = new Rectangle(padding, 0, Math.Min(naturalWidth, textLimit), Math.Max(0, Height - 1));
            TextRenderer.DrawText(e.Graphics, title, Font, TitleTextBounds, Theme.Muted, flags);
            BrandingIconBounds = showBranding
                ? new Rectangle(TitleTextBounds.Right + gap, (Height - iconSize) / 2, iconSize, iconSize)
                : Rectangle.Empty;
            BrandingGlyphDrawBounds = RectangleF.Empty;
            if (!BrandingIconBounds.IsEmpty)
            {
                Rectangle source = Branding.HeaderIconSourceBounds;
                if (source.Width <= 0 || source.Height <= 0) throw new InvalidOperationException("Header mark has no visible source bounds.");
                float scale = Math.Min((float)BrandingIconBounds.Width / source.Width, (float)BrandingIconBounds.Height / source.Height);
                float width = source.Width * scale, height = source.Height * scale;
                BrandingGlyphDrawBounds = new RectangleF(BrandingIconBounds.Left + (BrandingIconBounds.Width - width) / 2,
                    BrandingIconBounds.Top + (BrandingIconBounds.Height - height) / 2, width, height);
                var state = e.Graphics.Save();
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                e.Graphics.DrawImage(Branding.HeaderIcon, BrandingGlyphDrawBounds, source, GraphicsUnit.Pixel);
                e.Graphics.Restore(state);
            }
            using var line = new Pen(Theme.Border); e.Graphics.DrawLine(line, 0, Height - 1, Width, Height - 1);
        }
    }

    private enum WindowAction { Minimize, Maximize, Close }
    private sealed class WindowButton : Button
    {
        private readonly Form form;
        private readonly WindowAction action;
        private bool hovered;
        public WindowButton(Form form, WindowAction action)
        {
            this.form = form; this.action = action;
            Width = 46; FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; TabStop = false;
            BackColor = Theme.Background; ForeColor = Theme.Text;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Click += (_, _) =>
            {
                if (action == WindowAction.Close) form.Close();
                else if (action == WindowAction.Minimize) form.WindowState = FormWindowState.Minimized;
                else form.WindowState = form.WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
            };
        }
        protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(hovered ? action == WindowAction.Close ? Color.FromArgb(196, 43, 48) : Theme.Selection : Theme.Background);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(hovered && action == WindowAction.Close ? Color.White : Theme.Text, Math.Max(1f, DeviceDpi / 96f));
            float size = 10f * DeviceDpi / 96f, x = (Width - size) / 2, y = (Height - size) / 2;
            if (action == WindowAction.Minimize) e.Graphics.DrawLine(pen, x, y + size / 2, x + size, y + size / 2);
            else if (action == WindowAction.Close) { e.Graphics.DrawLine(pen, x, y, x + size, y + size); e.Graphics.DrawLine(pen, x + size, y, x, y + size); }
            else if (form.WindowState == FormWindowState.Maximized)
            {
                e.Graphics.DrawRectangle(pen, x + 2, y, size - 2, size - 2);
                using var fill = new SolidBrush(hovered ? Theme.Selection : Theme.Background); e.Graphics.FillRectangle(fill, x, y + 2, size - 2, size - 2);
                e.Graphics.DrawRectangle(pen, x, y + 2, size - 2, size - 2);
            }
            else e.Graphics.DrawRectangle(pen, x, y, size, size);
            using var separator = new Pen(Theme.Border); e.Graphics.DrawLine(separator, 0, Height - 1, Width, Height - 1);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; public NativePoint(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)] private struct StyleStruct { public uint OldStyle, NewStyle; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr handle, uint flags);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out NativeRect bounds);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr handle, out NativeRect bounds);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr handle, ref NativePoint point);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr handle, IntPtr dc, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
}

public enum ShellIcon { None, Home, Live, History, Video, Settings, Usage, Captions, Player, Translation, Microphone, Appearance }

/// <summary>Quiet navigation row with an original line icon and a neutral selection pill.</summary>
public sealed class SidebarButton : Button
{
    private bool selected, hovered;
    public ShellIcon Icon { get; set; }
    public bool Selected { get => selected; set { if (selected == value) return; selected = value; Invalidate(); } }
    public SidebarButton(string text, ShellIcon icon = ShellIcon.None)
    {
        Text = text; Icon = icon; AutoSize = false; Height = 42;
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; BackColor = Theme.Background; ForeColor = Theme.Text;
        Margin = new Padding(0, 0, 0, 3); Padding = new Padding(12, 0, 10, 0); Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Background); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (Selected || hovered)
        {
            using var path = Theme.RoundedPath(new RectangleF(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1)), 8);
            using var fill = new SolidBrush(Selected ? Theme.Selection : Theme.SurfaceRaised); e.Graphics.FillPath(fill, path);
        }
        float scale = DeviceDpi / 96f;
        Color color = Enabled ? Theme.Text : Theme.Muted;
        int textX = (int)Math.Round(12 * scale);
        if (Icon != ShellIcon.None)
        {
            DrawIcon(e.Graphics, Icon, new RectangleF(12 * scale, (Height - 18 * scale) / 2, 18 * scale, 18 * scale), color);
            textX = (int)Math.Round(40 * scale);
        }
        TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(textX, 0, Math.Max(0, Width - textX - 10), Height), color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4), color, Theme.Background);
    }

    internal static void DrawIcon(Graphics graphics, ShellIcon icon, RectangleF bounds, Color color)
    {
        var state = graphics.Save(); graphics.TranslateTransform(bounds.X, bounds.Y); graphics.ScaleTransform(bounds.Width / 18, bounds.Height / 18);
        using var pen = new Pen(color, 1.3f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        if (icon is ShellIcon.Video or ShellIcon.Player)
        {
            graphics.DrawRectangle(pen, 1.5f, 3, 15, 12);
            graphics.DrawPolygon(pen, new[] { new PointF(7, 6), new PointF(12, 9), new PointF(7, 12) });
        }
        else if (icon is ShellIcon.Live or ShellIcon.Microphone)
        {
            using var path = Theme.RoundedPath(new RectangleF(6, 1, 6, 10), 3); graphics.DrawPath(pen, path);
            graphics.DrawArc(pen, 3.5f, 5, 11, 9, 0, 180); graphics.DrawLine(pen, 9, 14, 9, 17); graphics.DrawLine(pen, 6, 17, 12, 17);
        }
        else if (icon is ShellIcon.History or ShellIcon.Usage)
        {
            graphics.DrawArc(pen, 2, 2, 14, 14, 215, 300); graphics.DrawLines(pen, new[] { new PointF(1, 2), new PointF(1, 6), new PointF(5, 6) });
            graphics.DrawLines(pen, new[] { new PointF(9, 5), new PointF(9, 9), new PointF(12, 10) });
        }
        else if (icon == ShellIcon.Settings)
        {
            graphics.DrawLine(pen, 2, 4, 16, 4); graphics.DrawLine(pen, 2, 9, 16, 9); graphics.DrawLine(pen, 2, 14, 16, 14);
            using var fill = new SolidBrush(Theme.Background); graphics.FillEllipse(fill, 5, 2, 4, 4); graphics.DrawEllipse(pen, 5, 2, 4, 4);
            graphics.FillEllipse(fill, 10, 7, 4, 4); graphics.DrawEllipse(pen, 10, 7, 4, 4); graphics.FillEllipse(fill, 4, 12, 4, 4); graphics.DrawEllipse(pen, 4, 12, 4, 4);
        }
        else if (icon == ShellIcon.Appearance)
        {
            graphics.DrawEllipse(pen, 2, 2, 14, 14);
            using var fill = new SolidBrush(color); graphics.FillPie(fill, 4, 4, 10, 10, 90, 180);
        }
        else if (icon == ShellIcon.Home)
        {
            graphics.DrawLines(pen, new[] { new PointF(1.5f, 8), new PointF(9, 2), new PointF(16.5f, 8) });
            graphics.DrawLines(pen, new[] { new PointF(4, 7), new PointF(4, 16), new PointF(14, 16), new PointF(14, 7) });
        }
        else
        {
            using var path = Theme.RoundedPath(new RectangleF(1.5f, 2.5f, 15, 12), 3); graphics.DrawPath(pen, path);
            graphics.DrawLine(pen, 4.5f, 6, 13.5f, 6); graphics.DrawLine(pen, 4.5f, 10, 10.5f, 10);
        }
        graphics.Restore(state);
    }
}
