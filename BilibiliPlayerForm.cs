using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace Kotoshiro;

internal sealed class BilibiliPlayerForm : Form
{
    private readonly AppSettings settings;
    private readonly TextBox room;
    private readonly NumericUpDown delay;
    private readonly CheckBox wait;
    private readonly NumericUpDown volume;
    private readonly Button start = Theme.Button("打开直播", true), pause = Theme.Button("暂停"), fullscreen = Theme.Button("全屏");
    private readonly Button refresh = Theme.Button("刷新直播");
    private readonly Label status = Theme.Label("输入 B 站直播间链接或房间号。画面和声音会一起延迟，为翻译留出时间。", true);
    private readonly Label timing = Theme.Label("直播缓存放在你选择的模型文件夹中，停止后清理。", true);
    private readonly Panel video = new() { Dock = DockStyle.Fill, BackColor = Color.Black, Margin = Padding.Empty, Padding = Padding.Empty };
    private readonly BilibiliAccountStore accounts = new();
    private readonly BilibiliCommentsPanel comments;
    private readonly ComboBox quality = Theme.Combo();
    private readonly Button applyQuality = Theme.Button("读取清晰度");
    private readonly Label qualityStatus = Theme.Label("画质以直播间返回结果为准", true);
    private int actualQuality;
    private CancellationTokenSource? session;
    private Task? running;
    private Task? lifetime;
    private Task? commentConnection;
    private VideoBuffer? buffer;
    private VideoPlayer? player;
    private readonly List<SubtitleCue> cues = [];
    private double resolvedThrough;
    private bool inputFinished, manualPause, isFullscreen, closing, refreshing, playbackStarted;
    private string segmentTiming = "正在等待第一段识别…";
    private readonly TableLayoutPanel root;
    private readonly TableLayoutPanel content;
    private readonly FlowLayoutPanel actions;
    private readonly Panel inputPanel;
    private readonly Panel commentRail;
    private readonly PlayerRailButton commentToggle = new(), railRefresh = new() { RefreshIcon = true };
    private readonly ToolTip playerTips = new();
    private bool commentsVisible = true;
    private float commentsWidth = 310;
    private Rectangle previousBounds;
    private FormWindowState previousState;
    private Padding previousPadding;
    private string? localTestFile;
    private HttpMessageHandler? testHandler;
    private HttpMessageHandler? testRecognitionHandler;
    private bool testScreenshot;
    internal bool CompletedTest { get; private set; }
    internal string LastTestStatus => status.Text;
    internal string CacheFolder => buffer?.Folder ?? "";
    internal bool Running => session != null;
    public event Action<string, int, bool, int>? PreferencesChanged;
    public event Action<string, string>? CaptionTranslated;
    public BilibiliPlayerForm(AppSettings settings)
    {
        this.settings = settings.Clone(); Theme.Apply(this); Text = "言代Kotoshiro · B站延迟播放器";
        ClientSize = new Size(1320, 800); MinimumSize = new Size(980, 620); StartPosition = FormStartPosition.CenterParent; KeyPreview = true;
        room = Theme.Input(settings.BilibiliRoom); room.PlaceholderText = "https://live.bilibili.com/22748536 或房间号";
        delay = new NumericUpDown { Minimum = 3, Maximum = 60, Value = Math.Clamp(settings.PlaybackDelay, 3, 60), Width = 65, BackColor = Theme.Surface, ForeColor = Theme.Text };
        wait = new CheckBox { Text = "字幕未准备好时等待", AutoSize = true, Checked = settings.WaitForSubtitles, Margin = new Padding(12, 10, 0, 0) };
        var inputRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 }; inputRow.ColumnStyles.Add(new(SizeType.Absolute, 110)); inputRow.ColumnStyles.Add(new(SizeType.Percent, 100)); inputRow.Controls.Add(Theme.Label("B站直播间"), 0, 0); room.Margin = new Padding(3, 8, 3, 8); inputRow.Controls.Add(room, 1, 0);
        inputPanel = new Panel { Dock = DockStyle.Fill, Height = 48 }; inputPanel.Controls.Add(inputRow);
        actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Padding = new Padding(0, 6, 0, 6) };
        actions.Controls.AddRange([start, pause, refresh, fullscreen, Theme.Label("播放延迟 / 秒"), delay, wait]);
        volume = new NumericUpDown { Minimum = 0, Maximum = 100, Value = 80, Width = 60, BackColor = Theme.Surface, ForeColor = Theme.Text, Margin = new Padding(3, 8, 3, 8) }; actions.Controls.Add(Theme.Label("音量")); actions.Controls.Add(volume);
        quality.Dock = DockStyle.None; quality.Width = 115; quality.Margin = new Padding(3, 8, 3, 8);
        quality.DrawMode = DrawMode.Normal; quality.BackColor = Theme.SurfaceRaised; quality.ForeColor = Theme.Text; quality.DisplayMember = nameof(BilibiliQuality.Name);
        quality.Items.Add(new BilibiliQuality(0, "自动")); if (settings.BilibiliQuality > 0) quality.Items.Add(new BilibiliQuality(settings.BilibiliQuality, BilibiliResolver.QualityName(settings.BilibiliQuality))); quality.SelectedIndex = quality.Items.Count - 1;
        actions.Controls.AddRange([Theme.Label("清晰度"), quality, applyQuality, qualityStatus]);
        comments = new BilibiliCommentsPanel(accounts);
        comments.Margin = comments.Padding = Padding.Empty;
        commentRail = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black, Padding = Padding.Empty, Margin = Padding.Empty };
        commentRail.Controls.AddRange([commentToggle, railRefresh]);
        commentToggle.AccessibleName = "收起评论区"; railRefresh.AccessibleName = "刷新直播";
        playerTips.SetToolTip(commentToggle, "收起评论区"); playerTips.SetToolTip(railRefresh, "刷新直播（F5）"); playerTips.SetToolTip(refresh, "重新连接直播并从当前直播位置开始缓存（F5）");
        commentRail.SizeChanged += (_, _) => PositionRailControls();
        commentToggle.Click += (_, _) => SetCommentsVisible(!commentsVisible);
        refresh.Click += async (_, _) => await RefreshAsync(); railRefresh.Click += async (_, _) => await RefreshAsync();
        content = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.Black, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
        content.ColumnStyles.Add(new(SizeType.Percent, 100)); content.ColumnStyles.Add(new(SizeType.Absolute, 32)); content.ColumnStyles.Add(new(SizeType.Absolute, 310)); content.RowStyles.Add(new(SizeType.Percent, 100)); content.Controls.Add(video, 0, 0); content.Controls.Add(commentRail, 1, 0); content.Controls.Add(comments, 2, 0);
        root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), Margin = Padding.Empty, ColumnCount = 1, RowCount = 5 }; root.ColumnStyles.Add(new(SizeType.Percent, 100));
        for (int i = 0; i < 5; i++) root.RowStyles.Add(new(i == 2 ? SizeType.Percent : SizeType.AutoSize, i == 2 ? 100 : 0));
        root.Controls.Add(inputPanel, 0, 0); root.Controls.Add(actions, 0, 1); root.Controls.Add(content, 0, 2); status.MaximumSize = timing.MaximumSize = new Size(1270, 0); root.Controls.Add(status, 0, 3); root.Controls.Add(timing, 0, 4); Controls.Add(root);
        pause.Enabled = false;
        start.Click += async (_, _) => { if (Running) await StopAsync(); else await StartAsync(); };
        pause.Click += (_, _) => { manualPause = !manualPause; pause.Text = manualPause ? "继续" : "暂停"; };
        fullscreen.Click += (_, _) => Fullscreen();
        applyQuality.Click += async (_, _) => await ApplyQualityAsync();
        comments.AccountChanged += () => qualityStatus.Text = "账号已更新，可重新读取或应用清晰度";
        Shown += async (_, _) => { try { if (!comments.Preview) await comments.RefreshAccountAsync(); } catch { qualityStatus.Text = "B站登录信息未能读取，可重新登录"; } };
        volume.ValueChanged += async (_, _) => { try { if (player != null) await player.VolumeAsync((int)volume.Value, session?.Token ?? CancellationToken.None); } catch (Exception) { } };
        KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Escape && isFullscreen) { Fullscreen(); e.Handled = true; }
            else if (e.KeyCode == Keys.F5 || e.Control && e.KeyCode == Keys.R) { e.SuppressKeyPress = true; await RefreshAsync(); }
        };
        FormClosing += async (_, e) => { if (closing) return; e.Cancel = true; closing = true; await StopAsync(); Close(); };
    }
    private void Fullscreen()
    {
        SuspendLayout(); root.SuspendLayout();
        if (!isFullscreen)
        {
            previousBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds; previousState = WindowState; previousPadding = root.Padding; isFullscreen = true; inputPanel.Visible = actions.Visible = status.Visible = timing.Visible = false; root.Padding = Padding.Empty; root.BackColor = BackColor = Color.Black;
            FormBorderStyle = FormBorderStyle.None; WindowState = FormWindowState.Normal; Bounds = Screen.FromControl(this).Bounds;
        }
        else
        {
            isFullscreen = false; inputPanel.Visible = actions.Visible = status.Visible = timing.Visible = true; root.Padding = previousPadding; root.BackColor = BackColor = Theme.Background; FormBorderStyle = FormBorderStyle.Sizable; Bounds = previousBounds; WindowState = previousState;
        }
        root.ResumeLayout(true); ResumeLayout(true); PositionRailControls();
    }
    private void PositionRailControls()
    {
        int height = 56, top = Math.Max(0, (commentRail.ClientSize.Height - height) / 2);
        commentToggle.SetBounds(0, top, commentRail.ClientSize.Width, height);
        railRefresh.SetBounds(0, Math.Max(0, top - 42), commentRail.ClientSize.Width, 34);
    }
    private void SetCommentsVisible(bool visible)
    {
        if (commentsVisible == visible) return;
        if (!visible) commentsWidth = content.ColumnStyles[2].Width;
        commentsVisible = visible; content.SuspendLayout(); comments.Visible = visible; content.ColumnStyles[2].Width = visible ? commentsWidth : 0;
        commentToggle.PointLeft = !visible; commentToggle.AccessibleName = visible ? "收起评论区" : "展开评论区";
        playerTips.SetToolTip(commentToggle, commentToggle.AccessibleName); commentToggle.Invalidate(); content.ResumeLayout(true);
    }
    private void SetRefreshEnabled(bool enabled) => refresh.Enabled = railRefresh.Enabled = enabled;
    private Task StartAsync()
    {
        if (closing || IsDisposed) return Task.CompletedTask;
        if (lifetime is { IsCompleted: false }) return lifetime;
        lifetime = StartCoreAsync(); return lifetime;
    }
    private async Task StartCoreAsync()
    {
        try
        {
            MediaTools.Check();
            if (!ApiTranslator.IsConfigured(settings)) throw new InvalidOperationException("请先在主窗口设置翻译服务。");
            RecognitionEngines.Validate(SettingsForRecognition());
            if (string.IsNullOrWhiteSpace(settings.ModelFolder)) throw new InvalidOperationException("请先选择播放器缓存文件夹。");
            if (localTestFile == null) BilibiliResolver.RoomId(room.Text);
            session = new(); room.Enabled = delay.Enabled = wait.Enabled = false; start.Text = "停止直播"; pause.Enabled = true; pause.Text = "暂停";
            manualPause = inputFinished = playbackStarted = false; resolvedThrough = 0; cues.Clear(); segmentTiming = "正在等待第一段识别…";
            CompletedTest = testScreenshot = false;
            PreferencesChanged?.Invoke(room.Text.Trim(), (int)delay.Value, wait.Checked, SelectedQuality);
            running = RunAsync(session.Token); await running;
        }
        catch (OperationCanceledException) { if (!IsDisposed) status.Text = session?.IsCancellationRequested == true ? "直播已停止。" : "直播连接、识别或翻译请求超时，请重新打开直播。"; }
        catch (Exception ex) { if (!IsDisposed) status.Text = ex.Message; }
        finally
        {
            session?.Cancel(); await comments.StopAsync(); if (commentConnection != null) { try { await commentConnection; } catch (Exception) { } commentConnection = null; }
            player?.Dispose(); player = null; buffer?.Dispose(); buffer = null; session?.Dispose(); session = null; running = null;
            if (!IsDisposed) { room.Enabled = delay.Enabled = wait.Enabled = true; start.Text = "打开直播"; start.Enabled = true; pause.Enabled = false; }
            if (!IsDisposed) applyQuality.Text = "读取清晰度";
        }
    }
    private async Task RunAsync(CancellationToken token)
    {
        status.Text = "正在检查 B 站直播间…";
        BilibiliStream? source = null;
        if (localTestFile == null)
        {
            using var resolver = new BilibiliResolver(); var account = await accounts.GetAsync(token);
            source = await resolver.ResolveAsync(room.Text, token, SelectedQuality, account?.CookieHeader); SetQualities(source);
            applyQuality.Text = "应用清晰度";
            commentConnection = comments.StartAsync(source.RoomId, token);
        }
        status.Text = RecognitionEngines.UsesApi(settings) ? "正在准备 API 识别…" : "正在加载本地识别模型…";
        var recognitionSettings = SettingsForRecognition();
        using var engine = RecognitionEngines.Create(recognitionSettings, testRecognitionHandler); await engine.StartAsync(recognitionSettings, token);
        buffer = new VideoBuffer(settings.ModelFolder);
        if (source != null) buffer.Start(source); else buffer.StartFile(localTestFile!);
        var jobs = Channel.CreateBounded<List<VideoPiece>>(new BoundedChannelOptions(8) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        status.Text = $"正在缓存直播，目标延迟 {delay.Value} 秒。识别与翻译已开始提前处理…";
        using var fault = CancellationTokenSource.CreateLinkedTokenSource(token);
        async Task Guard(Func<Task> action) { try { await action(); } catch { fault.Cancel(); throw; } }
        var captureTask = Guard(() => ReadBufferAsync(jobs.Writer, fault.Token));
        var translationTask = Guard(() => TranslateAsync(jobs.Reader, engine, fault.Token));
        var playbackTask = Guard(() => PlayAsync(fault.Token));
        try { await Task.WhenAll(captureTask, translationTask, playbackTask); status.Text = "直播已结束。"; CompletedTest = true; }
        catch
        {
            var failure = new[] { captureTask, translationTask, playbackTask }.FirstOrDefault(x => x.IsFaulted)?.Exception?.GetBaseException();
            if (failure != null) throw failure; throw;
        }
    }
    private async Task ReadBufferAsync(ChannelWriter<List<VideoPiece>> writer, CancellationToken token)
    {
        var pending = new List<VideoPiece>(); var first = Stopwatch.StartNew(); var diskCheck = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                foreach (var piece in buffer!.TakeNewPieces())
                {
                    pending.Add(piece);
                    if (pending.Sum(x => x.Duration) >= settings.SegmentSeconds)
                    { if (!writer.TryWrite(pending)) throw new InvalidOperationException("识别或翻译持续落后，请使用较小模型或较快的翻译服务后重新打开直播。"); pending = []; }
                }
                if (diskCheck.Elapsed.TotalSeconds > 15)
                {
                    var drive = new DriveInfo(Path.GetPathRoot(buffer.Folder)!); if (drive.AvailableFreeSpace < 512L * 1024 * 1024) throw new IOException("缓存所在磁盘剩余空间不足 512 MB，直播已停止。"); diskCheck.Restart();
                }
                if (buffer.Finished)
                {
                    if (buffer.Failed) throw new IOException(buffer.FailureMessage);
                    inputFinished = true;
                    if (pending.Count > 0 && !writer.TryWrite(pending)) throw new InvalidOperationException("直播翻译队列已满。");
                    if (buffer.Edge == 0) throw new InvalidOperationException("直播源没有返回可播放的音画。可能已经结束或连接受到限制。");
                    return;
                }
                if (buffer.Edge == 0 && first.Elapsed.TotalSeconds > 35) throw new TimeoutException("未收到直播画面，请停止后重新打开直播。");
                await Task.Delay(250, token);
            }
        }
        finally { writer.TryComplete(); }
    }
    private async Task TranslateAsync(ChannelReader<List<VideoPiece>> jobs, IRecognitionEngine engine, CancellationToken token)
    {
        using var translator = new ApiTranslator(settings, testHandler);
        int recognitionFailures = 0, translationFailures = 0;
        await foreach (var pieces in jobs.ReadAllAsync(token))
        {
            var timer = Stopwatch.StartNew(); byte[] wave = await buffer!.DecodeAsync(pieces, token); double decode = timer.Elapsed.TotalSeconds; timer.Restart();
            string japanese = "", chinese = ""; double translation = 0; bool translationSucceeded = false;
            try { japanese = await engine.TranscribeAsync(wave, token); recognitionFailures = 0; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) when (RecognitionEngines.UsesApi(settings))
            {
                if (ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.PaymentRequired or System.Net.HttpStatusCode.Forbidden } || ++recognitionFailures >= 3) throw;
                // Do not retry billable requests. Resolve this segment as a gap so waiting-for-subtitles playback can continue.
                chinese = "（这一段识别未完成）";
            }
            double recognition = timer.Elapsed.TotalSeconds;
            if (japanese.Any(c => c is >= '\u3040' and <= '\u30ff' or >= '\u3400' and <= '\u9fff'))
            {
                try { timer.Restart(); chinese = await translator.TranslateAsync(japanese, token); translation = timer.Elapsed.TotalSeconds; translationFailures = 0; translationSucceeded = true; }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    if (ex is ChatGptPlanException or HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.PaymentRequired or System.Net.HttpStatusCode.Forbidden } || ++translationFailures >= 3) throw;
                    chinese = "（这一段翻译未完成）";
                }
            }
            double startTime = pieces[0].Start, endTime = pieces[^1].End;
            cues.Add(new(startTime, endTime, japanese, chinese)); resolvedThrough = endTime;
            cues.RemoveAll(x => x.End < resolvedThrough - 300);
            if (translationSucceeded) PublishTranslatedCaption(japanese, chinese, token);
            segmentTiming = $"本段 {endTime - startTime:F1}s · 读音频 {decode:F2}s · 识别 {recognition:F2}s · 翻译 {translation:F2}s · {engine.Mode} · 处理耗时 {(decode + recognition + translation) / (endTime - startTime):F2} 倍音频时长";
            timing.Text = segmentTiming;
        }
    }
    private void PublishTranslatedCaption(string japanese, string chinese, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(japanese) && !string.IsNullOrWhiteSpace(chinese)) CaptionTranslated?.Invoke(japanese, chinese);
    }
    internal void RecordDelayedCaptionForPreview(string japanese, string chinese, CancellationToken token = default, bool succeeded = true)
    {
        if (succeeded) PublishTranslatedCaption(japanese, chinese, token);
    }
    private async Task PlayAsync(CancellationToken token)
    {
        int delaySeconds = PlaybackGate.TargetDelay((int)delay.Value, settings.SegmentSeconds, wait.Checked);
        while ((!inputFinished && buffer!.Edge < delaySeconds) || (wait.Checked && resolvedThrough <= 0))
        {
            status.Text = $"正在准备播放 · 起播缓存目标 {delaySeconds}s · 已缓存 {buffer!.Edge:F1}s · " + (wait.Checked && resolvedThrough <= 0 ? "等待首段字幕" : "等待视频缓存");
            await Task.Delay(200, token);
        }
        player = new VideoPlayer();
        player.KeyCommand += message =>
        {
            if (message == "kotoshiro-exit-fullscreen" && isFullscreen) Fullscreen();
            else if (message == "kotoshiro-toggle-fullscreen") Fullscreen();
            else if (message == "kotoshiro-toggle-pause") { manualPause = !manualPause; pause.Text = manualPause ? "继续" : "暂停"; }
            else if (message == "kotoshiro-refresh" && !token.IsCancellationRequested && !closing && IsHandleCreated) BeginInvoke(() => { if (!token.IsCancellationRequested) _ = RefreshAsync(); });
        };
        await player.StartAsync(buffer!.PlaybackUrl, video.Handle, token, inputConfig: Path.Combine(buffer.Folder, "input.conf"), fontSize: settings.FontSize);
        await player.VolumeAsync((int)volume.Value, token); playbackStarted = true; bool? paused = null; var captionUpdate = Stopwatch.StartNew(); var diagnosticUpdate = Stopwatch.StartNew();
        var gate = new PlaybackGate(wait.Checked); string? previousCaption = null; bool playerBuffering = false;
        string decoderStatus = "解码信息读取中"; long? dropped = null, decoderDropped = null;
        while (true)
        {
            token.ThrowIfCancellationRequested(); if (player.Finished) throw new InvalidOperationException("播放器已关闭，请重新打开直播。");
            double position = await player.PositionAsync(token) ?? 0;
            if (inputFinished && position >= buffer.Edge - 0.15) return;
            if (inputFinished && position >= buffer.Edge - 1 && await player.PropertyAsync("eof-reached", token) is { ValueKind: System.Text.Json.JsonValueKind.True }) return;
            var cause = gate.Update(position, buffer.Edge, resolvedThrough, inputFinished); bool hold = cause.Hold;
            bool desired = manualPause || hold;
            if (paused != desired) { await player.PauseAsync(desired, token); paused = desired; }
            if (captionUpdate.Elapsed.TotalMilliseconds > 450)
            {
                var cue = SubtitleTimeline.At(cues, position);
                string text = cue?.Chinese ?? "";
                if (text.Length > 0 && settings.ShowJapanese && !string.IsNullOrWhiteSpace(cue!.Japanese)) text = cue.Japanese + "\n" + text;
                if (hold && wait.Checked && position + 0.15 >= resolvedThrough) text = "正在准备下一段字幕…";
                if (text != previousCaption) { await player.CaptionAsync(text, token); previousCaption = text; }
                captionUpdate.Restart();
                if (localTestFile != null && !testScreenshot && position > 2 && cue?.Chinese.Length > 0)
                {
                    testScreenshot = true; await Task.Delay(100, token);
                    await player.ScreenshotAsync(Path.Combine(settings.ModelFolder, "embedded-video-subtitles.png"), token, "window");
                    double before = await player.PositionAsync(token) ?? 0; await player.PauseAsync(true, token); await Task.Delay(400, token);
                    double after = await player.PositionAsync(token) ?? 0;
                    if (Math.Abs(after - before) > 0.15) throw new Exception("嵌入播放器暂停检查失败。");
                    Fullscreen(); await Task.Delay(200, token); await player.TestKeyAsync("kotoshiro-exit-fullscreen", token); await player.PositionAsync(token);
                    if (isFullscreen) throw new Exception("全屏退出事件检查失败。"); await player.PauseAsync(desired, token);
                    await File.WriteAllTextAsync(Path.Combine(settings.ModelFolder, "embedded-clock-test.txt"), $"PASS: embedded real video and subtitle screenshot\nPASS: cue {cue.Start:F2}..{cue.End:F2}, playback {position:F2}, resolved {resolvedThrough:F2}, buffered {buffer.Edge:F2}\nPASS: pause held clock {before:F2} -> {after:F2}; fullscreen toggled and playback restored\nChinese: {cue.Chinese}", token);
                }
                if (diagnosticUpdate.Elapsed.TotalSeconds >= 2)
                {
                    playerBuffering = await player.PropertyAsync("paused-for-cache", token) is { ValueKind: System.Text.Json.JsonValueKind.True };
                    var hw = await player.PropertyAsync("hwdec-current", token);
                    decoderStatus = hw is { ValueKind: System.Text.Json.JsonValueKind.String } h && h.GetString() is { } mode ? mode is not ("no" or "none" or "") ? "硬件解码" : "软件解码" : "解码信息不可用";
                    var displayDrop = await player.PropertyAsync("frame-drop-count", token); var decodeDrop = await player.PropertyAsync("decoder-frame-drop-count", token);
                    dropped = displayDrop is { ValueKind: System.Text.Json.JsonValueKind.Number } d && d.TryGetInt64(out var dn) ? dn : null;
                    decoderDropped = decodeDrop is { ValueKind: System.Text.Json.JsonValueKind.Number } dd && dd.TryGetInt64(out var ddn) ? ddn : null;
                    diagnosticUpdate.Restart();
                }
                string reason = manualPause ? "手动暂停" : cause.Network && cause.Subtitles ? "等待字幕与视频缓存" : cause.Subtitles ? "等待字幕（识别或翻译尚未完成）" : cause.Network ? "等待直播视频缓存" : playerBuffering ? "播放器缓冲中" : "正在播放";
                status.Text = $"B站 · {BilibiliResolver.QualityName(actualQuality)} · 播放 {position:F1}s · 视频缓存余量 {Math.Max(0, buffer.Edge - position):F1}s · {reason}";
                timing.Text = segmentTiming + $"\n字幕余量 {Math.Max(0, resolvedThrough - position):F1}s · {decoderStatus} · 显示掉帧 {dropped?.ToString() ?? "--"} / 解码掉帧 {decoderDropped?.ToString() ?? "--"}";
            }
            if (buffer.Edge - position > 120) throw new InvalidOperationException("播放落后超过 2 分钟，请停止后重新打开直播，以免缓存持续增加。");
            await Task.Delay(100, token);
        }
    }
    public async Task StopAsync()
    {
        if (session == null && lifetime is not { IsCompleted: false }) return; start.Enabled = false; session?.Cancel();
        if (lifetime != null) { try { await lifetime; } catch (Exception) { } }
        if (!IsDisposed) status.Text = "已停止直播，正在清理本次缓存。";
    }
    internal void SetRoomForPreview(string text) => room.Text = text;
    internal string VerifyLayoutForPreview(string? folder = null)
    {
        if (!Visible || Running || isFullscreen) throw new InvalidOperationException("播放器布局检查需要已显示且未播放的普通窗口。");
        string originalTheme = Theme.Current; Rectangle originalBounds = Bounds; FormWindowState originalState = WindowState; bool originalComments = commentsVisible;
        var checks = new List<string>();
        try
        {
            foreach (string theme in new[] { "dark", "light" })
            {
                Theme.Use(theme); Theme.Refresh(this); SetCommentsVisible(true); PerformLayout(); Application.DoEvents();
                int visibleWidth = video.Width; commentToggle.PerformClick(); PerformLayout(); Application.DoEvents();
                if (comments.Visible || content.ColumnStyles[2].Width != 0 || video.Width <= visibleWidth || !commentToggle.PointLeft) throw new InvalidOperationException("收起评论区后视频未扩展，或展开箭头不可用。");
                commentToggle.PerformClick(); PerformLayout(); Application.DoEvents();
                if (!comments.Visible || video.Width != visibleWidth || commentToggle.PointLeft) throw new InvalidOperationException("评论区展开后未恢复布局。");
                checks.Add($"PASS: {theme} normal window collapse/expand uses the real arrow button and returns video width {visibleWidth}px.");
                Fullscreen(); PerformLayout(); Application.DoEvents();
                if (FormBorderStyle != FormBorderStyle.None || Bounds != Screen.FromControl(this).Bounds || root.Padding != Padding.Empty || root.Bounds != ClientRectangle || content.Bounds != root.ClientRectangle || video.Left != 0 || video.Top != 0 || video.Bottom != ClientSize.Height || comments.Right != content.ClientSize.Width || content.Padding != Padding.Empty || video.Margin != Padding.Empty || comments.Margin != Padding.Empty || root.BackColor.ToArgb() != Color.Black.ToArgb()) throw new InvalidOperationException("全屏布局仍有外边距、边框或未铺满屏幕。");
                if (!commentToggle.Visible || !railRefresh.Visible || !refresh.Visible && !railRefresh.Enabled) throw new InvalidOperationException("全屏评论箭头或刷新入口不可用。");
                if (folder != null) { Directory.CreateDirectory(folder); using var image = CaptureLayoutPreview(); image.Save(Path.Combine(folder, $"bilibili-fullscreen-{theme}.png")); }
                SetCommentsVisible(false); PerformLayout(); Application.DoEvents();
                if (comments.Visible || video.Left != 0 || video.Right != commentRail.Left || commentRail.Right != content.Width || video.Bottom != ClientSize.Height || !commentToggle.Visible) throw new InvalidOperationException("全屏收起评论后的视频或恢复箭头布局有空隙。");
                if (folder != null) { using var image = CaptureLayoutPreview(); image.Save(Path.Combine(folder, $"bilibili-fullscreen-collapsed-{theme}.png")); }
                checks.Add($"PASS: {theme} fullscreen has zero outer margins at {ClientSize.Width}x{ClientSize.Height}; comment collapse keeps a visible arrow and refresh icon.");
                Fullscreen(); PerformLayout(); Application.DoEvents();
                if (Bounds != originalBounds || WindowState != originalState || BackColor.ToArgb() != Theme.Background.ToArgb()) throw new InvalidOperationException("退出全屏后原窗口位置、大小或主题未恢复。");
            }
            return string.Join(Environment.NewLine, checks) + Environment.NewLine + "PASS: fullscreen exit restores original window bounds and theme; no API/login/comment send performed.";
        }
        finally
        {
            if (isFullscreen) Fullscreen(); SetCommentsVisible(originalComments); Theme.Use(originalTheme); Theme.Refresh(this);
            root.BackColor = BackColor = Theme.Background; WindowState = FormWindowState.Normal; Bounds = originalBounds; WindowState = originalState; PerformLayout();
        }
    }
    internal Bitmap CaptureLayoutPreview()
    {
        var image = new Bitmap(Width, Height); using var graphics = Graphics.FromImage(image); IntPtr dc = graphics.GetHdc(); bool success;
        try { success = PrintWindow(Handle, dc, 2); } finally { graphics.ReleaseHdc(dc); }
        if (!success) { image.Dispose(); throw new InvalidOperationException("播放器窗口预览生成失败。"); } return image;
    }
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr handle, IntPtr dc, uint flags);
    internal void SeedCommunityPreview()
    {
        comments.SeedPreview(); quality.Items.Clear(); foreach (int id in new[] { 0, 250, 400, 10000 }) quality.Items.Add(new BilibiliQuality(id, BilibiliResolver.QualityName(id))); quality.SelectedIndex = 1;
        qualityStatus.Text = "示例档位 · 实际档位需读取直播间";
    }
    private int SelectedQuality => (quality.SelectedItem as BilibiliQuality)?.Id ?? 0;
    private async Task RefreshAsync()
    {
        if (closing || IsDisposed || refreshing) return;
        refreshing = true; SetRefreshEnabled(false); start.Enabled = applyQuality.Enabled = false;
        try
        {
            await StopAsync();
            if (closing || IsDisposed) return;
            status.Text = "正在刷新直播源并重新缓存…";
            _ = StartAsync();
        }
        finally { refreshing = false; if (!IsDisposed && !closing) { SetRefreshEnabled(true); start.Enabled = applyQuality.Enabled = true; } }
    }
    internal async Task VerifyRefreshTestAsync(Task firstRun)
    {
        var deadline = Stopwatch.StartNew();
        while (!playbackStarted && !firstRun.IsCompleted && deadline.Elapsed.TotalSeconds < 30) await Task.Delay(100);
        if (!playbackStarted || buffer == null) throw new InvalidOperationException("刷新测试未等到实际播放器启动：" + status.Text);
        string oldCache = buffer.Folder; var oldSession = session;
        refresh.PerformClick();
        deadline.Restart();
        while ((session == oldSession || refreshing) && deadline.Elapsed.TotalSeconds < 10) await Task.Delay(50);
        await firstRun;
        if (session == oldSession || !Running || Directory.Exists(oldCache)) throw new InvalidOperationException("刷新未结束旧任务或清理旧缓存。");
        string newCache = buffer?.Folder ?? "";
        var secondRun = lifetime ?? throw new InvalidOperationException("刷新没有重新启动播放。");
        if (newCache.Length == 0 || newCache == oldCache) throw new InvalidOperationException("刷新没有使用独立的新缓存。");
        await secondRun;
        if (!CompletedTest || Running || Directory.Exists(newCache) || !refresh.Enabled || !railRefresh.Enabled) throw new InvalidOperationException("刷新后没有正常播放结束、恢复按钮或清理缓存：" + status.Text);
    }
    private void SetQualities(BilibiliStream source)
    {
        int requested = SelectedQuality; actualQuality = source.Quality; quality.BeginUpdate();
        try
        {
            quality.Items.Clear(); foreach (var item in source.Qualities ?? new[] { new BilibiliQuality(0, "自动") }) quality.Items.Add(item);
            int selected = quality.Items.Cast<BilibiliQuality>().ToList().FindIndex(x => x.Id == requested); quality.SelectedIndex = selected < 0 ? 0 : selected;
        }
        finally { quality.EndUpdate(); quality.Refresh(); }
        qualityStatus.Text = source.Quality == 0 ? "直播源未报告实际画质" : $"实际：{BilibiliResolver.QualityName(source.Quality)}" + (requested > 0 && requested != source.Quality ? "（B站返回较低档位，可登录后重试）" : "");
    }
    private async Task ApplyQualityAsync()
    {
        applyQuality.Enabled = false;
        try
        {
            if (Running) { await StopAsync(); status.Text = "正在切换清晰度并重新缓存…"; _ = StartAsync(); }
            else
            {
                using var resolver = new BilibiliResolver(); var account = await accounts.GetAsync(CancellationToken.None);
                var source = await resolver.ResolveAsync(room.Text, CancellationToken.None, SelectedQuality, account?.CookieHeader); SetQualities(source);
            }
        }
        catch (Exception ex) { qualityStatus.Text = ex.Message; }
        finally { if (!IsDisposed) applyQuality.Enabled = true; }
    }
    internal Task RunLocalTestAsync(string file, HttpMessageHandler handler) { localTestFile = file; testHandler = handler; return StartAsync(); }
    internal Task RunApiLocalTestAsync(string file, HttpMessageHandler translation, HttpMessageHandler speech) { localTestFile = file; testHandler = translation; testRecognitionHandler = speech; return StartAsync(); }
    private AppSettings SettingsForRecognition()
    {
        if (testHandler == null || RecognitionEngines.UsesApi(settings)) return settings;
        var current = settings.Clone(); current.ModelFolder = SettingsStore.ModelDirectory; return current;
    }
    internal async Task VerifyQualityUiAsync(string folder)
    {
        await ApplyQualityAsync();
        if (quality.SelectedIndex < 0 || quality.Items.Count < 2) throw new Exception("实际直播清晰度列表未显示：" + qualityStatus.Text);
        int initial = actualQuality;
        var alternate = quality.Items.Cast<BilibiliQuality>().FirstOrDefault(x => x.Id > 0 && x.Id != actualQuality);
        if (alternate != null)
        {
            quality.SelectedIndex = quality.Items.IndexOf(alternate); await ApplyQualityAsync();
            if (SelectedQuality != alternate.Id) throw new Exception("请求的清晰度未保留在下拉框。");
        }
        quality.Refresh(); using var image = new Bitmap(Width, Height); DrawToBitmap(image, new Rectangle(Point.Empty, Size)); image.Save(Path.Combine(folder, "quality-ui.png"));
        await File.WriteAllTextAsync(Path.Combine(folder, "quality-ui-test.txt"), $"PASS: actual room API with dropdown selected index {quality.SelectedIndex}, selected {SelectedQuality} ({quality.Text}), initial actual {initial}, final actual {actualQuality}; label: {qualityStatus.Text}\nNo playback, real translation, login or send.\n");
    }
    internal async Task RunLiveTestAsync(string input, HttpMessageHandler handler)
    {
        room.Text = input; testHandler = handler; var run = StartAsync(); string cache = "";
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(90));
            if (!Running || buffer == null || buffer.Finished || player == null || cues.Count == 0) throw new Exception("真实直播测试未保持运行：" + status.Text);
            double position = await player.PositionAsync(CancellationToken.None) ?? 0;
            if (position < 45 || buffer.Edge < 60) throw new Exception("真实直播画面没有持续推进：" + status.Text);
            cache = buffer.Folder;
            await player.ScreenshotAsync(Path.Combine(settings.ModelFolder, "live-video.png"), CancellationToken.None, "window");
            using (var image = new Bitmap(Width, Height))
            {
                DrawToBitmap(image, new Rectangle(Point.Empty, Size));
                using var frame = Image.FromFile(Path.Combine(settings.ModelFolder, "live-video.png"));
                using var graphics = Graphics.FromImage(image);
                var origin = PointToClient(video.PointToScreen(Point.Empty)); origin.Offset((Width - ClientSize.Width) / 2, Height - ClientSize.Height - (Width - ClientSize.Width) / 2);
                graphics.DrawImage(frame, new Rectangle(origin, video.ClientSize)); image.Save(Path.Combine(settings.ModelFolder, "live-window.png"));
            }
            await File.WriteAllTextAsync(Path.Combine(settings.ModelFolder, "live-ui-test.txt"), $"{DateTimeOffset.Now:O}\nPASS: room {BilibiliResolver.RoomId(input)} real stream kept running for 90s; Windows roots with TLS verification enabled\n{status.Text}\n{timing.Text}\nRecognized cue groups: {cues.Count}\nLive comments: {comments.ReceivedCount}; status: {comments.ConnectionStatus}\nActual quality: {actualQuality}; options: {string.Join(",", quality.Items.Cast<BilibiliQuality>().Select(x => x.Id))}\nTranslation: mock API (500ms), no real translation quota used.\n");
        }
        finally { await StopAsync(); await run; }
        if (cache.Length == 0 || Directory.Exists(cache)) throw new Exception("真实直播测试缓存未清理。");
        await File.AppendAllTextAsync(Path.Combine(settings.ModelFolder, "live-ui-test.txt"), "PASS: stop closes own media processes and removes session cache.\n");
        var alternative = quality.Items.Cast<BilibiliQuality>().FirstOrDefault(x => x.Id > 0 && x.Id != actualQuality);
        if (alternative == null) return;
        quality.SelectedItem = alternative; var switched = StartAsync(); string switchedCache = "";
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            double position = player == null ? 0 : await player.PositionAsync(CancellationToken.None) ?? 0;
            if (!Running || buffer == null || buffer.Finished || position < 8 || buffer.Edge < 18 || buffer.Folder == cache) throw new Exception("清晰度切换后直播未能恢复：" + status.Text);
            switchedCache = buffer.Folder;
            await File.AppendAllTextAsync(Path.Combine(settings.ModelFolder, "live-ui-test.txt"), $"PASS: reopened with requested quality {alternative.Id}; actual quality {actualQuality}; playback {position:F1}s; buffer {buffer.Edge:F1}s; separate session cache.\n");
        }
        finally { await StopAsync(); await switched; }
        if (switchedCache.Length == 0 || Directory.Exists(switchedCache)) throw new Exception("切换清晰度后的缓存未清理。");
        await File.AppendAllTextAsync(Path.Combine(settings.ModelFolder, "live-ui-test.txt"), "PASS: quality switch then stop cleaned both sessions.\n");
    }
    internal Task TogglePlayerAsync() => Running ? StopAsync() : StartAsync();
    internal async Task VerifyCancelTestAsync(string file)
    {
        var test = RunLocalTestAsync(file, new BilibiliTests.MockTranslation());
        await Task.Delay(2500); string cache = CacheFolder;
        var stop = StopAsync();
        if (await Task.WhenAny(stop, Task.Delay(10000)) != stop) throw new Exception("停止直播超时。");
        await stop; await test;
        if (Running || (cache.Length > 0 && Directory.Exists(cache))) throw new Exception("停止后仍有活动直播或缓存。");
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { playerTips.Dispose(); accounts.Dispose(); }
        base.Dispose(disposing);
    }

    private sealed class PlayerRailButton : Button
    {
        private bool hovered, pressed;
        internal bool PointLeft { get; set; }
        internal bool RefreshIcon { get; set; }
        internal PlayerRailButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Color.Black; ForeColor = Color.White; FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
            Margin = Padding.Empty; Padding = Padding.Empty; TabStop = true; Cursor = Cursors.Hand; UseVisualStyleBackColor = false;
        }
        protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovered = pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var graphics = e.Graphics; graphics.Clear(pressed ? Color.FromArgb(52, 56, 62) : hovered ? Color.FromArgb(30, 33, 38) : Color.Black);
            graphics.SmoothingMode = SmoothingMode.AntiAlias; Color glyph = Enabled ? Color.FromArgb(234, 238, 244) : Color.FromArgb(96, 103, 113);
            using var pen = new Pen(glyph, 2.1f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round }; float x = Width / 2f, y = Height / 2f;
            if (RefreshIcon)
            {
                graphics.DrawArc(pen, x - 7, y - 7, 14, 14, -80, 290);
                graphics.DrawLines(pen, new[] { new PointF(x - 7.5f, y - 1), new PointF(x - 6, y - 4), new PointF(x - 3, y - 2.5f) });
            }
            else
            {
                float direction = PointLeft ? -1 : 1;
                graphics.DrawLines(pen, new[] { new PointF(x - direction * 3, y - 6), new PointF(x + direction * 3, y), new PointF(x - direction * 3, y + 6) });
            }
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(ClientRectangle, -3, -3), glyph, Color.Black);
        }
    }
}
