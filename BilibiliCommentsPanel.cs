using System.Collections.Concurrent;

namespace Kotoshiro;

internal sealed class BilibiliCommentsPanel : UserControl
{
    private readonly BilibiliAccountStore accounts;
    private readonly Label accountLabel = Theme.Label("未登录，可接收公开弹幕", true);
    private readonly Label connectionLabel = Theme.Label("打开直播后连接弹幕", true);
    private readonly Button login = Theme.Button("登录B站"), logout = Theme.Button("退出"), send = Theme.Button("发送", true);
    private readonly RichTextBox messages = new() { ReadOnly = true, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Theme.Surface, ForeColor = Theme.Text, DetectUrls = false, ScrollBars = RichTextBoxScrollBars.Vertical };
    private readonly TextBox message = Theme.Input();
    private readonly CheckBox follow = new() { Text = "跟随新弹幕", Checked = true, AutoSize = true };
    private readonly ConcurrentQueue<BilibiliComment> pending = new();
    private readonly System.Windows.Forms.Timer drain = new() { Interval = 200 };
    private CancellationTokenSource? session;
    private BilibiliDanmakuClient? receiver;
    private readonly SemaphoreSlim connectionGate = new(1, 1);
    private CancellationToken parentToken;
    private long roomId;
    private bool sending, disposed;
    internal int ReceivedCount { get; private set; }
    internal string ConnectionStatus => connectionLabel.Text;
    internal bool Preview { get; set; }
    public event Action? AccountChanged;
    internal BilibiliCommentsPanel(BilibiliAccountStore accounts)
    {
        this.accounts = accounts; Theme.Apply(this); Dock = DockStyle.Fill; Padding = new Padding(12, 0, 0, 0);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, BackColor = Theme.Surface, Padding = new Padding(10) };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        for (int i = 0; i < 7; i++) layout.RowStyles.Add(new(i == 3 ? SizeType.Percent : SizeType.AutoSize, i == 3 ? 100 : 0));
        layout.Controls.Add(Theme.Label("直播弹幕"), 0, 0);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false }; buttons.Controls.AddRange([login, logout]);
        logout.MinimumSize = new Size(60, 36); logout.Enabled = false; layout.Controls.Add(buttons, 0, 1);
        var info = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false }; info.Controls.AddRange([accountLabel, connectionLabel]);
        accountLabel.MaximumSize = connectionLabel.MaximumSize = new Size(270, 0); layout.Controls.Add(info, 0, 2); layout.Controls.Add(messages, 0, 3);
        var foot = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        foot.Controls.Add(follow); var note = Theme.Label("弹幕实时显示；视频有翻译延迟。", true); note.MaximumSize = new Size(270, 0); foot.Controls.Add(note); layout.Controls.Add(foot, 0, 4);
        message.PlaceholderText = "登录后发送弹幕"; message.MaxLength = 100; message.Margin = new Padding(0, 6, 0, 6); message.Enabled = send.Enabled = false;
        layout.Controls.Add(message, 0, 5); send.Dock = DockStyle.Fill; layout.Controls.Add(send, 0, 6); Controls.Add(layout);
        drain.Tick += (_, _) => Flush(); drain.Start();
        login.Click += async (_, _) =>
        {
            if (Preview) return;
            try { using var dialog = new BilibiliLoginForm(accounts); dialog.ShowDialog(FindForm()); await RefreshAccountAsync(); AccountChanged?.Invoke(); if (roomId != 0) await RestartAsync(); }
            catch (Exception) { if (!disposed) connectionLabel.Text = "B站账号未能更新，请重试。"; }
        };
        logout.Click += async (_, _) =>
        {
            if (Preview) return; logout.Enabled = false;
            try { await accounts.LogoutAsync(CancellationToken.None); await RefreshAccountAsync(); AccountChanged?.Invoke(); if (roomId != 0) await RestartAsync(); }
            catch { connectionLabel.Text = "退出未完成，请重试。"; }
        };
        send.Click += async (_, _) => await SendAsync();
        message.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await SendAsync(); } };
    }
    internal async Task RefreshAccountAsync()
    {
        BilibiliAccount? account;
        try { account = await accounts.GetAsync(CancellationToken.None); }
        catch { if (!disposed) { accountLabel.Text = "登录信息未能读取，请退出后重新登录"; logout.Enabled = true; message.Enabled = send.Enabled = false; } return; }
        if (disposed) return;
        accountLabel.Text = account == null ? "未登录，可接收公开弹幕" : "已登录：" + account.DisplayName;
        logout.Enabled = account != null && !sending; login.Enabled = !sending; login.Text = account == null ? "登录B站" : "切换账号";
        message.Enabled = send.Enabled = account != null && roomId != 0 && !sending;
        message.PlaceholderText = account == null ? "登录后发送弹幕" : "输入弹幕，Enter 发送";
    }
    internal async Task StartAsync(long id, CancellationToken token)
    {
        await connectionGate.WaitAsync(token);
        try
        {
            await StopCoreAsync(); token.ThrowIfCancellationRequested(); if (disposed) return;
            parentToken = token; roomId = id; session = CancellationTokenSource.CreateLinkedTokenSource(token);
            ReceivedCount = 0; messages.Clear(); while (pending.TryDequeue(out _)) { }
            await RefreshAccountAsync(); var account = await accounts.GetAsync(session.Token); session.Token.ThrowIfCancellationRequested();
            var active = new BilibiliDanmakuClient(); receiver = active;
            active.CommentReceived += comment => { if (receiver == active && pending.Count < 1000) pending.Enqueue(comment); };
            active.StatusChanged += text => UpdateStatus(text, active);
            await active.StartAsync(id, account, session.Token);
        }
        catch (OperationCanceledException) { await StopCoreAsync(); }
        catch { await StopCoreAsync(); if (!disposed) connectionLabel.Text = "弹幕连接未完成，可继续观看视频。"; }
        finally { connectionGate.Release(); }
    }
    private async Task RestartAsync()
    { long id = roomId; var token = parentToken; if (id != 0 && !token.IsCancellationRequested) await StartAsync(id, token); }
    internal async Task StopAsync()
    {
        await connectionGate.WaitAsync();
        try { await StopCoreAsync(); }
        finally { connectionGate.Release(); }
    }
    private async Task StopCoreAsync()
    {
        session?.Cancel(); if (receiver != null) { await receiver.DisposeAsync(); receiver = null; }
        session?.Dispose(); session = null; roomId = 0;
        if (!disposed) { message.Enabled = send.Enabled = false; connectionLabel.Text = "打开直播后连接弹幕"; }
    }
    private async Task SendAsync()
    {
        if (Preview || sending || session == null || roomId == 0) return;
        string text = message.Text.Trim(); if (text.Length == 0) return;
        var token = session.Token; long id = roomId;
        sending = true; send.Enabled = login.Enabled = logout.Enabled = false; message.Enabled = false;
        try
        {
            var account = await accounts.GetAsync(token); if (account == null) { connectionLabel.Text = "请先登录B站账号。"; return; }
            token.ThrowIfCancellationRequested();
            await BilibiliDanmakuSender.SendAsync(id, text, account, token);
            if (message.Text.Trim() == text) message.Clear(); connectionLabel.Text = "弹幕已提交；显示以B站审核结果为准。";
        }
        catch (OperationCanceledException) { if (!disposed) connectionLabel.Text = "发送已停止，结果未确认，请查看弹幕区。"; }
        catch (Exception ex) { if (!disposed) connectionLabel.Text = ex.Message; }
        finally { sending = false; if (!disposed) { login.Enabled = true; await RefreshAccountAsync(); } }
    }
    private void UpdateStatus(string text, BilibiliDanmakuClient active)
    {
        if (disposed || !IsHandleCreated) return;
        try { BeginInvoke(() => { if (!disposed && receiver == active) connectionLabel.Text = text; }); } catch (InvalidOperationException) { }
    }
    private void Flush()
    {
        for (int i = 0; i < 80 && pending.TryDequeue(out var comment); i++)
        {
            ReceivedCount++; int saved = messages.SelectionStart; int length = messages.SelectionLength;
            messages.SelectionStart = messages.TextLength; messages.SelectionLength = 0;
            messages.SelectionColor = Theme.Accent; messages.AppendText(comment.UserName + "\n");
            messages.SelectionColor = Theme.Text; messages.AppendText(comment.Text + "\n\n");
            if (messages.TextLength > 50000)
            {
                int cut = messages.Text.IndexOf('\n', 10000); if (cut > 0) { RichTextHistory.RemovePrefix(messages, cut + 1); saved = Math.Max(0, saved - cut - 1); }
            }
            if (follow.Checked) { messages.SelectionStart = messages.TextLength; messages.ScrollToCaret(); }
            else messages.Select(Math.Min(saved, messages.TextLength), Math.Min(length, Math.Max(0, messages.TextLength - saved)));
        }
    }
    internal void SeedPreview()
    {
        Preview = true; accountLabel.Text = "未登录，可接收公开弹幕"; connectionLabel.Text = "界面示例 · 未连接直播";
        pending.Enqueue(new(1001, "观众小明", "晚上好！今天玩什么？")); pending.Enqueue(new(1002, "日语学习中", "字幕终于能和视频一起看了")); pending.Enqueue(new(1003, "直播观众", "右侧是实时弹幕，视频会延后播放。")); Flush();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { disposed = true; drain.Stop(); drain.Dispose(); session?.Cancel(); }
        base.Dispose(disposing);
    }
}
