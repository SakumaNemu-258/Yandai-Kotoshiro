using QRCoder;

namespace Kotoshiro;

public sealed class BilibiliLoginForm : Form
{
    private readonly BilibiliAccountStore store;
    private readonly bool preview;
    private readonly PictureBox qr = new() { Width = 272, Height = 272, SizeMode = PictureBoxSizeMode.CenterImage, BackColor = Color.White, Anchor = AnchorStyles.None };
    private readonly Label accountLabel = Theme.Label("尚未登录 B 站");
    private readonly Label state = Theme.Label("正在准备登录…", true);
    private readonly Button refresh = Theme.Button("生成二维码", true);
    private readonly Button logout = Theme.Button("退出账号");
    private readonly Button done = Theme.Button("关闭");
    private CancellationTokenSource? operation;
    public BilibiliAccount? Account { get; private set; }

    public BilibiliLoginForm(BilibiliAccountStore store, bool preview = false)
    {
        this.store = store; this.preview = preview;
        Theme.Apply(this); Text = "言代Kotoshiro · B 站账号"; StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(500, 520); MinimumSize = MaximumSize = Size; MaximizeBox = false; MinimizeBox = false;
        Padding = new Padding(24, 18, 24, 18);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6 };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 40)); layout.RowStyles.Add(new(SizeType.Absolute, 55));
        layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 57));
        layout.RowStyles.Add(new(SizeType.Absolute, 30)); layout.RowStyles.Add(new(SizeType.Absolute, 45));
        accountLabel.Dock = DockStyle.Fill; accountLabel.TextAlign = ContentAlignment.MiddleCenter;
        layout.Controls.Add(accountLabel, 0, 0);
        var instruction = Theme.Label("使用 B 站手机客户端扫码，并在手机上确认登录。\n无需输入密码；登录信息由 Windows 当前用户加密保存。", true);
        instruction.Dock = DockStyle.Fill; instruction.TextAlign = ContentAlignment.MiddleCenter; layout.Controls.Add(instruction, 0, 1);
        layout.Controls.Add(qr, 0, 2);
        state.Dock = DockStyle.Fill; state.TextAlign = ContentAlignment.MiddleCenter; state.AutoSize = false; layout.Controls.Add(state, 0, 3);
        var note = Theme.Label("退出仅清除言代Kotoshiro 保存的 B 站登录信息。", true);
        note.Dock = DockStyle.Fill; note.TextAlign = ContentAlignment.MiddleCenter; layout.Controls.Add(note, 0, 4);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        buttons.Controls.AddRange([refresh, logout, done]); layout.Controls.Add(buttons, 0, 5); Controls.Add(layout);
        done.Click += (_, _) => Close(); CancelButton = done;
        refresh.Click += async (_, _) => { if (!preview) await StartQrAsync(); };
        logout.Click += async (_, _) => { if (!preview) await LogoutAsync(); };
        Shown += async (_, _) =>
        {
            if (preview) { state.Text = "二维码界面预览；实际登录时将生成 B 站二维码。"; SetQr("https://www.bilibili.com/"); logout.Enabled = false; return; }
            try
            {
                Account = await store.GetAsync(); ShowAccount();
                if (Account == null) await StartQrAsync(); else state.Text = "已保存登录；可退出账号，或重新扫码切换账号。";
            }
            catch (Exception ex) { state.Text = SafeMessage(ex); logout.Enabled = true; }
        };
        FormClosing += (_, _) => operation?.Cancel();
    }

    private void ShowAccount()
    {
        accountLabel.Text = Account == null ? "尚未登录 B 站" : "已登录：" + Account.DisplayName;
        logout.Enabled = Account != null; refresh.Text = Account == null ? "刷新二维码" : "重新扫码";
    }
    private async Task StartQrAsync()
    {
        operation?.Cancel();
        var attempt = new CancellationTokenSource(); operation = attempt;
        refresh.Enabled = false; SetQr(null); state.Text = "正在向 B 站申请登录二维码…";
        try
        {
            var ticket = await store.BeginQrAsync(attempt.Token);
            if (operation != attempt || IsDisposed) return;
            SetQr(ticket.Url); refresh.Enabled = true; state.Text = "请使用 B 站手机客户端扫码。二维码约 3 分钟内有效。";
            while (true)
            {
                await Task.Delay(2000, attempt.Token);
                var result = await store.PollQrAsync(ticket, attempt.Token);
                attempt.Token.ThrowIfCancellationRequested();
                if (operation != attempt || IsDisposed) return;
                switch (result.Status)
                {
                    case BilibiliQrStatus.Waiting: break;
                    case BilibiliQrStatus.Scanned: state.Text = "扫码成功，请在手机上确认登录。"; break;
                    case BilibiliQrStatus.Expired: SetQr(null); state.Text = "二维码已过期，请点击“刷新二维码”。"; return;
                    case BilibiliQrStatus.LoggedIn:
                        Account = result.Account; ShowAccount(); SetQr(null); state.Text = "登录成功。返回播放器后即可输入并发送弹幕。"; done.Text = "完成"; return;
                }
            }
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested) { }
        catch (OperationCanceledException) { if (operation == attempt && !IsDisposed) { SetQr(null); state.Text = "B 站登录请求超时，请刷新二维码重试。"; } }
        catch (Exception ex) { if (operation == attempt && !IsDisposed) { SetQr(null); state.Text = SafeMessage(ex); } }
        finally
        {
            if (operation == attempt) { operation = null; if (!IsDisposed) refresh.Enabled = true; }
            attempt.Dispose();
        }
    }
    private async Task LogoutAsync()
    {
        operation?.Cancel(); refresh.Enabled = logout.Enabled = false;
        try { await store.LogoutAsync(); Account = null; SetQr(null); ShowAccount(); state.Text = "已退出 B 站账号。点击“刷新二维码”重新登录。"; }
        catch (Exception ex) { state.Text = SafeMessage(ex); }
        finally { refresh.Enabled = true; }
    }
    private static string SafeMessage(Exception ex) => ex is HttpRequestException or InvalidDataException or InvalidOperationException
        ? ex.Message : "无法完成 B 站登录，请检查网络或本机数据目录后重试。";
    private void SetQr(string? value)
    {
        Image? previous = qr.Image; qr.Image = null; previous?.Dispose();
        if (value == null) return;
        using var generator = new QRCodeGenerator(); using var data = generator.CreateQrCode(value, QRCodeGenerator.ECCLevel.Q);
        // Integer pixels preserve QR module edges and the mandatory quiet zone.
        int pixels = Math.Max(1, 260 / data.ModuleMatrix.Count);
        using var renderer = new QRCode(data); qr.Image = renderer.GetGraphic(pixels, Color.Black, Color.White, true);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { operation?.Cancel(); qr.Image?.Dispose(); qr.Image = null; }
        base.Dispose(disposing);
    }
}
