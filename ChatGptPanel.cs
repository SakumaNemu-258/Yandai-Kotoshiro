using System.Diagnostics;

namespace Kotoshiro;

internal sealed class ChatGptPanel : UserControl
{
    private readonly ChatGptAuth auth = new();
    private readonly ComboBox accounts = Theme.Combo(), models = Theme.Combo();
    private readonly Label status = Theme.Label("登录后读取该账号可用的订阅模型。", true);
    private readonly Button login = Theme.Button("Continue with ChatGPT", true), add = Theme.Button("添加账号"), refresh = Theme.Button("刷新模型"), logout = Theme.Button("退出登录");
    private readonly Button test = Theme.Button("测试订阅翻译");
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? operation;
    private bool loading;
    public bool IsBusy => operation != null;
    public event Action<bool>? BusyChanged;
    public string SelectedAccountId => (accounts.SelectedItem as ChatGptAccount)?.Id ?? "";
    public string SelectedModelId => (models.SelectedItem as ChatGptModel)?.Slug ?? "";
    public ChatGptPanel(AppSettings settings)
    {
        AutoSize = true; Dock = DockStyle.Top; BackColor = Theme.Background; ForeColor = Theme.Text;
        var table = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2, Padding = new Padding(0), RowCount = 9 };
        table.ColumnStyles.Add(new(SizeType.Absolute, 135)); table.ColumnStyles.Add(new(SizeType.Percent, 100));
        for (int i = 0; i < 9; i++) table.RowStyles.Add(new(SizeType.AutoSize));
        void Row(int row, string text, Control control) { table.Controls.Add(Theme.Label(text), 0, row); control.Margin = new Padding(3, 8, 3, 8); table.Controls.Add(control, 1, row); }
        Row(0, "ChatGPT 账号", accounts);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        actions.Controls.AddRange([login, add, logout]); table.Controls.Add(actions, 1, 1);
        Row(2, "订阅可用模型", models);
        var modelActions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill }; modelActions.Controls.AddRange([refresh, test]); table.Controls.Add(modelActions, 1, 3);
        table.Controls.Add(status, 1, 4);
        var usage = Theme.Button("管理订阅用量"); usage.Click += (_, _) => OpenBrowser(new Uri(ChatGptAuth.UsageUrl)); table.Controls.Add(usage, 1, 5);
        table.Controls.Add(Theme.Label("密码只在 OpenAI 官方网页输入。此模式使用授权的 ChatGPT 订阅额度，不需要 API 密钥。额度或账号资格以官方页面为准。", true), 1, 6);
        table.Controls.Add(Theme.Label("登录与退出会立即生效；关闭设置或点击取消不会撤销这些账号操作。", true), 1, 7);
        Controls.Add(table);
        ReloadAccounts(settings.ChatGptAccountId, settings.ChatGptModel);
        accounts.SelectedIndexChanged += async (_, _) => { if (!loading && !IsBusy) await RunAsync(RefreshModelsAsync); };
        login.Click += async (_, _) => await RunAsync(token => LoginAsync(SelectedAccountId.Length > 0 ? SelectedAccountId : null, token));
        add.Click += async (_, _) => await RunAsync(token => LoginAsync(null, token));
        refresh.Click += async (_, _) => await RunAsync(RefreshModelsAsync);
        logout.Click += async (_, _) => await RunAsync(async token =>
        {
            string id = SelectedAccountId; if (id.Length == 0) return;
            bool revoked = await auth.SignOutAsync(id, token); ReloadAccounts(id, "");
            status.Text = revoked ? "已退出，服务器授权已撤销。" : "已清除本机登录。服务器撤销未确认，请到 ChatGPT 账号设置检查授权。";
        });
        test.Click += async (_, _) => await RunAsync(async token =>
        {
            ValidateSelection(); status.Text = "正在使用此账号的订阅额度翻译一句问候…";
            var current = new AppSettings { ApiProvider = "chatgpt", ApiProtocol = "responses", ChatGptAccountId = SelectedAccountId, ChatGptModel = SelectedModelId };
            using var translator = new ApiTranslator(current, auth: auth);
            string text = await translator.TranslateAsync("こんにちは。今日は一緒にゲームをしましょう。", token, false);
            status.Text = $"订阅翻译成功 · {translator.LastTiming!.TotalSeconds:F2} 秒\n{text}";
        });
        UpdateButtons();
    }
    private void ReloadAccounts(string id, string model)
    {
        loading = true;
        try
        {
            accounts.Items.Clear(); accounts.Items.AddRange(auth.Accounts().ToArray());
            accounts.SelectedItem = accounts.Items.Cast<ChatGptAccount>().FirstOrDefault(x => x.Id == id);
            LoadModels((accounts.SelectedItem as ChatGptAccount)?.Models ?? [], model);
            ShowAccountStatus();
        }
        finally { loading = false; }
    }
    private void LoadModels(List<ChatGptModel> catalog, string selected)
    {
        models.Items.Clear(); models.Items.AddRange(catalog.ToArray());
        models.SelectedItem = catalog.FirstOrDefault(x => x.Slug == selected);
        if (models.SelectedIndex < 0 && models.Items.Count > 0) models.SelectedIndex = 0;
    }
    private void ShowAccountStatus()
    {
        status.Text = accounts.SelectedItem is not ChatGptAccount account ? "点击 Continue with ChatGPT 登录并授权订阅额度。" : !account.IsSignedIn ? "此账号已退出。可重新登录，也可添加其他账号。" : !account.CanUsePlan ? "账号已登录，但未获得订阅额度授权。请重新授权。" : "正在使用 ChatGPT 订阅 · " + account.Label;
    }
    private async Task LoginAsync(string? id, CancellationToken token)
    {
        status.Text = "请在浏览器完成 OpenAI 官方登录与订阅授权（最多等待 5 分钟）…";
        var account = await auth.LoginAsync(id, uri => { OpenBrowser(uri); return Task.CompletedTask; }, token);
        ReloadAccounts(account.Id, "");
        if (!account.CanUsePlan) { ShowAccountStatus(); return; }
        if (!account.Welcomed)
        {
            MessageBox.Show(FindForm(), "正在使用 ChatGPT 订阅\n\n翻译会使用该账号授权给言代Kotoshiro 的订阅额度。可点击“管理订阅用量”查看限额与用量。", "ChatGPT 登录成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
            await auth.MarkWelcomedAsync(account.Id, token);
        }
        await RefreshModelsAsync(token);
    }
    private async Task RefreshModelsAsync(CancellationToken token)
    {
        ShowAccountStatus();
        if (accounts.SelectedItem is not ChatGptAccount account || !account.CanUsePlan) { models.Items.Clear(); return; }
        string previous = SelectedModelId; status.Text = "正在获取此账号可用的订阅模型…";
        var catalog = await auth.ModelsAsync(account.Id, token);
        ReloadAccounts(account.Id, previous);
        if (catalog.Count == 0) status.Text = "账号未返回可用的订阅模型。请检查账号资格或稍后刷新。";
    }
    public void ValidateSelection()
    {
        var account = auth.Accounts().FirstOrDefault(x => x.Id == SelectedAccountId);
        if (account?.CanUsePlan != true) throw new InvalidOperationException("请先登录 ChatGPT，并授权订阅额度。");
        if (SelectedModelId.Length == 0 || !account.Models.Any(x => x.Slug == SelectedModelId)) throw new InvalidOperationException("请刷新并选择该账号可用的订阅模型。");
    }
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); UpdateButtons(); BusyChanged?.Invoke(true);
        try { await action(operation.Token); }
        catch (OperationCanceledException) { if (!IsDisposed) status.Text = "操作已取消或等待超时，可重新尝试。"; }
        catch (Exception ex) { if (!IsDisposed) status.Text = ex.Message; }
        finally
        {
            operation.Dispose(); operation = null;
            if (IsDisposed) auth.Dispose();
            if (!IsDisposed) { UpdateButtons(); BusyChanged?.Invoke(false); }
        }
    }
    private void UpdateButtons()
    {
        accounts.Enabled = models.Enabled = login.Enabled = add.Enabled = !IsBusy;
        logout.Enabled = !IsBusy && (accounts.SelectedItem as ChatGptAccount)?.IsSignedIn == true;
        refresh.Enabled = test.Enabled = !IsBusy && (accounts.SelectedItem as ChatGptAccount)?.CanUsePlan == true;
    }
    internal static void OpenBrowser(Uri uri)
    {
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { throw new InvalidOperationException("无法打开默认浏览器，请在 Windows 设置默认浏览器后重试。"); }
    }
    public void Cancel() => lifetime.Cancel();
    internal void SelectForPreview(string id, string model) { ReloadAccounts(id, model); UpdateButtons(); }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { lifetime.Cancel(); if (!IsBusy) auth.Dispose(); lifetime.Dispose(); }
        base.Dispose(disposing);
    }
}
