using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Kotoshiro;

public sealed class MainForm : Form
{
    private AppSettings settings;
    private readonly SubtitleOverlay overlay;
    private readonly ComboBox devices = Theme.Combo();
    private readonly Button start = Theme.Button("开始翻译", true), setup = new SidebarButton("设置", ShellIcon.Settings), refresh = Theme.Button("刷新设备");
    private readonly Label status = Theme.Label("就绪 · 首次使用请打开设置，选择翻译服务与语音识别方式。", true);
    private readonly Label service = Theme.Label("", true);
    private readonly Button usage = new SidebarButton("订阅用量", ShellIcon.Usage);
    private readonly Button biliButton = new SidebarButton("延时翻译", ShellIcon.Video);
    private readonly Button subtitleToggle = Theme.Button("显示字幕");
    private readonly Button translateNav = new SidebarButton("实时翻译", ShellIcon.Live), historyNav = new SidebarButton("翻译记录", ShellIcon.History);
    private readonly Label workspaceTitle = Theme.Label("实时翻译");
    private readonly Panel workspace = new(), readingHost = new(), readingBody = new(), composerHost = new(), operationHost = new(), delayedHost = new();
    private readonly TableLayoutPanel mainLayout = new();
    private readonly TextBox delayedRoom = Theme.Input();
    private readonly Button delayedStart = Theme.Button("开始延时翻译", true);
    private readonly Label delayedStatus = Theme.Label("Enter 打开延时播放器", true);
    private readonly CheckBox through = new() { Text = "鼠标穿透", AutoSize = true };
    private readonly ToolTip details = new() { InitialDelay = 350, ReshowDelay = 100, AutoPopDelay = 12000, ShowAlways = true };
    private enum MainPage { Realtime, Delayed, Records }
    private MainPage currentPage;
    private Action? fitWorkspace;
    private BilibiliPlayerForm? biliPlayer;
    private SettingsForm? settingsEditor;
    private readonly Label timing = Theme.Label("耗时：开始翻译后显示采集段、排队、识别、翻译和字幕更新的时间。", true);
    private readonly AudioLevelMeter meter = new() { Dock = DockStyle.Fill, Height = 8, Maximum = 100 };
    private readonly RichTextBox history = new() { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Theme.Surface, ForeColor = Theme.Text };
    private CancellationTokenSource? session;
    private Task? pipeline;
    private LiveAudioCapture? capture;
    private IRecognitionEngine? engine;
    private bool busy, closing;
    private long levelUpdate;
    private bool hotkeyRegistered;
    internal string SelectedDeviceName => (devices.SelectedItem as AudioDevice)?.Name ?? "";
    public MainForm(AppSettings settings)
    {
        this.settings = settings; Theme.Use(settings.UiTheme); ApiProviders.Normalize(this.settings); SettingsStore.SetModelDirectory(settings.ModelFolder); Theme.Apply(this); Text = "言代Kotoshiro · 日语直播中文字幕";
        ClientSize = new Size(1120, 800); MinimumSize = new Size(920, 680); StartPosition = FormStartPosition.CenterScreen;
        overlay = new(settings);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = Padding.Empty, Margin = Padding.Empty, ColumnCount = 2, RowCount = 1, BackColor = Theme.Surface };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 208)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var sidebar = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14, 22, 14, 14), Margin = Padding.Empty, ColumnCount = 1, RowCount = 5, BackColor = Theme.Background };
        sidebar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 70)); sidebar.RowStyles.Add(new RowStyle(SizeType.AutoSize)); sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); sidebar.RowStyles.Add(new RowStyle(SizeType.AutoSize)); sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        var identity = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Margin = Padding.Empty };
        var title = Theme.Label("言代Kotoshiro"); title.Font = new Font("Microsoft YaHei UI", 16, FontStyle.Bold); title.Location = new Point(8, 0); title.Margin = Padding.Empty;
        var description = Theme.Label("日语直播 · 中文字幕", true); description.Font = new Font("Microsoft YaHei UI", 9); description.Location = new Point(9, 35); description.Margin = Padding.Empty;
        identity.Controls.Add(title); identity.Controls.Add(description); sidebar.Controls.Add(identity, 0, 0);
        var navigation = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty, BackColor = Theme.Background };
        var accountNavigation = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty, BackColor = Theme.Background };
        void NavigationButton(Button button)
        {
            button.AutoSize = false; button.Size = new Size(180, 43); button.MinimumSize = new Size(0, 43); button.Margin = new Padding(0, 2, 0, 2); button.Padding = new Padding(12, 0, 8, 0); button.TextAlign = ContentAlignment.MiddleLeft;
            button.Font = new Font("Microsoft YaHei UI", 10); button.BackColor = Theme.Background;
        }
        foreach (var button in new[] { translateNav, historyNav, biliButton, setup, usage }) NavigationButton(button);
        navigation.Controls.Add(translateNav); navigation.Controls.Add(biliButton); navigation.Controls.Add(historyNav);
        sidebar.Controls.Add(navigation, 0, 1); accountNavigation.Controls.Add(setup); accountNavigation.Controls.Add(usage); sidebar.Controls.Add(accountNavigation, 0, 3);
        var sidebarFooter = Theme.Label("日语 → 中文", true); sidebarFooter.Font = new Font("Microsoft YaHei UI", 9); sidebarFooter.Margin = new Padding(12, 12, 0, 0); sidebar.Controls.Add(sidebarFooter, 0, 4);
        root.Controls.Add(sidebar, 0, 0);

        workspace.Dock = DockStyle.Fill; workspace.Margin = Padding.Empty; workspace.BackColor = Theme.Surface;
        mainLayout.Dock = DockStyle.Fill; mainLayout.Padding = Padding.Empty; mainLayout.Margin = Padding.Empty; mainLayout.ColumnCount = 1; mainLayout.RowCount = 3; mainLayout.BackColor = Theme.Surface;
        mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 79)); mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 245));
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(30, 17, 30, 10), Margin = Padding.Empty, ColumnCount = 1, RowCount = 2, BackColor = Theme.Surface };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.RowStyles.Add(new RowStyle(SizeType.Absolute, 26)); header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        workspaceTitle.Font = new Font("Microsoft YaHei UI", 12, FontStyle.Bold); workspaceTitle.Margin = Padding.Empty;
        service.Font = new Font("Microsoft YaHei UI", 8.5f); service.Margin = Padding.Empty; service.AutoSize = false; service.Dock = DockStyle.Fill; service.AutoEllipsis = true;
        header.Controls.Add(workspaceTitle, 0, 0); header.Controls.Add(service, 0, 1);
        header.Paint += (_, e) => { using var pen = new Pen(Theme.Border); e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1); };
        mainLayout.Controls.Add(header, 0, 0);

        readingHost.Dock = DockStyle.Fill; readingHost.BackColor = Theme.Surface; readingHost.Margin = Padding.Empty;
        readingBody.BackColor = Theme.Surface; readingHost.Controls.Add(readingBody);
        history.Margin = Padding.Empty; history.Font = new Font("Microsoft YaHei UI", 11); history.HideSelection = false; history.ScrollBars = RichTextBoxScrollBars.Vertical; history.WordWrap = true;
        readingBody.Controls.Add(history); history.Visible = false;
        var recordMenu = new ContextMenuStrip(); recordMenu.Items.Add("复制选中内容", null, (_, _) => { if (history.SelectionLength > 0) history.Copy(); }); recordMenu.Items.Add("复制全部记录", null, (_, _) => { if (history.TextLength > 0) Clipboard.SetText(history.Text); }); recordMenu.Items.Add("查看最新记录", null, (_, _) => { history.SelectionStart = history.TextLength; history.SelectionLength = 0; history.ScrollToCaret(); }); history.ContextMenuStrip = recordMenu;
        history.TextChanged += (_, _) => history.Visible = history.TextLength > 0 && currentPage == MainPage.Records;
        mainLayout.Controls.Add(readingHost, 0, 1);

        operationHost.Dock = DockStyle.Fill; operationHost.BackColor = Theme.Surface; operationHost.Margin = Padding.Empty;
        composerHost.Dock = DockStyle.Fill; composerHost.BackColor = Theme.Surface; composerHost.Margin = Padding.Empty; operationHost.Controls.Add(composerHost);
        var composer = Theme.Card(24); composer.Padding = new Padding(18, 14, 18, 14); composer.Margin = Padding.Empty; composerHost.Controls.Add(composer);
        var input = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Theme.Surface, Margin = Padding.Empty, Padding = Padding.Empty };
        input.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); input.RowStyles.Add(new RowStyle(SizeType.Absolute, 25)); input.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); input.RowStyles.Add(new RowStyle(SizeType.Absolute, 49));
        var deviceTitle = Theme.Label("声音来源", true); deviceTitle.Font = new Font("Microsoft YaHei UI", 9); deviceTitle.Margin = Padding.Empty; input.Controls.Add(deviceTitle, 0, 0);
        var deviceRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Surface, Margin = Padding.Empty };
        deviceRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); deviceRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
        devices.Margin = new Padding(0, 0, 10, 4); devices.Font = new Font("Microsoft YaHei UI", 9); deviceRow.Controls.Add(devices, 0, 0);
        refresh.Text = "刷新"; refresh.AutoSize = false; refresh.MinimumSize = Size.Empty; refresh.Size = new Size(82, 33); refresh.Font = new Font("Microsoft YaHei UI", 9); refresh.Padding = Padding.Empty; refresh.Margin = Padding.Empty; deviceRow.Controls.Add(refresh, 1, 0); input.Controls.Add(deviceRow, 0, 1);
        var actionRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Surface, Margin = Padding.Empty };
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
        var subtitleActions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = false, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Surface, Margin = Padding.Empty, Padding = new Padding(0, 7, 0, 0) };
        subtitleToggle.AutoSize = false; subtitleToggle.MinimumSize = Size.Empty; subtitleToggle.Size = new Size(99, 34); subtitleToggle.Font = new Font("Microsoft YaHei UI", 9); subtitleToggle.Padding = Padding.Empty; subtitleToggle.Margin = new Padding(0, 0, 12, 0);
        through.Font = new Font("Microsoft YaHei UI", 9); through.ForeColor = Theme.Text; through.Margin = new Padding(0, 7, 0, 0); through.CheckedChanged += (_, _) => overlay.SetPassThrough(through.Checked);
        subtitleActions.Controls.Add(subtitleToggle); subtitleActions.Controls.Add(through); actionRow.Controls.Add(subtitleActions, 0, 0);
        start.AutoSize = false; start.MinimumSize = Size.Empty; start.Size = new Size(132, 42); start.Font = new Font("Microsoft YaHei UI", 10); start.Padding = Padding.Empty; start.Margin = new Padding(0, 7, 0, 0); actionRow.Controls.Add(start, 1, 0);
        input.Controls.Add(actionRow, 0, 2); composer.Controls.Add(input);
        var activity = new TableLayoutPanel { ColumnCount = 2, RowCount = 3, BackColor = Theme.Surface, Margin = Padding.Empty, Padding = Padding.Empty };
        activity.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 14)); activity.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); activity.RowStyles.Add(new RowStyle(SizeType.Absolute, 20)); activity.RowStyles.Add(new RowStyle(SizeType.Absolute, 6)); activity.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var signal = Theme.Label("●"); signal.ForeColor = Theme.Accent; signal.Font = new Font("Microsoft YaHei UI", 7); signal.Margin = new Padding(0, 4, 0, 0); activity.Controls.Add(signal, 0, 0);
        status.AutoSize = false; status.Dock = DockStyle.Fill; status.AutoEllipsis = true; status.Font = new Font("Microsoft YaHei UI", 8.5f); status.ForeColor = Theme.Muted; status.Margin = Padding.Empty; status.Text = "就绪 · 选择声音来源，开始翻译。"; activity.Controls.Add(status, 1, 0);
        meter.ForeColor = Theme.Accent; meter.BackColor = Theme.Background; meter.Margin = new Padding(0, 0, 0, 3); activity.Controls.Add(meter, 0, 1); activity.SetColumnSpan(meter, 2);
        timing.AutoSize = false; timing.Dock = DockStyle.Fill; timing.AutoEllipsis = true; timing.Text = "Ctrl + Alt + T 开始 / 停止翻译 · 采集所选设备播放的声音"; timing.Font = new Font("Microsoft YaHei UI", 8); timing.Margin = new Padding(0, 3, 0, 0); activity.Controls.Add(timing, 0, 2); activity.SetColumnSpan(timing, 2); composerHost.Controls.Add(activity);
        delayedHost.Dock = DockStyle.Fill; delayedHost.BackColor = Theme.Surface; delayedHost.Margin = Padding.Empty; operationHost.Controls.Add(delayedHost);
        var delayedComposer = Theme.Card(24); delayedComposer.Padding = new Padding(18, 14, 18, 14); delayedComposer.Margin = Padding.Empty; delayedHost.Controls.Add(delayedComposer);
        var delayedInput = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Theme.Surface, Margin = Padding.Empty };
        delayedInput.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); delayedInput.RowStyles.Add(new RowStyle(SizeType.Absolute, 25)); delayedInput.RowStyles.Add(new RowStyle(SizeType.Absolute, 40)); delayedInput.RowStyles.Add(new RowStyle(SizeType.Absolute, 49));
        var roomTitle = Theme.Label("B 站直播间", true); roomTitle.Font = new Font("Microsoft YaHei UI", 9); roomTitle.Margin = Padding.Empty; delayedInput.Controls.Add(roomTitle, 0, 0);
        delayedRoom.Text = settings.BilibiliRoom; delayedRoom.PlaceholderText = "https://live.bilibili.com/22748536 或房间号"; delayedRoom.Font = new Font("Microsoft YaHei UI", 10); delayedRoom.Margin = new Padding(0, 0, 0, 5); delayedInput.Controls.Add(delayedRoom, 0, 1);
        var delayedActions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Surface, Margin = Padding.Empty };
        delayedActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); delayedActions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 172));
        var roomHint = Theme.Label("支持直播间链接或房间号", true); roomHint.AutoSize = false; roomHint.Dock = DockStyle.Fill; roomHint.Font = new Font("Microsoft YaHei UI", 9); roomHint.TextAlign = ContentAlignment.MiddleLeft; roomHint.Margin = Padding.Empty; delayedActions.Controls.Add(roomHint, 0, 0);
        delayedStart.AutoSize = false; delayedStart.MinimumSize = Size.Empty; delayedStart.Size = new Size(172, 42); delayedStart.Font = new Font("Microsoft YaHei UI", 10); delayedStart.Padding = Padding.Empty; delayedStart.Margin = new Padding(0, 7, 0, 0); delayedActions.Controls.Add(delayedStart, 1, 0); delayedInput.Controls.Add(delayedActions, 0, 2); delayedComposer.Controls.Add(delayedInput);
        delayedStatus.AutoSize = false; delayedStatus.AutoEllipsis = true; delayedStatus.Font = new Font("Microsoft YaHei UI", 8.5f); delayedStatus.Margin = Padding.Empty; delayedHost.Controls.Add(delayedStatus);
        mainLayout.Controls.Add(operationHost, 0, 2); workspace.Controls.Add(mainLayout); root.Controls.Add(workspace, 1, 0); Controls.Add(root);
        void FitWorkspace()
        {
            int readWidth = Math.Min(860, Math.Max(200, readingHost.ClientSize.Width - 60));
            readingBody.SetBounds((readingHost.ClientSize.Width - readWidth) / 2, 23, readWidth, Math.Max(0, readingHost.ClientSize.Height - 34));
            // Hidden Dock.Fill pages keep their previous client width until shown.
            // The workspace remains laid out while the records page collapses the bottom row.
            int hostWidth = workspace.ClientSize.Width;
            int composeWidth = Math.Min(860, Math.Max(200, hostWidth - 60)), composeLeft = (hostWidth - composeWidth) / 2;
            composer.SetBounds(composeLeft, 7, composeWidth, 151); activity.SetBounds(composeLeft + 5, 170, Math.Max(100, composeWidth - 10), 65);
            delayedComposer.SetBounds(composeLeft, 7, composeWidth, 151); delayedStatus.SetBounds(composeLeft + 5, 173, Math.Max(100, composeWidth - 10), 50);
            // Repaint old rounded corners after a hidden page or a narrower window is restored.
            composer.Invalidate(true); delayedComposer.Invalidate(true);
        }
        fitWorkspace = FitWorkspace;
        readingHost.SizeChanged += (_, _) => FitWorkspace(); operationHost.SizeChanged += (_, _) => FitWorkspace(); workspace.SizeChanged += (_, _) => FitWorkspace(); SizeChanged += (_, _) => FitWorkspace();
        translateNav.Click += (_, _) => SelectWorkspace(MainPage.Realtime); historyNav.Click += (_, _) => SelectWorkspace(MainPage.Records); biliButton.Click += (_, _) => SelectWorkspace(MainPage.Delayed);
        delayedStart.Click += async (_, _) => await OpenBilibiliPlayerAsync(); delayedRoom.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; delayedStart.PerformClick(); } };
        biliButton.EnabledChanged += (_, _) => SyncDelayedControls(); setup.EnabledChanged += (_, _) => SyncDelayedControls();
        usage.Click += (_, _) => ChatGptPanel.OpenBrowser(new Uri(ChatGptAuth.UsageUrl));
        subtitleToggle.Click += (_, _) => { if (overlay.Visible) overlay.Hide(); else { overlay.Apply(this.settings); overlay.Show(); } };
        overlay.VisibleChanged += (_, _) => { if (!subtitleToggle.IsDisposed) subtitleToggle.Text = overlay.Visible ? "隐藏字幕" : "显示字幕"; };
        foreach (var label in new[] { service, status, timing, delayedStatus })
        {
            details.SetToolTip(label, label.Text); label.TextChanged += (_, _) => details.SetToolTip(label, label.Text);
        }
        details.SetToolTip(refresh, "重新查找扬声器和耳机"); details.SetToolTip(through, "让鼠标操作穿过悬浮字幕，直接控制后面的直播窗口");
        DesktopShell.Attach(this, "言代Kotoshiro");
        Theme.Refresh(this); UpdateService(); SelectWorkspace(MainPage.Realtime); SyncDelayedControls(); FitWorkspace();
        start.Click += async (_, _) => await ToggleAsync(); setup.Click += (_, _) => OpenSettings(); refresh.Click += (_, _) => RefreshDevices();
        RefreshDevices();
        Shown += (_, _) =>
        {
            // RichTextBox only raises TextChanged consistently once its native handle exists.
            // Create it while the empty-state label is visible, before the first caption arrives.
            _ = history.Handle;
            hotkeyRegistered = RegisterHotKey(Handle, 1, 0x4000 | 0x0002 | 0x0001, (uint)Keys.T);
            if (!hotkeyRegistered) status.Text += " 快捷键已被其他程序占用，仍可使用按钮。";
        };
        FormClosing += async (_, e) =>
        {
            if (closing) return;
            e.Cancel = true; closing = true;
            if (settingsEditor is { IsDisposed: false } editor) { editor.DialogResult = DialogResult.Cancel; editor.Close(); }
            await StopAsync(); overlay.RememberBounds(this.settings);
            if (biliPlayer != null) { await biliPlayer.StopAsync(); biliPlayer.Close(); }
            try { SettingsStore.Save(this.settings); } catch { }
            if (hotkeyRegistered) UnregisterHotKey(Handle, 1);
            overlay.Dispose(); Close();
        };
        FormClosed += (_, _) => details.Dispose();
    }
    private void SelectWorkspace(MainPage page)
    {
        currentPage = page;
        ((SidebarButton)translateNav).Selected = page == MainPage.Realtime; ((SidebarButton)biliButton).Selected = page == MainPage.Delayed; ((SidebarButton)historyNav).Selected = page == MainPage.Records;
        workspaceTitle.Text = page switch { MainPage.Delayed => "延时翻译", MainPage.Records => "翻译记录", _ => "实时翻译" };
        operationHost.Visible = page != MainPage.Records; mainLayout.RowStyles[2].Height = page == MainPage.Records ? 0 : 245;
        composerHost.Visible = page == MainPage.Realtime; delayedHost.Visible = page == MainPage.Delayed;
        history.Visible = history.TextLength > 0 && page == MainPage.Records;
        mainLayout.PerformLayout();
        fitWorkspace?.Invoke();
        if (page == MainPage.Records && history.Visible) history.Focus();
        else if (page == MainPage.Realtime && devices.Enabled) devices.Focus();
        else if (page == MainPage.Delayed && delayedRoom.Enabled) delayedRoom.Focus();
    }
    private void SyncDelayedControls()
    {
        bool allowed = biliButton.Enabled && setup.Enabled && settingsEditor is not { IsDisposed: false } && !closing;
        delayedRoom.Enabled = delayedStart.Enabled = allowed;
    }
    private AppSettings PrepareDelayedPlayerSettings()
    {
        long roomId = BilibiliResolver.RoomId(delayedRoom.Text);
        string canonical = "https://live.bilibili.com/" + roomId;
        settings.BilibiliRoom = canonical; SettingsStore.Save(settings); delayedRoom.Text = canonical;
        return settings.Clone();
    }
    private void AppendTranslationRecord(string japanese, string chinese)
    {
        if (IsDisposed || closing || string.IsNullOrWhiteSpace(japanese) || string.IsNullOrWhiteSpace(chinese)) return;
        history.AppendText($"{DateTime.Now:HH:mm:ss}  {japanese}\n{chinese}\n\n");
        if (history.TextLength > 30000) { history.Select(0, history.TextLength - 24000); history.SelectedText = ""; }
        history.SelectionStart = history.TextLength; history.ScrollToCaret();
    }
    private void BindDelayedHistory(BilibiliPlayerForm player)
    {
        void Record(string japanese, string chinese) => Ui(() => AppendTranslationRecord(japanese, chinese));
        player.CaptionTranslated += Record;
        player.FormClosed += (_, _) => player.CaptionTranslated -= Record;
    }
    internal Bitmap CapturePreview()
    {
        fitWorkspace?.Invoke(); Refresh();
        return DesktopShell.CapturePreview(this);
    }
    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
    internal string VerifyMainLayoutForPreview(string? folder = null)
    {
        if (session != null || busy || closing || !Visible || biliPlayer != null) throw new InvalidOperationException("主界面布局检查需要空闲且已显示的窗口。");
        Size originalSize = ClientSize; string originalTheme = Theme.Current, originalRtf = history.Rtf ?? "", originalRoom = settings.BilibiliRoom, originalInput = delayedRoom.Text, originalStatus = status.Text; MainPage originalPage = currentPage; bool originalOverlay = overlay.Visible;
        int selected = history.SelectionStart, selectedLength = history.SelectionLength;
        var checks = new List<string>();
        try
        {
            history.Clear();
            foreach (var (button, page, title) in new[] { (translateNav, MainPage.Realtime, "实时翻译"), (biliButton, MainPage.Delayed, "延时翻译"), (historyNav, MainPage.Records, "翻译记录") })
            {
                button.PerformClick();
                if (currentPage != page || workspaceTitle.Text != title || !((SidebarButton)button).Selected || biliPlayer != null) throw new InvalidOperationException("分类导航未正常切页，或仅点击导航就打开了播放器。");
                if (Descendants(readingBody).Any(x => x is Label or Button) || history.Visible || operationHost.Visible == (page == MainPage.Records)) throw new InvalidOperationException("中央空白区域仍有提示或按钮，或记录页未收起底部操作区。");
            }
            checks.Add("PASS: ordered realtime/delayed/records categories switch pages without launching playback; empty central area contains no labels or quick actions, records page expands reading space.");
            biliButton.PerformClick(); delayedRoom.Text = "https://live.bilibili.com/22748536?from=preview";
            AppSettings prepared = PrepareDelayedPlayerSettings(); AppSettings saved = SettingsStore.Load();
            if (prepared.BilibiliRoom != "https://live.bilibili.com/22748536" || saved.BilibiliRoom != prepared.BilibiliRoom || prepared.PlaybackDelay != settings.PlaybackDelay || prepared.WaitForSubtitles != settings.WaitForSubtitles || prepared.BilibiliQuality != settings.BilibiliQuality || prepared.ModelFolder != settings.ModelFolder) throw new InvalidOperationException("延时入口未保存房间号，或未保留播放器配置。");
            using (var player = new BilibiliPlayerForm(prepared))
            {
                if (!Descendants(player).OfType<TextBox>().Any(x => x.Text == prepared.BilibiliRoom) || player.Running) throw new InvalidOperationException("有效房间地址未传给播放器，或离线检查启动了播放。");
                BindDelayedHistory(player); AppendTranslationRecord("リアルタイムの字幕です。", "实时成功译文");
                player.RecordDelayedCaptionForPreview("遅延再生の字幕です。", "延时成功译文"); player.RecordDelayedCaptionForPreview("", "失败占位", succeeded: false);
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel(); bool lateRejected = false;
                try { player.RecordDelayedCaptionForPreview("遅れた字幕", "迟到译文", cancelled.Token); } catch (OperationCanceledException) { lateRejected = true; }
                Application.DoEvents();
                if (!lateRejected || !history.Text.Contains("实时成功译文") || !history.Text.Contains("延时成功译文") || history.Text.Contains("失败占位") || history.Text.Contains("迟到译文")) throw new InvalidOperationException("实时/延时成功译文未共同累计，或失败/取消结果进入了记录。");
            }
            if (!history.Text.Contains("延时成功译文")) throw new InvalidOperationException("播放器关闭后丢失了已完成记录。");
            checks.Add("PASS: successful realtime and delayed captions accumulate in the same retained history; failed placeholders and cancelled delayed callbacks do not enter it. Offline event forwarding only, no inference.");
            delayedRoom.Text = "22748536"; if (PrepareDelayedPlayerSettings().BilibiliRoom != prepared.BilibiliRoom) throw new InvalidOperationException("纯房间号未转换为有效链接。");
            delayedRoom.Text = "https://example.test/22748536"; bool invalidRejected = false;
            try { PrepareDelayedPlayerSettings(); } catch (ArgumentException) { invalidRejected = true; }
            if (!invalidRejected || settings.BilibiliRoom != prepared.BilibiliRoom) throw new InvalidOperationException("非B站链接未被本地拒绝。");
            delayedRoom.Text = prepared.BilibiliRoom;
            biliButton.Enabled = false; setup.Enabled = false; if (delayedRoom.Enabled || delayedStart.Enabled) throw new InvalidOperationException("实时翻译禁用状态未同步到延时入口。");
            biliButton.Enabled = setup.Enabled = true; if (!delayedRoom.Enabled || !delayedStart.Enabled) throw new InvalidOperationException("停止后的延时入口未恢复。");
            checks.Add("PASS: offline URL/room-number validation saves canonical room and transfers it into an idle player with delay/cache/account preferences; invalid hosts are rejected and delayed controls mirror session/settings availability. No network or paid API calls.");
            translateNav.PerformClick(); bool before = overlay.Visible; subtitleToggle.PerformClick(); if (overlay.Visible == before) throw new InvalidOperationException("字幕入口没有响应。"); subtitleToggle.PerformClick();
            string samples = string.Concat(Enumerable.Range(0, 18).Select(i => $"12:{i:00}:08  今日は一緒にゲームをやっていきましょう。\n今天一起玩游戏吧。这段是界面检查的示例文字，用来确认较长的翻译记录仍能换行、选择和滚动查看。\n\n"));
            history.Text = samples; translateNav.PerformClick(); if (history.Visible) throw new InvalidOperationException("实时页中央出现了历史文字。"); biliButton.PerformClick(); if (history.Visible || biliPlayer != null) throw new InvalidOperationException("延时页中央出现历史文字，或导航打开了播放器。"); historyNav.PerformClick(); if (!history.Visible || history.Text != samples) throw new InvalidOperationException("后台累计记录无法在记录页查看。");
            checks.Add("PASS: accumulated history remains hidden on realtime/delayed pages and appears only in the records page.");
            foreach (string theme in new[] { "light", "dark" })
            {
                Theme.Use(theme); Theme.Refresh(this);
                foreach (Size size in new[] { new Size(1120, 800), new Size(920, 680) })
                foreach (MainPage page in new[] { MainPage.Realtime, MainPage.Delayed, MainPage.Records })
                {
                    ClientSize = size; history.Text = page == MainPage.Records ? samples : ""; SelectWorkspace(page); PerformLayout(); Application.DoEvents();
                    var visibleControls = new List<Control> { translateNav, biliButton, historyNav, setup, workspaceTitle, service };
                    if (page == MainPage.Realtime) visibleControls.AddRange([devices, refresh, start, subtitleToggle, through, status, timing]);
                    else if (page == MainPage.Delayed) visibleControls.AddRange([delayedRoom, delayedStart, delayedStatus]);
                    else visibleControls.Add(history);
                    foreach (Control control in visibleControls)
                    {
                        var bounds = RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
                        if (!control.Visible || control.Width <= 0 || control.Height <= 0 || !ClientRectangle.Contains(bounds)) throw new InvalidOperationException($"{theme} {size.Width}×{size.Height} {page} 布局遮挡：{control.Text} {bounds}。");
                    }
                    if (page == MainPage.Records && (history.Height < 350 || operationHost.Visible || !history.Visible || history.ForeColor.ToArgb() != Theme.Text.ToArgb())) throw new InvalidOperationException("记录页阅读区、底部收起状态或文字主题异常。");
                    if (folder != null)
                    {
                        Directory.CreateDirectory(folder); using var bitmap = CapturePreview(); bitmap.Save(Path.Combine(folder, $"main-{theme}-{page.ToString().ToLowerInvariant()}{(size.Width == 1120 ? "" : "-minimum")}.png"));
                    }
                    checks.Add($"PASS: {theme} {size.Width}x{size.Height} {page} controls are visible and full native window was captured.");
                }
            }
            history.Select(0, 8); if (history.SelectedText.Length != 8 || history.ContextMenuStrip?.Items.Count != 3) throw new InvalidOperationException("翻译记录无法选择复制。");
            checks.Add("PASS: long records retain selectable text, wrapping, scrolling and copy/latest actions.");
        }
        finally
        {
            Theme.Use(originalTheme); Theme.Refresh(this); ClientSize = originalSize; history.Rtf = originalRtf; history.Select(Math.Min(selected, history.TextLength), Math.Min(selectedLength, Math.Max(0, history.TextLength - selected))); settings.BilibiliRoom = originalRoom; delayedRoom.Text = originalInput; SettingsStore.Save(settings); SelectWorkspace(originalPage); status.Text = originalStatus;
            biliButton.Enabled = setup.Enabled = true; SyncDelayedControls(); if (originalOverlay) { if (!overlay.Visible) overlay.Show(); } else overlay.Hide(); PerformLayout(); Application.DoEvents();
        }
        return string.Join(Environment.NewLine, checks);
    }
    public void OpenSettings()
    {
        if (closing) return;
        if (settingsEditor is { IsDisposed: false } existing) { existing.Activate(); return; }
        if (busy || session != null || biliPlayer is { IsDisposed: false }) return;
        var editor = new SettingsForm(settings); settingsEditor = editor;
        start.Enabled = false; SyncDelayedControls();
        editor.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(settingsEditor, editor)) settingsEditor = null;
            if (editor.DialogResult == DialogResult.OK)
            {
                settings = editor.Result; Theme.Use(settings.UiTheme); Theme.RefreshOpenForms();
                if (!overlay.IsDisposed) overlay.Apply(settings);
                if (!closing) status.Text = "设置已保存。";
            }
            else if (settings.ModelFolder != editor.Result.ModelFolder)
            {
                settings.ModelFolder = editor.Result.ModelFolder;
                if (!closing) status.Text = "模型迁移已完成，存放位置已保存。";
            }
            if (!closing && !IsDisposed)
            {
                start.Enabled = !busy && session == null && biliPlayer is not { IsDisposed: false };
                SyncDelayedControls(); UpdateService();
            }
        };
        editor.Show(this);
    }
    internal string VerifyModelessSettingsForPreview(string? folder = null)
    {
        if (!Visible || closing || busy || session != null || biliPlayer != null || settingsEditor is { IsDisposed: false }) throw new InvalidOperationException("设置窗口检查需要空闲且已显示的主窗口。");
        AppSettings original = settings.Clone(); string originalTheme = Theme.Current, originalStatus = status.Text, originalRoot = SettingsStore.Root; Rectangle originalBounds = Bounds; MainPage originalPage = currentPage;
        var checks = new List<string>();
        ComboBox ThemeChoice(SettingsForm editor) => (ComboBox)typeof(SettingsForm).GetField("themeChoice", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
        void CheckMainEnabled()
        {
            if (!Enabled || !IsWindowEnabled(Handle)) throw new InvalidOperationException("非模态设置仍禁用了主窗口。");
            var shell = DesktopShell.For(this) ?? throw new InvalidOperationException("主窗口缺少可移动边框。");
            var points = new[] { (new Point(1, 1), 13), (new Point(Width / 2, 1), 12), (new Point(Width - 2, 1), 14), (new Point(1, Height / 2), 10), (new Point(Width - 2, Height / 2), 11), (new Point(1, Height - 2), 16), (new Point(Width / 2, Height - 2), 15), (new Point(Width - 2, Height - 2), 17) };
            if (points.Any(x => shell.ResizeHitTest(PointToScreen(x.Item1)) != x.Item2)) throw new InvalidOperationException("设置打开后主窗口的八边缩放命中丢失。");
        }
        try
        {
            setup.PerformClick(); Application.DoEvents();
            var editor = settingsEditor ?? throw new InvalidOperationException("设置按钮未打开编辑器。");
            CheckMainEnabled();
            if (!editor.Visible || editor.Owner != this || start.Enabled || delayedStart.Enabled || !setup.Enabled) throw new InvalidOperationException("设置窗口所有权或编辑期间的启动禁用状态不正确。");
            setup.PerformClick(); Application.DoEvents();
            if (!ReferenceEquals(settingsEditor, editor) || OwnedForms.Count(x => x is SettingsForm && !x.IsDisposed) != 1) throw new InvalidOperationException("重复打开设置创建了多个编辑器。");
            var hotkey = Message.Create(Handle, 0x312, (IntPtr)1, IntPtr.Zero); WndProc(ref hotkey);
            if (session != null || biliPlayer != null || !ReferenceEquals(settingsEditor, editor)) throw new InvalidOperationException("设置期间快捷键启动了识别或播放器。");
            Bounds = new Rectangle(originalBounds.X + 16, originalBounds.Y + 12, originalBounds.Width, originalBounds.Height); Application.DoEvents(); CheckMainEnabled();
            if (Bounds.Location == originalBounds.Location) throw new InvalidOperationException("设置打开时主窗口无法移动。");
            historyNav.PerformClick(); if (currentPage != MainPage.Records) throw new InvalidOperationException("设置打开时主窗口导航被禁用。"); translateNav.PerformClick();
            if (folder != null)
            {
                Directory.CreateDirectory(folder); using var mainImage = CapturePreview(); mainImage.Save(Path.Combine(folder, "main-settings-modeless.png")); using var editorImage = DesktopShell.CapturePreview(editor); editorImage.Save(Path.Combine(folder, "settings-modeless.png"));
            }
            checks.Add("PASS: settings opens modeless with an enabled native owner; main window moves, keeps all eight resize edges and working navigation; repeated clicks reuse one editor and translation hotkey only activates it.");

            int savedContext = original.NormalizedTranslationContextSegments == 7 ? 6 : 7; string savedTheme = originalTheme == "light" ? "dark" : "light";
            editor.SetContextForPreview(savedContext); editor.ShowSubtitleStyleForPreview(); ThemeChoice(editor).SelectedIndex = savedTheme == "light" ? 1 : 0;
            if (Theme.Current != savedTheme) throw new InvalidOperationException("设置主题预览未即时应用。");
            editor.SaveForPreview(); Application.DoEvents();
            if (settingsEditor != null || !editor.IsDisposed || !start.Enabled || !delayedStart.Enabled || settings.TranslationContextSegments != savedContext || SettingsStore.Load().TranslationContextSegments != savedContext || Theme.Current != savedTheme || Theme.Normalize(settings.UiTheme) != savedTheme) throw new InvalidOperationException("非模态保存未应用Result、主题、上下文或恢复启动按钮。");
            checks.Add("PASS: Save closes the modeless editor, applies Result/context/theme to main and disk, and restores realtime/delayed startup controls.");

            setup.PerformClick(); var cancelEditor = settingsEditor ?? throw new InvalidOperationException("无法再次打开设置。");
            cancelEditor.SetContextForPreview(savedContext + 1); cancelEditor.ShowSubtitleStyleForPreview(); ThemeChoice(cancelEditor).SelectedIndex = savedTheme == "light" ? 0 : 1;
            cancelEditor.CancelForPreview(); Application.DoEvents();
            if (settingsEditor != null || !cancelEditor.IsDisposed || !start.Enabled || !delayedStart.Enabled || settings.TranslationContextSegments != savedContext || Theme.Current != savedTheme || SettingsStore.Load().TranslationContextSegments != savedContext) throw new InvalidOperationException("非模态取消未回滚主题，或覆盖了已保存上下文。");
            checks.Add("PASS: Cancel restores the last saved theme/context and startup state without saving temporary edits.");

            string secondaryRoot = Path.Combine(Path.GetTempPath(), "Kotoshiro-modeless-close-" + Guid.NewGuid().ToString("N"));
            try
            {
                SettingsStore.Root = secondaryRoot;
                using var secondary = new MainForm(new AppSettings { UiTheme = savedTheme }); secondary.Show(); Application.DoEvents(); secondary.setup.PerformClick();
                var secondaryEditor = secondary.settingsEditor ?? throw new InvalidOperationException("临时主窗口未打开设置。");
                using var pendingOperation = new CancellationTokenSource(); typeof(SettingsForm).GetField("operation", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(secondaryEditor, pendingOperation);
                secondary.Close(); Application.DoEvents();
                if (!secondary.IsDisposed || !secondaryEditor.IsDisposed || secondary.settingsEditor != null || !pendingOperation.IsCancellationRequested || secondary.start.Enabled) throw new InvalidOperationException("关闭主窗口留下编辑器、未取消设置操作或错误恢复启动按钮。");
                checks.Add("PASS: closing an isolated main window cancels pending editor work, closes/disposes its owned editor before saving, and never restores startup while closing. No downloads or API requests.");
            }
            finally { SettingsStore.Root = originalRoot; SettingsStore.SetModelDirectory(settings.ModelFolder); }
        }
        finally
        {
            if (settingsEditor is { IsDisposed: false } editor) { editor.DialogResult = DialogResult.Cancel; editor.Close(); }
            SettingsStore.Root = originalRoot; settings = original; SettingsStore.SetModelDirectory(settings.ModelFolder); SettingsStore.Save(settings); Theme.Use(originalTheme); Theme.RefreshOpenForms(); overlay.Apply(settings); Bounds = originalBounds; SelectWorkspace(originalPage); start.Enabled = setup.Enabled = true; SyncDelayedControls(); UpdateService(); status.Text = originalStatus; Application.DoEvents();
        }
        return string.Join(Environment.NewLine, checks);
    }
    private async Task OpenBilibiliPlayerAsync()
    {
        if (settingsEditor is { IsDisposed: false } editor) { editor.Activate(); return; }
        if (closing || busy || session != null) return;
        if (biliPlayer != null && !biliPlayer.IsDisposed) { biliPlayer.Activate(); return; }
        AppSettings prepared;
        try { prepared = PrepareDelayedPlayerSettings(); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { delayedStatus.Text = ex.Message; delayedRoom.Focus(); return; }
        if (!ApiTranslator.IsConfigured(settings) || !RecognitionEngines.IsConfigured(settings)) { OpenSettings(); return; }
        if (string.IsNullOrWhiteSpace(settings.ModelFolder))
        {
            using var picker = new FolderBrowserDialog { Description = "选择 B 站播放器临时缓存目录（建议 D 盘等数据盘）", UseDescriptionForTitle = true };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            settings.ModelFolder = picker.SelectedPath; SettingsStore.SetModelDirectory(settings.ModelFolder); SettingsStore.Save(settings);
            prepared = settings.Clone();
        }
        biliPlayer = new BilibiliPlayerForm(prepared);
        BindDelayedHistory(biliPlayer);
        biliPlayer.PreferencesChanged += (room, delay, wait, quality) => { settings.BilibiliRoom = room; delayedRoom.Text = room; settings.PlaybackDelay = delay; settings.WaitForSubtitles = wait; settings.BilibiliQuality = quality; SettingsStore.Save(settings); };
        start.Enabled = setup.Enabled = false;
        biliPlayer.FormClosed += (_, _) => { biliPlayer = null; start.Enabled = setup.Enabled = true; };
        biliPlayer.Show(this);
        delayedStatus.Text = "已打开延时播放器。";
        if (!biliPlayer.Running) await biliPlayer.TogglePlayerAsync();
    }
    private void UpdateService()
    {
        service.Text = "日语 → 中文 · " + RecognitionEngines.ModelLabel(settings) + " · " + ApiTranslator.ServiceLabel(settings);
        bool showUsage = settings.ApiProvider == "chatgpt";
        usage.Visible = showUsage;
        if (showUsage)
        {
            using var auth = new ChatGptAuth(); var account = auth.Accounts().FirstOrDefault(x => x.Id == settings.ChatGptAccountId);
            service.Text += "\n账号：" + (account?.Label ?? "尚未选择") + (account?.CanUsePlan == true ? "" : " · 请登录并授权");
        }
    }
    private void RefreshDevices()
    {
        try
        {
            devices.Items.Clear(); var found = LiveAudioCapture.Devices(); devices.Items.AddRange(found);
            if (devices.Items.Count > 0) { int selected = Array.FindIndex(found, x => x.Id == LiveAudioCapture.DefaultDeviceId()); devices.SelectedIndex = Math.Max(0, selected); }
            else status.Text = "没有可用的扬声器或耳机，请连接声音设备。";
        }
        catch { status.Text = "无法读取声音设备，请检查 Windows 音频服务。"; }
    }
    private void Ui(Action action, CancellationToken token = default) { if (!IsDisposed && IsHandleCreated && !token.IsCancellationRequested) { try { BeginInvoke(() => { if (!IsDisposed && !closing && !token.IsCancellationRequested) action(); }); } catch (InvalidOperationException) { } } }
    public async Task ToggleAsync()
    {
        if (closing) return;
        if (settingsEditor is { IsDisposed: false } editor) { editor.Activate(); return; }
        if (biliPlayer != null && !biliPlayer.IsDisposed) { await biliPlayer.TogglePlayerAsync(); return; }
        if (session != null) { await StopAsync(); return; }
        if (busy) return;
        if (!ApiTranslator.IsConfigured(settings) || !RecognitionEngines.IsConfigured(settings)) { OpenSettings(); return; }
        if (devices.SelectedItem is not AudioDevice device) { status.Text = "请先选择声音设备。"; return; }
        busy = true; setup.Enabled = devices.Enabled = refresh.Enabled = biliButton.Enabled = false; start.Text = "停止翻译"; session = new();
        var localSession = session;
        var token = localSession.Token;
        try
        {
            status.Text = RecognitionEngines.UsesApi(settings) ? "正在准备 API 识别…" : "正在加载本地模型…";
            var loadTimer = Stopwatch.StartNew();
            engine = RecognitionEngines.Create(settings); await engine.StartAsync(settings, token);
            token.ThrowIfCancellationRequested();
            timing.Text = $"识别服务准备 {loadTimer.Elapsed.TotalSeconds:F2} 秒（仅启动时） · 等待第一段声音…";
            capture = new LiveAudioCapture(settings.SegmentSeconds);
            capture.Level += value => { if (Environment.TickCount64 - levelUpdate < 80) return; levelUpdate = Environment.TickCount64; Ui(() => meter.Value = Math.Clamp((int)(value * 150), 0, 100), token); };
            capture.Failed += message => Ui(() => _ = StopAsync(message), token);
            capture.Start(device.Id); overlay.Show(); overlay.SetStatus("正在等待直播声音…");
            status.Text = $"正在监听 · {engine.Mode} · 日语 → 简体中文";
            pipeline = RunPipelineAsync(capture, engine, settings.Clone(), token); busy = false;
        }
        catch (OperationCanceledException) { busy = false; CleanupIfCurrent(localSession); }
        catch (Exception ex) { status.Text = ex.Message; busy = false; CleanupIfCurrent(localSession); }
    }
    private async Task RunPipelineAsync(LiveAudioCapture input, IRecognitionEngine recognizer, AppSettings snapshot, CancellationToken token)
    {
        using var translator = new ApiTranslator(snapshot);
        int failures = 0;
        try
        {
            await foreach (var chunk in input.Chunks.Reader.ReadAllAsync(token))
            {
                if (DateTime.UtcNow - chunk.CapturedAt > TimeSpan.FromSeconds(15)) { Ui(() => status.Text = "处理落后，已跳过旧音频。请换更快的识别或翻译模型。", token); continue; }
                try
                {
                    double queueSeconds = chunk.ReadyTimestamp > 0 ? Stopwatch.GetElapsedTime(chunk.ReadyTimestamp).TotalSeconds : Math.Max(0, (DateTime.UtcNow - chunk.CapturedAt).TotalSeconds);
                    var timer = Stopwatch.StartNew();
                    string japanese = await recognizer.TranscribeAsync(chunk.Wave, token);
                    double recognitionSeconds = timer.Elapsed.TotalSeconds;
                    if (string.IsNullOrWhiteSpace(japanese) || !japanese.Any(c => c is >= '\u3040' and <= '\u30ff' or >= '\u3400' and <= '\u9fff')) continue;
                    Ui(() => { overlay.SetStatus("日语已识别，正在翻译…"); status.Text = "日语已识别，正在翻译…"; }, token);
                    timer.Restart();
                    string chinese = await translator.TranslateAsync(japanese, token);
                    double translationSeconds = timer.Elapsed.TotalSeconds;
                    token.ThrowIfCancellationRequested(); failures = 0;
                    if (string.IsNullOrWhiteSpace(chinese)) continue;
                    long displayQueued = Stopwatch.GetTimestamp();
                    Ui(() =>
                    {
                        var displayTimer = Stopwatch.StartNew();
                        double displayQueueSeconds = Stopwatch.GetElapsedTime(displayQueued).TotalSeconds;
                        overlay.SetCaption(japanese, chinese);
                        AppendTranslationRecord(japanese, chinese);
                        double displaySeconds = displayQueueSeconds + displayTimer.Elapsed.TotalSeconds;
                        timing.Text = $"采集段 {chunk.Duration:F2}s · 排队 {queueSeconds:F2}s · 识别 {recognitionSeconds:F2}s · 翻译 {translationSeconds:F2}s · 字幕更新 {displaySeconds:F3}s\n本段起点到更新约 {chunk.Duration + queueSeconds + recognitionSeconds + translationSeconds + displaySeconds:F2}s（采集段含讲话与断句等待）";
                        status.Text = $"正在监听 · {recognizer.Mode}";
                    }, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    failures++; Ui(() => { status.Text = ex is OperationCanceledException ? "本段请求超时，已跳过。" : ex.Message; overlay.SetStatus("这一段未完成，等待下一段…"); }, token);
                    if (ex is ChatGptPlanException || ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.PaymentRequired or System.Net.HttpStatusCode.Forbidden } || failures >= 3)
                    { Ui(() => _ = StopAsync("识别或翻译已停止：" + (ex is ChatGptPlanException or HttpRequestException ? ex.Message : "请检查识别与翻译设置、网络或本地模型。")), token); break; }
                }
            }
        }
        catch (OperationCanceledException) { }
    }
    private void CleanupIfCurrent(CancellationTokenSource expected)
    {
        if (session != expected) return;
        capture?.Dispose(); capture = null; engine?.Dispose(); engine = null;
        expected.Dispose(); session = null; pipeline = null;
        start.Text = "开始翻译"; setup.Enabled = devices.Enabled = refresh.Enabled = biliButton.Enabled = true; meter.Value = 0;
    }
    private async Task StopAsync(string message = "已停止。")
    {
        // Hide before cancellation waits; history remains available through "显示字幕".
        if (!overlay.IsDisposed) overlay.Hide();
        var current = session; if (current == null) return;
        start.Enabled = false; current.Cancel(); capture?.Dispose(); capture = null;
        // During model loading ToggleAsync owns cleanup. Cancellation prevents a second session.
        if (busy)
        {
            while (busy) await Task.Delay(50);
        }
        else if (pipeline != null) { try { await pipeline; } catch (OperationCanceledException) { } }
        CleanupIfCurrent(current); start.Enabled = true; status.Text = message;
        if (!overlay.IsDisposed) overlay.SetStatus("翻译已停止 · 可滚动查看历史");
    }
    internal async Task<string> VerifyStopForPreviewAsync()
    {
        if (session != null || busy || closing || !Visible) throw new InvalidOperationException("停止翻译检查需要空闲且已显示的主窗口。");
        string originalStatus = status.Text;
        overlay.SetCaption("停止前の字幕です。", "停止前的字幕历史。");
        string retainedHistory = overlay.HistoryForTest.Text;
        var checks = new List<string>();
        foreach (bool duringStartup in new[] { false, true })
        {
            var current = new CancellationTokenSource(); var token = current.Token;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool cancelled = false, staleApplied = false;
            using var registration = token.Register(() => cancelled = true);
            session = current; busy = duringStartup;
            start.Text = "停止翻译"; start.Enabled = true;
            setup.Enabled = devices.Enabled = refresh.Enabled = biliButton.Enabled = false;
            pipeline = duringStartup ? null : completion.Task;
            overlay.Show();
            Ui(() => { staleApplied = true; overlay.SetCaption("古い字幕", "迟到的字幕"); overlay.Show(); }, token);
            try
            {
                // Exercise the actual button handler while cancellation cleanup is held pending.
                start.PerformClick();
                if (overlay.Visible || !cancelled || start.Enabled) throw new InvalidOperationException("停止按钮未立即隐藏字幕并取消待处理任务。");
                await Task.Delay(80);
                if (staleApplied || overlay.Visible || overlay.HistoryForTest.Text != retainedHistory) throw new InvalidOperationException("取消后排队的字幕回调仍然执行，或停止时丢失了历史。");
                if (duringStartup) { busy = false; CleanupIfCurrent(current); }
                else completion.TrySetResult();
                var wait = Stopwatch.StartNew();
                while (!start.Enabled && wait.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(20);
                if (session != null || busy || !start.Enabled || start.Text != "开始翻译" || !setup.Enabled || !devices.Enabled || !refresh.Enabled || !biliButton.Enabled || overlay.Visible || subtitleToggle.Text != "显示字幕") throw new InvalidOperationException("停止后控件或字幕状态未恢复。");
                subtitleToggle.PerformClick();
                if (!overlay.Visible || overlay.HistoryForTest.Text != retainedHistory) throw new InvalidOperationException("停止后不能重新查看保留的字幕历史。");
                subtitleToggle.PerformClick();
                checks.Add($"PASS: {(duringStartup ? "startup" : "pending pipeline")} stop button immediately hides subtitles, cancels work, ignores queued callbacks, restores controls and preserves viewable history.");
            }
            finally
            {
                completion.TrySetResult();
                if (session == current) { busy = false; if (!token.IsCancellationRequested) current.Cancel(); CleanupIfCurrent(current); }
                start.Enabled = true; overlay.Hide();
            }
        }
        status.Text = originalStatus;
        return string.Join(Environment.NewLine, checks);
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x312 && m.WParam == (IntPtr)1)
        {
            if (settingsEditor is { IsDisposed: false } editor) editor.Activate();
            else if (biliPlayer == null && session == null && !busy && currentPage == MainPage.Delayed) _ = OpenBilibiliPlayerAsync();
            else _ = ToggleAsync();
        }
        base.WndProc(ref m);
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
