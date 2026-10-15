namespace Kotoshiro;

public sealed class SettingsForm : Form
{
    public AppSettings Result { get; private set; }
    private readonly TextBox key, address, glossary;
    private readonly ComboBox provider, protocol, apiModel;
    private readonly Button fetchModels;
    private readonly Label modelStatus;
    private readonly SettingsDeck pages;
    private CancellationTokenSource? modelFetch;
    private long modelGeneration;
    private readonly Dictionary<string, ApiProfile> apiDrafts;
    private string activeProvider;
    private readonly ChatGptPanel chatgpt;
    private readonly TextBox modelFolder;
    private readonly Button browseFolder, migrateModels;
    private string migrationSource;
    private readonly ComboBox recognition;
    private readonly ComboBox recognitionMode, speechProvider, speechModel, themeChoice;
    private readonly TextBox speechAddress, speechKey;
    private readonly Label speechStatus, speechHint;
    private readonly Button speechTest, fetchSpeechModels;
    private readonly Label speechModelStatus;
    private CancellationTokenSource? speechModelFetch;
    private long speechModelGeneration;
    private bool busy, closing;
    private HttpMessageHandler? previewSpeechModelsHandler;
    private Task? speechModelFetchTask;
    private readonly CheckBox gpu, showJapanese;
    private readonly NumericUpDown threads, seconds, fontSize, opacity, translationContext;
    private readonly Button importGlossary, exportGlossary;
    private string? previewGlossaryImportPath, previewGlossaryExportPath;
    private readonly Label downloadStatus, testStatus;
    private readonly Label gpuStatus;
    private readonly Button enableGpu;
    private readonly Button download, test;
    private CancellationTokenSource? operation;
    private readonly Button save;
    public SettingsForm(AppSettings original)
    {
        Result = original.Clone(); Theme.Apply(this); Text = "言代Kotoshiro · 设置";
        ApiProviders.Normalize(Result); activeProvider = Result.ApiProvider;
        apiDrafts = new(Result.ApiProfiles); apiDrafts[activeProvider] = new(Result.ApiBase, Result.ApiModel, Result.EncryptedKey, Result.ApiProtocol);
        SettingsStore.SetModelDirectory(Result.ModelFolder);
        migrationSource = string.IsNullOrWhiteSpace(Result.ModelFolder) ? SettingsStore.LegacyModelDirectory : Result.ModelFolder;
        ClientSize = new Size(980, 800); MinimumSize = new Size(860, 660); StartPosition = FormStartPosition.CenterParent;
        pages = new SettingsDeck { Dock = DockStyle.Fill, BackColor = Theme.Background };
        var api = Page("翻译服务", "选择文字翻译服务。语音识别方式在“语音识别”中单独设置。", pages);
        provider = Theme.Combo(); provider.Items.AddRange(ApiProviders.All); provider.SelectedItem = ApiProviders.Get(activeProvider); Row(api, 1, "翻译平台", provider);
        var apiFields = FieldTable(9);
        api.Controls.Add(apiFields, 0, 2); api.SetColumnSpan(apiFields, 2);
        protocol = Theme.Combo(); protocol.Items.AddRange(ApiProviders.Protocols); protocol.SelectedItem = ApiProviders.Protocols.First(x => x.Id == Result.ApiProtocol); Row(apiFields, 0, "接口类型", protocol);
        address = Theme.Input(Result.ApiBase); Row(apiFields, 1, "API 地址", address);
        apiModel = Theme.Combo(editable: true); apiModel.Text = Result.ApiModel;
        apiModel.AutoCompleteMode = AutoCompleteMode.SuggestAppend; apiModel.AutoCompleteSource = AutoCompleteSource.ListItems;
        fetchModels = Theme.Button("获取模型"); fetchModels.Margin = Padding.Empty; fetchModels.Click += async (_, _) => await FetchModelsAsync();
        var modelRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty };
        modelRow.ColumnStyles.Add(new(SizeType.Percent, 100)); modelRow.ColumnStyles.Add(new(SizeType.Absolute, 116));
        apiModel.Margin = new Padding(0, 5, 12, 0); modelRow.Controls.Add(apiModel, 0, 0); modelRow.Controls.Add(fetchModels, 1, 0);
        Row(apiFields, 2, "翻译模型", modelRow);
        modelStatus = Theme.Label("从下拉列表选择，也可以直接填写模型 ID。", true); apiFields.Controls.Add(modelStatus, 1, 3);
        key = Theme.Input(Result.GetKey()); key.UseSystemPasswordChar = true; Row(apiFields, 4, "API 密钥", key);
        var reveal = new CheckBox { Text = "显示密钥", AutoSize = true, Margin = new Padding(0, 4, 0, 8) }; reveal.CheckedChanged += (_, _) => key.UseSystemPasswordChar = !reveal.Checked; apiFields.Controls.Add(reveal, 1, 5);
        apiFields.Controls.Add(Theme.Label("各平台分别保存设置，密钥由 Windows 当前用户加密保护。", true), 1, 6);
        test = Theme.Button("测试连接"); test.Click += async (_, _) => await TestAsync(); apiFields.Controls.Add(test, 1, 7);
        testStatus = Theme.Label("测试翻译一句“こんにちは”，产生少量 API 用量。", true); apiFields.Controls.Add(testStatus, 1, 8);
        PopulateModels(ApiModels.Defaults(activeProvider), Result.ApiModel);
        void InvalidateModelRequest()
        {
            modelGeneration++; modelFetch?.Cancel(); fetchModels.Enabled = true;
            modelStatus.Text = "从下拉列表选择，也可以直接填写模型 ID。";
        }
        address.TextChanged += (_, _) => InvalidateModelRequest();
        key.TextChanged += (_, _) => InvalidateModelRequest();
        protocol.SelectedIndexChanged += (_, _) => InvalidateModelRequest();
        foreach (Control field in apiFields.Controls) field.Margin = new Padding(0, 4, 0, 4);
        chatgpt = new ChatGptPanel(Result); api.Controls.Add(chatgpt, 0, 3); api.SetColumnSpan(chatgpt, 2);
        chatgpt.BusyChanged += enabled => Busy(enabled);
        apiFields.Visible = activeProvider != "chatgpt"; chatgpt.Visible = activeProvider == "chatgpt";
        translationContext = Number(Result.NormalizedTranslationContextSegments, 0, 30); Row(api, 4, "翻译上下文段数", translationContext);
        api.Controls.Add(Theme.Label("最近 N 段成功原文和译文，0 表示不带前文。更多前文可能改善连贯，但增加 API 输入用量，可能更慢。保存后下次开始生效；停止重开或刷新后清空。可先试 6 段。", true), 1, 5);
        glossary = Theme.Input(Result.Glossary); glossary.Multiline = true; glossary.Height = 112; glossary.ScrollBars = ScrollBars.Vertical; Row(api, 6, "术语表", glossary);
        var glossaryActions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0, 2, 0, 0) };
        importGlossary = Theme.Button("导入 TXT"); importGlossary.Click += (_, _) => ImportGlossary(); glossaryActions.Controls.Add(importGlossary);
        exportGlossary = Theme.Button("导出 TXT"); exportGlossary.Click += (_, _) => ExportGlossary(); glossaryActions.Controls.Add(exportGlossary);
        api.Controls.Add(glossaryActions, 1, 7);
        api.Controls.Add(Theme.Label("每行填写“日语 → 中文”。导入合并并去重；同词不同译文请手动确认。导出当前内容为 UTF-8 TXT。保存设置后用于翻译。", true), 1, 8);
        provider.SelectedIndexChanged += (_, _) =>
        {
            apiDrafts[activeProvider] = CaptureApiProfile();
            var selected = (ApiProviderInfo)provider.SelectedItem!; activeProvider = selected.Id;
            var draft = apiDrafts.GetValueOrDefault(activeProvider) ?? new(selected.Address, selected.Model, "", selected.Protocol);
            InvalidateModelRequest();
            address.Text = draft.Address; PopulateModels(ApiModels.Defaults(activeProvider), draft.Model);
            var secret = new AppSettings { EncryptedKey = draft.EncryptedKey }; key.Text = secret.GetKey(); reveal.Checked = false;
            protocol.SelectedItem = ApiProviders.Protocols.FirstOrDefault(x => x.Id == draft.Protocol) ?? ApiProviders.Protocols[0];
            testStatus.Text = "请填写该平台的模型和密钥，再测试连接。";
            apiFields.Visible = activeProvider != "chatgpt"; chatgpt.Visible = activeProvider == "chatgpt";
        };

        var recognitionPage = Page("语音识别", "本地识别适合离线使用；API 识别适合性能较弱的电脑。", pages);
        recognitionMode = Theme.Combo(); recognitionMode.Items.AddRange(new object[] { "本地识别", "API 识别" }); recognitionMode.SelectedIndex = Result.RecognitionMode == "api" ? 1 : 0; Row(recognitionPage, 1, "识别方式", recognitionMode);
        var storage = FieldTable(2); recognitionPage.Controls.Add(storage, 0, 2); recognitionPage.SetColumnSpan(storage, 2);
        var speechFields = FieldTable(9);
        recognitionPage.Controls.Add(speechFields, 0, 3); recognitionPage.SetColumnSpan(speechFields, 2);
        speechProvider = Theme.Combo(); speechProvider.Items.AddRange(new object[] { "OpenAI", "自定义兼容 API" }); speechProvider.SelectedIndex = Result.SpeechApiProvider == "custom" ? 1 : 0; Row(speechFields, 0, "识别平台", speechProvider);
        speechAddress = Theme.Input(Result.SpeechApiBase); Row(speechFields, 1, "识别 API 地址", speechAddress);
        speechModel = Theme.Combo(editable: true); speechModel.Text = Result.SpeechApiModel;
        speechModel.AutoCompleteMode = AutoCompleteMode.SuggestAppend; speechModel.AutoCompleteSource = AutoCompleteSource.ListItems;
        fetchSpeechModels = Theme.Button("获取模型"); fetchSpeechModels.Margin = Padding.Empty;
        fetchSpeechModels.Click += async (_, _) => { speechModelFetchTask = FetchSpeechModelsAsync(); await speechModelFetchTask; };
        var speechModelRow = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty };
        speechModelRow.ColumnStyles.Add(new(SizeType.Percent, 100)); speechModelRow.ColumnStyles.Add(new(SizeType.Absolute, 116));
        speechModel.Margin = new Padding(0, 5, 12, 0); speechModelRow.Controls.Add(speechModel, 0, 0); speechModelRow.Controls.Add(fetchSpeechModels, 1, 0);
        Row(speechFields, 2, "识别模型", speechModelRow);
        speechModelStatus = Theme.Label(SpeechModelInstructions, true); speechFields.Controls.Add(speechModelStatus, 1, 3);
        speechKey = Theme.Input(Result.GetSpeechKey()); speechKey.UseSystemPasswordChar = true; Row(speechFields, 4, "识别 API 密钥", speechKey);
        var revealSpeech = new CheckBox { Text = "显示识别密钥", AutoSize = true }; revealSpeech.CheckedChanged += (_, _) => speechKey.UseSystemPasswordChar = !revealSpeech.Checked; speechFields.Controls.Add(revealSpeech, 1, 5);
        speechHint = Theme.Label("", true); speechFields.Controls.Add(speechHint, 1, 6);
        speechTest = Theme.Button("用录音测试 API 识别"); speechTest.Click += async (_, _) => await TestSpeechAsync(); speechFields.Controls.Add(speechTest, 1, 7);
        speechStatus = Theme.Label("选择日语录音测试，会使用识别 API 的少量用量。", true); speechFields.Controls.Add(speechStatus, 1, 8);
        speechAddress.TextChanged += (_, _) => InvalidateSpeechModelRequest();
        speechKey.TextChanged += (_, _) => InvalidateSpeechModelRequest();
        speechProvider.SelectedIndexChanged += (_, _) =>
        {
            InvalidateSpeechModelRequest();
            UpdateSpeechModels();
            if (speechProvider.SelectedIndex == 0 && !SpeechAddressIsOpenAi()) { speechAddress.Text = "https://api.openai.com/v1"; speechKey.Text = ""; speechStatus.Text = "请填写 OpenAI 识别密钥；翻译密钥不会自动用于识别。"; }
            else if (speechProvider.SelectedIndex == 1) speechStatus.Text = "选择日语录音测试，会使用识别 API 的少量用量。";
        };
        UpdateSpeechModels();
        var local = FieldTable(14);
        recognitionPage.Controls.Add(local, 0, 4); recognitionPage.SetColumnSpan(local, 2);
        var recognitionHint = Theme.Label("API 识别无需下载本地模型或启用显卡。系统声音模式可不选文件夹；B 站延迟播放需文件夹存放临时视频缓存。", true); recognitionPage.Controls.Add(recognitionHint, 0, 5); recognitionPage.SetColumnSpan(recognitionHint, 2);
        void SelectRecognitionMode() { bool remote = recognitionMode.SelectedIndex == 1; speechFields.Visible = remote; local.Visible = !remote; recognitionHint.Visible = remote; RefreshSpeechModelFetchButton(); }
        recognitionMode.SelectedIndexChanged += (_, _) => { InvalidateSpeechModelRequest(); SelectRecognitionMode(); }; SelectRecognitionMode();
        modelFolder = Theme.Input(Result.ModelFolder); modelFolder.ReadOnly = true; modelFolder.PlaceholderText = "本地模型 / 视频缓存，例如 D:\\Kotoshiro模型"; Row(storage, 0, "文件存放位置", modelFolder);
        var storageActions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        browseFolder = Theme.Button("选择文件夹"); browseFolder.Click += (_, _) => ChooseFolder(); storageActions.Controls.Add(browseFolder);
        migrateModels = Theme.Button("迁移现有模型"); migrateModels.Click += async (_, _) => await MigrateModelsAsync(); storageActions.Controls.Add(migrateModels); storage.Controls.Add(storageActions, 1, 1);
        recognition = Theme.Combo(); recognition.Items.AddRange(Models.All); recognition.SelectedItem = Models.Get(Result.RecognitionModel); Row(local, 3, "识别模型", recognition);
        download = Theme.Button("下载所选模型"); download.Click += async (_, _) => await DownloadAsync(); local.Controls.Add(download, 1, 4);
        downloadStatus = Theme.Label("", true); local.Controls.Add(downloadStatus, 1, 5);
        recognition.SelectedIndexChanged += (_, _) => UpdateModelStatus(); UpdateModelStatus();
        var gpuActions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        enableGpu = Theme.Button("一键启用显卡", true); enableGpu.Click += async (_, _) => await EnableGpuAsync(); gpuActions.Controls.Add(enableGpu);
        gpu = new CheckBox { Text = "优先使用显卡", AutoSize = true, Checked = Result.UseGpu, Margin = new Padding(12, 10, 0, 0) }; gpuActions.Controls.Add(gpu);
        local.Controls.Add(gpuActions, 1, 6);
        gpuStatus = Theme.Label("正在检测显卡…", true); local.Controls.Add(gpuStatus, 1, 7);
        Shown += async (_, _) =>
        {
            string? name = await GpuSetup.DetectNvidiaAsync();
            if (IsDisposed || operation != null) return;
            gpuStatus.Text = name == null ? "未检测到可用的 NVIDIA 显卡，仍可使用 CPU。" : $"已检测到 {name} · " + (GpuSetup.IsInstalled() ? "显卡组件已就绪。" : "点击上方按钮即可启用。首次下载约 436 MB。");
        };
        threads = Number(Result.Threads, 1, 32); Row(local, 8, "CPU 线程", threads);
        seconds = Number(Result.SegmentSeconds, 3, 12); Row(recognitionPage, 6, "最长分段 / 秒", seconds);
        local.Controls.Add(Theme.Label("显卡组件也放在所选文件夹中，无需安装 CUDA 或复制文件。显卡不可用时自动使用 CPU。", true), 1, 10);
        var folder = Theme.Button("打开模型目录"); folder.Click += (_, _) => { if (!EnsureFolder()) return; Directory.CreateDirectory(SettingsStore.ModelDirectory); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", SettingsStore.ModelDirectory) { UseShellExecute = true }); }; local.Controls.Add(folder, 1, 11);
        var benchmark = Theme.Button("用日语录音测试识别"); benchmark.Click += async (_, _) =>
        {
            if (operation != null || !EnsureFolder()) return;
            using var picker = new OpenFileDialog { Title = "选择一段日语录音（前 30 秒）", Filter = "音频文件|*.wav;*.mp3;*.flac;*.m4a" };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                var current = ReadRecognitionFields(); operation = new(); Busy(true); benchmark.Enabled = false; downloadStatus.Text = "正在测试本机识别速度…";
                var wave = AudioFiles.Read(picker.FileName, 30);
                using var engine = new WhisperEngine(); await engine.StartAsync(current, operation.Token);
                var timer = System.Diagnostics.Stopwatch.StartNew(); string text = await engine.TranscribeAsync(wave.Bytes, operation.Token);
                if (!IsDisposed) MessageBox.Show(this, $"运行方式：{engine.Mode}\n音频：{wave.Duration:F1} 秒\n识别耗时：{timer.Elapsed.TotalSeconds:F1} 秒（不含模型加载和翻译）\n\n{text}\n\n若识别耗时接近或超过音频时长，请换较小模型。", "本机识别测试");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!IsDisposed) downloadStatus.Text = ex.Message; }
            finally { if (!IsDisposed) { Busy(false); benchmark.Enabled = true; } operation?.Dispose(); operation = null; }
        }; local.Controls.Add(benchmark, 1, 12);
        var pipelineBenchmark = Theme.Button("用录音测试识别与翻译耗时"); pipelineBenchmark.Click += async (_, _) =>
        {
            if (operation != null || !EnsureFolder()) return;
            using var picker = new OpenFileDialog { Title = "选择日语录音（前 30 秒；会使用 API 翻译一次）", Filter = "音频文件|*.wav;*.mp3;*.flac;*.m4a" };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                var current = ReadFields(); operation = new(); Busy(true); pipelineBenchmark.Enabled = false;
                var report = await LatencyDiagnostics.MeasureAsync(current, picker.FileName, 1, true, new Progress<string>(text => { if (!IsDisposed) downloadStatus.Text = text; }), operation.Token);
                if (!IsDisposed) MessageBox.Show(this, report.ToText(), "各环节耗时（包含一次 API 用量）");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!IsDisposed) downloadStatus.Text = ex.Message; }
            finally { if (!IsDisposed) { Busy(false); pipelineBenchmark.Enabled = true; } operation?.Dispose(); operation = null; }
        }; local.Controls.Add(pipelineBenchmark, 1, 13);

        var style = Page("外观与字幕", "切换黑白主题，调整多行字幕与历史记录的显示。", pages);
        themeChoice = Theme.Combo(); themeChoice.Items.AddRange(new object[] { "黑色", "白色" }); themeChoice.SelectedIndex = Theme.Normalize(Result.UiTheme) == "light" ? 1 : 0; Row(style, 1, "界面主题", themeChoice);
        themeChoice.SelectedIndexChanged += (_, _) => { Theme.Use(themeChoice.SelectedIndex == 1 ? "light" : "dark"); Theme.RefreshOpenForms(); };
        showJapanese = new CheckBox { Text = "同时显示日语原文", AutoSize = true, Checked = Result.ShowJapanese }; style.Controls.Add(showJapanese, 1, 2);
        fontSize = Number(Result.FontSize, 14, 48); Row(style, 3, "中文字号", fontSize);
        opacity = Number(Result.OverlayOpacity, 40, 100); Row(style, 4, "不透明度 / %", opacity);
        style.Controls.Add(Theme.Label("拖动字幕顶部空白区移动，拖动外边缘调整大小。\n拖动右侧滚动条查看历史；滚到底部恢复自动跟随。\n字幕内容支持选择和复制。右键或 Esc 可隐藏字幕。\n打开“鼠标穿透”后可操作字幕后面的直播。", true), 1, 5);
        var sample = Theme.Card(18); sample.Dock = DockStyle.Top; sample.Padding = new Padding(16, 12, 16, 12); sample.Margin = new Padding(0, 18, 0, 0);
        var sampleChinese = new Label { Text = "天 地 星 空   山 川 峰 谷", Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 22, FontStyle.Bold), ForeColor = Theme.Text, TextAlign = ContentAlignment.MiddleCenter };
        var sampleJapanese = new Label { Text = "あめ つち ほし そら   やま かは みね たに", Dock = DockStyle.Top, Height = 36, ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleCenter };
        sample.Controls.Add(sampleChinese); sample.Controls.Add(sampleJapanese); style.Controls.Add(sample, 0, 6); style.SetColumnSpan(sample, 2);
        void UpdateSubtitleSample()
        {
            sampleJapanese.Visible = showJapanese.Checked;
            int points = Math.Clamp((int)fontSize.Value, 14, 32);
            if (Math.Abs(sampleChinese.Font.Size - points) > .01f)
            {
                var previous = sampleChinese.Font; sampleChinese.Font = new Font("Microsoft YaHei UI", points, FontStyle.Bold); previous.Dispose();
            }
            sampleJapanese.Height = (int)Math.Ceiling(sampleJapanese.Font.GetHeight()) + 2;
            sample.Height = sample.Padding.Vertical + (showJapanese.Checked ? sampleJapanese.Height : 0) + (int)Math.Ceiling(sampleChinese.Font.GetHeight()) + 4;
        }
        showJapanese.CheckedChanged += (_, _) => UpdateSubtitleSample(); fontSize.ValueChanged += (_, _) => UpdateSubtitleSample();
        UpdateSubtitleSample();

        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 78, Padding = new Padding(28, 18, 24, 12), FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = Theme.Surface };
        footer.Paint += (_, e) => { using var line = new Pen(Theme.Border); e.Graphics.DrawLine(line, 0, 0, footer.Width, 0); };
        save = Theme.Button("保存设置", true); save.Click += (_, _) => Save();
        var cancel = Theme.Button("取消"); cancel.Click += (_, _) => Close(); footer.Controls.Add(save); footer.Controls.Add(cancel);
        pages.SetFooter(footer); Controls.Add(pages);
        FormClosing += (_, _) => { closing = true; InvalidateSpeechModelRequest(false); operation?.Cancel(); modelGeneration++; modelFetch?.Cancel(); chatgpt.Cancel(); SettingsStore.SetModelDirectory(Result.ModelFolder); Theme.Use(DialogResult == DialogResult.OK ? Result.UiTheme : original.UiTheme); Theme.RefreshOpenForms(); };
        Disposed += (_, _) => { closing = true; speechModelGeneration++; speechModelFetch?.Cancel(); };
        Shown += (_, _) => Theme.Refresh(this);
        DesktopShell.Attach(this, "设置");
    }
    private static TableLayoutPanel FieldTable(int rows)
    {
        var table = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2, RowCount = rows, Margin = Padding.Empty, BackColor = Theme.Surface };
        table.ColumnStyles.Add(new(SizeType.Absolute, 124)); table.ColumnStyles.Add(new(SizeType.Percent, 100));
        for (int i = 0; i < rows; i++) table.RowStyles.Add(new(SizeType.AutoSize));
        ConstrainFieldLabels(table);
        return table;
    }
    private static TableLayoutPanel Page(string title, string description, SettingsDeck pages)
    {
        var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(32, 28, 32, 24), ColumnCount = 2, RowCount = 15, BackColor = Theme.Surface };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 124)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 15; i++) table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var intro = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 0, 0, 22) };
        var heading = Theme.Label(title); heading.Font = new Font("Microsoft YaHei UI", 19, FontStyle.Bold); heading.Margin = new Padding(0, 0, 0, 10);
        var subtitle = Theme.Label(description, true); subtitle.Margin = Padding.Empty;
        intro.Controls.Add(heading); intro.Controls.Add(subtitle); table.Controls.Add(intro, 0, 0); table.SetColumnSpan(intro, 2);
        table.SizeChanged += (_, _) => subtitle.MaximumSize = new Size(Math.Max(180, table.ClientSize.Width - table.Padding.Horizontal - 20), 0);
        ConstrainFieldLabels(table);
        pages.AddPage(title, table); return table;
    }
    private static void ConstrainFieldLabels(TableLayoutPanel table)
    {
        void ResizeLabels()
        {
            int available = Math.Max(180, table.ClientSize.Width - table.Padding.Horizontal - 124 - 22);
            foreach (var label in table.Controls.OfType<Label>())
                label.MaximumSize = new Size(table.GetColumn(label) == 0 && table.GetColumnSpan(label) == 1 ? 114 : available, 0);
        }
        table.SizeChanged += (_, _) => ResizeLabels();
        table.ControlAdded += (_, _) => ResizeLabels();
    }
    private static NumericUpDown Number(int value, int min, int max) => new() { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), Width = 100, BackColor = Theme.Surface, ForeColor = Theme.Text };
    private static void Row(TableLayoutPanel table, int row, string text, Control value) { table.Controls.Add(Theme.Label(text), 0, row); value.Margin = new Padding(0, 8, 0, 8); table.Controls.Add(value, 1, row); }
    private void PopulateModels(IReadOnlyList<ApiModelInfo> choices, string selected)
    {
        apiModel.BeginUpdate();
        apiModel.Items.Clear();
        foreach (var model in choices) apiModel.Items.Add(model.Id);
        if (!string.IsNullOrWhiteSpace(selected) && !apiModel.Items.Contains(selected)) apiModel.Items.Insert(0, selected);
        apiModel.Text = selected;
        apiModel.EndUpdate();
        if (activeProvider == "deepseek") modelStatus.Text = "Flash：快速翻译 · V4 Pro：质量优先。也可填写其他模型 ID。";
    }
    private async Task FetchModelsAsync()
    {
        if (operation != null || activeProvider == "chatgpt") return;
        modelFetch?.Cancel();
        var request = new CancellationTokenSource(); modelFetch = request;
        long generation = ++modelGeneration;
        fetchModels.Enabled = false; modelStatus.Text = "正在获取此平台的可用模型…";
        try
        {
            var snapshot = new AppSettings { ApiProvider = activeProvider, ApiBase = address.Text.Trim(), ApiModel = apiModel.Text.Trim(), ApiProtocol = ((ApiProtocolInfo)protocol.SelectedItem!).Id };
            snapshot.SetKey(key.Text);
            var choices = await ApiModels.FetchAsync(snapshot, request.Token);
            if (IsDisposed || generation != modelGeneration) return;
            PopulateModels(choices, apiModel.Text);
            modelStatus.Text = $"已获取 {choices.Count} 个模型；请选择支持文字翻译的模型。";
        }
        catch (OperationCanceledException) { if (!IsDisposed && generation == modelGeneration) modelStatus.Text = "获取超时，可继续手动填写模型 ID。"; }
        catch (Exception ex) { if (!IsDisposed && generation == modelGeneration) modelStatus.Text = ex.Message + " 可继续手动填写。"; }
        finally
        {
            if (ReferenceEquals(modelFetch, request)) { modelFetch = null; if (!IsDisposed) fetchModels.Enabled = operation == null; }
            request.Dispose();
        }
    }
    private const string SpeechModelInstructions = "只获取已确认支持日语的语音识别模型；未知型号仍可手动填写。";
    private void RefreshSpeechModelFetchButton()
    {
        if (!IsDisposed && !closing)
            fetchSpeechModels.Enabled = !busy && recognitionMode.SelectedIndex == 1 && (speechModelFetch == null || speechModelFetch.IsCancellationRequested);
    }
    private void InvalidateSpeechModelRequest(bool resetStatus = true)
    {
        speechModelGeneration++; speechModelFetch?.Cancel();
        if (!IsDisposed && !closing)
        {
            if (resetStatus) speechModelStatus.Text = SpeechModelInstructions;
            RefreshSpeechModelFetchButton();
        }
    }
    private void PopulateSpeechModels(IReadOnlyList<ApiModelInfo> choices, string selected)
    {
        speechModel.BeginUpdate();
        try
        {
            speechModel.Items.Clear();
            foreach (var model in choices) if (!speechModel.Items.Contains(model.Id)) speechModel.Items.Add(model.Id);
            speechModel.Text = selected;
        }
        finally { speechModel.EndUpdate(); }
    }
    private async Task FetchSpeechModelsAsync()
    {
        if (busy || operation != null || recognitionMode.SelectedIndex != 1 || IsDisposed || closing) return;
        speechModelFetch?.Cancel();
        var request = new CancellationTokenSource(); speechModelFetch = request;
        long generation = ++speechModelGeneration;
        fetchSpeechModels.Enabled = false; speechModelStatus.Text = "正在获取已确认支持日语的语音模型…仅查询列表，不上传音频。";
        try
        {
            var snapshot = new AppSettings { RecognitionMode = "api", SpeechApiProvider = speechProvider.SelectedIndex == 1 ? "custom" : "openai", SpeechApiBase = speechAddress.Text.Trim(), SpeechApiModel = speechModel.Text.Trim() };
            snapshot.SetSpeechKey(speechKey.Text);
            var choices = await SpeechApiModels.FetchAsync(snapshot, request.Token, previewSpeechModelsHandler);
            if (IsDisposed || closing || busy || request.IsCancellationRequested || generation != speechModelGeneration || recognitionMode.SelectedIndex != 1) return;
            if (choices.Count == 0) throw new InvalidDataException();
            string selected = speechModel.Text;
            bool preserved = !string.IsNullOrWhiteSpace(selected) && !choices.Any(model => model.Id == selected);
            PopulateSpeechModels(choices, selected);
            speechModelStatus.Text = $"已获取 {choices.Count} 个已确认支持日语的识别模型。" + (preserved ? " 当前手填型号已保留，其日语能力需自行确认。" : "请选择模型，也可手动填写其他型号。");
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed && !closing && generation == speechModelGeneration)
                speechModelStatus.Text = "获取超时，请稍后重试；原模型和列表已保留，可继续手动填写。";
        }
        catch (Exception ex)
        {
            if (!IsDisposed && !closing && generation == speechModelGeneration)
                speechModelStatus.Text = SpeechModelFetchError(ex) + " 原模型和列表已保留，可继续手动填写。";
        }
        finally
        {
            if (ReferenceEquals(speechModelFetch, request)) { speechModelFetch = null; RefreshSpeechModelFetchButton(); }
            request.Dispose();
        }
    }
    private static string SpeechModelFetchError(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized } => "识别 API 密钥无效，请检查密钥。",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden } => "识别 API 拒绝访问列表，请检查账号权限。",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.MethodNotAllowed } => "此识别 API 不提供模型列表。",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests } => "获取过于频繁，请稍后重试。",
        TimeoutException => "获取超时，请稍后重试。",
        InvalidDataException => "列表中没有可确认的日语语音识别模型，或列表格式不受支持。",
        ArgumentException or InvalidOperationException => "请检查识别 API 地址和密钥后重试。",
        _ => "获取失败，请检查网络和识别 API 设置。"
    };
    private AppSettings ReadFields()
    {
        if (activeProvider == "chatgpt") chatgpt.ValidateSelection();
        else { ApiTranslator.Endpoint(address.Text, ((ApiProtocolInfo)protocol.SelectedItem!).Id); if (string.IsNullOrWhiteSpace(apiModel.Text)) throw new ArgumentException("请填写模型名称。"); }
        var value = ReadRecognitionFields(); value.ApiBase = address.Text.Trim(); value.ApiModel = apiModel.Text.Trim(); value.SetKey(key.Text);
        value.ApiProvider = activeProvider; value.ApiProtocol = ((ApiProtocolInfo)protocol.SelectedItem!).Id;
        value.ChatGptAccountId = chatgpt.SelectedAccountId; value.ChatGptModel = chatgpt.SelectedModelId;
        if (activeProvider == "chatgpt") { value.ApiBase = ChatGptAuth.Resource; value.ApiProtocol = "responses"; value.ApiModel = value.ChatGptModel; value.EncryptedKey = ""; }
        value.ApiProfiles = new(apiDrafts); value.ApiProfiles[activeProvider] = CaptureApiProfile();
        value.TranslationContextSegments = (int)translationContext.Value;
        value.ShowJapanese = showJapanese.Checked; value.FontSize = (int)fontSize.Value; value.OverlayOpacity = (int)opacity.Value; value.Glossary = glossary.Text.Trim(); return value;
    }
    private void ImportGlossary()
    {
        if (previewGlossaryImportPath is { } previewPath) { glossary.Text = GlossaryFiles.MergeImported(glossary.Text, GlossaryFiles.ReadText(previewPath)); return; }
        using var picker = new OpenFileDialog { Title = "导入术语表 TXT（合并到现有内容）", Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*", DefaultExt = "txt", CheckFileExists = true, Multiselect = false };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        try { glossary.Text = GlossaryFiles.MergeImported(glossary.Text, GlossaryFiles.ReadText(picker.FileName)); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "术语表导入失败", MessageBoxButtons.OK, MessageBoxIcon.Information); }
    }
    private void ExportGlossary()
    {
        if (previewGlossaryExportPath is { } previewPath) { GlossaryFiles.WriteText(previewPath, glossary.Text); return; }
        using var picker = new SaveFileDialog { Title = "导出术语表（UTF-8 TXT）", Filter = "文本文件 (*.txt)|*.txt", DefaultExt = "txt", AddExtension = true, FileName = "Kotoshiro-术语表.txt", OverwritePrompt = true };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        try { GlossaryFiles.WriteText(picker.FileName, glossary.Text); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "术语表导出失败", MessageBoxButtons.OK, MessageBoxIcon.Information); }
    }
    private ApiProfile CaptureApiProfile()
    {
        var secret = new AppSettings(); secret.SetKey(key.Text);
        return new(address.Text.Trim(), apiModel.Text.Trim(), secret.EncryptedKey, ((ApiProtocolInfo)protocol.SelectedItem!).Id);
    }
    internal void VerifyProviderSwitchForPreview()
    {
        key.Text = "preview-deepseek-secret";
        provider.SelectedItem = ApiProviders.Get("openai");
        if (key.Text.Length != 0 || address.Text != "https://api.openai.com/v1" || ((ApiProtocolInfo)protocol.SelectedItem!).Id != "responses") throw new InvalidOperationException("预览检查：新平台错误地使用了旧密钥。");
        key.Text = "preview-openai-secret"; apiModel.Text = "preview-model";
        provider.SelectedItem = ApiProviders.Get("deepseek");
        if (key.Text != "preview-deepseek-secret") throw new InvalidOperationException("预览检查：原平台的密钥未恢复。");
        provider.SelectedItem = ApiProviders.Get("openai");
        if (key.Text != "preview-openai-secret" || apiModel.Text != "preview-model") throw new InvalidOperationException("预览检查：平台设置未分别保留。");
        provider.SelectedItem = ApiProviders.Get("gemini");
        if (key.Text.Length != 0) throw new InvalidOperationException("预览检查：Gemini 继承了其他平台的密钥。");
        provider.SelectedItem = ApiProviders.Get(Result.ApiProvider);
        address.Text = Result.ApiBase; apiModel.Text = Result.ApiModel; key.Text = Result.GetKey();
        apiDrafts.Clear(); apiDrafts[activeProvider] = CaptureApiProfile();
        testStatus.Text = "测试会翻译一句“こんにちは”，产生少量 API 用量。";
    }
    internal void ShowChatGptForPreview() => provider.SelectedItem = ApiProviders.Get("chatgpt");
    internal void SelectChatGptForPreview(string id, string model) => chatgpt.SelectForPreview(id, model);
    internal void ShowRecognitionForPreview() => pages.ShowPage(1);
    internal void ShowApiRecognitionForPreview() { pages.ShowPage(1); recognitionMode.SelectedIndex = 1; }
    internal void ShowCustomSpeechForPreview() { ShowApiRecognitionForPreview(); speechProvider.SelectedIndex = 1; speechAddress.Text = "https://api.siliconflow.cn/v1"; speechModel.Text = "FunAudioLLM/SenseVoiceSmall"; speechKey.Text = ""; UpdateSpeechModels(); }
    internal void VerifyRecognitionForPreview()
    {
        string oldTranslationKey = key.Text;
        string beforeAddress = speechAddress.Text, beforeModel = speechModel.Text, beforeKey = speechKey.Text;
        int beforeMode = recognitionMode.SelectedIndex;
        recognitionMode.SelectedIndex = 1; speechAddress.Text = "https://speech.example.test/v1"; speechModel.Text = "custom-speech-model"; speechKey.Text = "fake-speech-key";
        var remote = ReadRecognitionFields(); RecognitionEngines.Validate(remote);
        if (remote.RecognitionMode != "api" || remote.SpeechApiModel != "custom-speech-model" || remote.GetSpeechKey() != "fake-speech-key" || key.Text != oldTranslationKey) throw new InvalidOperationException("API 识别设置或翻译密钥隔离检查失败。");
        recognitionMode.SelectedIndex = 0; recognitionMode.SelectedIndex = 1;
        if (speechModel.Text != "custom-speech-model" || speechKey.Text != "fake-speech-key") throw new InvalidOperationException("切换识别方式后 API 设置未保留。");
        speechAddress.Text = beforeAddress; speechModel.Text = beforeModel; speechKey.Text = beforeKey; recognitionMode.SelectedIndex = beforeMode;
    }
    private bool SpeechAddressIsOpenAi() => Uri.TryCreate(speechAddress.Text, UriKind.Absolute, out var endpoint) && endpoint.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase);
    private void UpdateSpeechModels()
    {
        string current = speechModel.Text;
        PopulateSpeechModels(speechProvider.SelectedIndex == 0 ? SpeechApiModels.Defaults() : [], current);
        speechHint.Text = "获取模型会筛选已确认支持日语且兼容 audio/transcriptions 的型号；列表可能不涵盖全部可用模型。\n音频上传给识别服务并单独计费。DeepSeek 文字模型请在“翻译服务”中使用。";
    }
    internal void VerifySpeechModelsForPreview()
    {
        int oldProvider = speechProvider.SelectedIndex;
        string oldModel = speechModel.Text, oldAddress = speechAddress.Text, oldKey = speechKey.Text, oldStatus = speechStatus.Text;
        try
        {
            speechProvider.SelectedIndex = 1; speechModel.Text = "fixture-japanese-asr"; UpdateSpeechModels();
            if (speechModel.DropDownStyle != ComboBoxStyle.DropDown || SpeechApiRecognition.Models.Any(x => speechModel.Items.Contains(x)) || speechModel.Text != "fixture-japanese-asr") throw new InvalidOperationException("自定义识别模型仍含有 OpenAI 预设，或不支持直接输入。");
            speechProvider.SelectedIndex = 0;
            if (SpeechApiModels.Defaults().Any(x => !speechModel.Items.Contains(x.Id)) || speechModel.Text != "fixture-japanese-asr") throw new InvalidOperationException("OpenAI 识别预设或已填写模型未保留。");
            speechProvider.SelectedIndex = 1;
            if (speechModel.Items.Count != 0 || speechModel.Text != "fixture-japanese-asr") throw new InvalidOperationException("切换平台后自定义识别模型未保留，或未知型号进入了已确认列表。");
        }
        finally { speechProvider.SelectedIndex = oldProvider; speechModel.Text = oldModel; speechAddress.Text = oldAddress; speechKey.Text = oldKey; speechStatus.Text = oldStatus; UpdateSpeechModels(); }
    }
    internal async Task<string> VerifySpeechModelFetchForPreviewAsync()
    {
        if (!Visible || busy || speechModelFetch != null) throw new InvalidOperationException("获取语音模型的预览检查需要空闲、已显示的设置窗口。");
        int oldPage = pages.SelectedPageIndex, oldMode = recognitionMode.SelectedIndex, oldProvider = speechProvider.SelectedIndex;
        string oldAddress = speechAddress.Text, oldKey = speechKey.Text, oldModel = speechModel.Text, oldStatus = speechModelStatus.Text, oldHint = speechHint.Text;
        string oldTranslationAddress = address.Text, oldTranslationKey = key.Text, oldTranslationModel = apiModel.Text, oldTranslationStatus = modelStatus.Text;
        object[] oldItems = speechModel.Items.Cast<object>().ToArray();
        var oldHandler = previewSpeechModelsHandler;
        const string speechSecret = "preview-speech-secret", translationSecret = "preview-translation-secret";
        string[] confirmed = ["Qwen/Qwen3-ASR-1.7B", "FunAudioLLM/SenseVoiceSmall"];
        static HttpResponseMessage ModelResponse(IEnumerable<string> ids) => new(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { data = ids.Select(id => new { id, @object = "model" }) }), System.Text.Encoding.UTF8, "application/json")
        };
        void Prepare()
        {
            Busy(false); pages.ShowPage(1); recognitionMode.SelectedIndex = 1; speechProvider.SelectedIndex = 1;
            speechAddress.Text = "https://api.siliconflow.cn/v1"; speechKey.Text = speechSecret; speechModel.Text = "preview-manual-japanese-model";
            UpdateSpeechModels(); PerformLayout(); Application.DoEvents();
        }
        Task Click()
        {
            long previousGeneration = speechModelGeneration; fetchSpeechModels.PerformClick();
            if (speechModelFetchTask == null || speechModelGeneration == previousGeneration) throw new InvalidOperationException("获取语音模型按钮未触发真实点击事件。");
            return speechModelFetchTask;
        }
        void CheckConfirmedList()
        {
            if (speechModel.Items.Count != confirmed.Length || confirmed.Any(id => !speechModel.Items.Contains(id)) || speechModel.Items.Contains("preview-manual-japanese-model"))
                throw new InvalidOperationException("语音模型列表混入文字、合成或未知模型，或漏掉已确认日语型号。");
        }
        try
        {
            Prepare(); address.Text = "https://translation.example.test/v1"; key.Text = translationSecret; apiModel.Text = "preview-translation-model";
            string translationStatus = modelStatus.Text;
            bool sawSpeechCredentials = false;
            previewSpeechModelsHandler = new SpeechModelPreviewHandler((request, _) =>
            {
                sawSpeechCredentials = request.Method == HttpMethod.Get && request.RequestUri!.Host == "api.siliconflow.cn" && request.RequestUri.AbsolutePath == "/v1/models" && request.RequestUri.Query.Contains("sub_type=speech-to-text") && request.Headers.Authorization?.Parameter == speechSecret;
                return Task.FromResult(ModelResponse(confirmed.Concat(["deepseek-chat", "FunAudioLLM/CosyVoice2-0.5B", "preview-manual-japanese-model"])));
            });
            await Click(); CheckConfirmedList();
            if (!sawSpeechCredentials || speechModel.Text != "preview-manual-japanese-model" || !speechModelStatus.Text.Contains("已获取 2 个") || !speechModelStatus.Text.Contains("自行确认") || !fetchSpeechModels.Enabled)
                throw new InvalidOperationException("模型获取未使用独立识别凭据、未保留手填值，或已确认模型统计错误。");
            if (address.Text != "https://translation.example.test/v1" || key.Text != translationSecret || apiModel.Text != "preview-translation-model" || modelStatus.Text != translationStatus)
                throw new InvalidOperationException("获取识别模型改动了翻译服务设置或提示。");

            previewSpeechModelsHandler = new SpeechModelPreviewHandler((_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized) { Content = new StringContent("private-service-body " + speechSecret) }));
            await Click(); CheckConfirmedList();
            if (speechModel.Text != "preview-manual-japanese-model" || !speechModelStatus.Text.Contains("密钥无效") || speechModelStatus.Text.Contains(speechSecret) || speechModelStatus.Text.Contains("private-service-body"))
                throw new InvalidOperationException("获取失败未保留模型，或错误提示暴露了服务正文/密钥。");
            previewSpeechModelsHandler = new SpeechModelPreviewHandler((_, _) => Task.FromResult(ModelResponse(["deepseek-chat", "preview-unknown-asr"])));
            await Click(); CheckConfirmedList();
            if (!speechModelStatus.Text.Contains("没有可确认") || speechModel.Text != "preview-manual-japanese-model") throw new InvalidOperationException("没有已确认型号时未保留原列表或未说明限制。");

            foreach (string change in new[] { "address", "key", "provider", "mode", "busy" })
            {
                Prepare();
                var completion = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                bool canceled = false;
                previewSpeechModelsHandler = new SpeechModelPreviewHandler(async (_, token) =>
                {
                    using var registration = token.Register(() => canceled = true);
                    return await completion.Task; // Deliberately return a late response after cancellation.
                });
                Task pending = Click();
                if (fetchSpeechModels.Enabled || !speechModelStatus.Text.Contains("正在获取")) throw new InvalidOperationException("获取进行中没有禁用按钮或显示状态。");
                switch (change)
                {
                    case "address": speechAddress.Text = "https://api.siliconflow.cn/changed/v1"; break;
                    case "key": speechKey.Text = "preview-changed-speech-secret"; break;
                    case "provider": speechProvider.SelectedIndex = 0; break;
                    case "mode": recognitionMode.SelectedIndex = 0; break;
                    case "busy": Busy(true); break;
                }
                string expectedModel = speechModel.Text, expectedStatus = speechModelStatus.Text;
                object[] expectedItems = speechModel.Items.Cast<object>().ToArray();
                completion.SetResult(ModelResponse(confirmed)); await pending;
                if (!canceled || speechModel.Text != expectedModel || speechModelStatus.Text != expectedStatus || !expectedItems.SequenceEqual(speechModel.Items.Cast<object>()))
                    throw new InvalidOperationException("更改 " + change + " 后请求未取消，或迟到响应覆盖了当前设置。");
                if (change == "busy" && fetchSpeechModels.Enabled) throw new InvalidOperationException("忙碌操作期间获取模型按钮被旧请求重新启用。");
                Busy(false);
            }

            Prepare();
            var manualCompletion = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            previewSpeechModelsHandler = new SpeechModelPreviewHandler((_, _) => manualCompletion.Task);
            Task manualPending = Click(); speechModel.Text = "preview-new-manual-model";
            key.Text = "preview-new-translation-secret"; address.Text = "https://translation.example.test/changed/v1";
            manualCompletion.SetResult(ModelResponse(confirmed)); await manualPending; CheckConfirmedList();
            if (speechModel.Text != "preview-new-manual-model" || !speechModelStatus.Text.Contains("已获取 2 个")) throw new InvalidOperationException("获取期间的新手填型号未保留，或翻译侧编辑取消了识别请求。");

            Prepare();
            var oldCompletion = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            previewSpeechModelsHandler = new SpeechModelPreviewHandler((_, _) => oldCompletion.Task);
            Task oldPending = Click(); speechKey.Text = "preview-new-speech-secret";
            previewSpeechModelsHandler = new SpeechModelPreviewHandler((_, _) => Task.FromResult(ModelResponse([confirmed[0]])));
            await Click();
            string newerStatus = speechModelStatus.Text;
            oldCompletion.SetResult(ModelResponse(confirmed)); await oldPending;
            if (speechModel.Items.Count != 1 || !speechModel.Items.Contains(confirmed[0]) || speechModelStatus.Text != newerStatus || !fetchSpeechModels.Enabled)
                throw new InvalidOperationException("旧请求覆盖了新请求的模型列表、状态或按钮状态。");

            using (var closingForm = new SettingsForm(Result.Clone()))
            {
                closingForm.Show(this); closingForm.pages.ShowPage(1); closingForm.recognitionMode.SelectedIndex = 1; closingForm.speechProvider.SelectedIndex = 1;
                closingForm.speechAddress.Text = "https://api.siliconflow.cn/v1"; closingForm.speechKey.Text = speechSecret;
                closingForm.PerformLayout(); Application.DoEvents();
                var closedCompletion = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                bool canceled = false;
                closingForm.previewSpeechModelsHandler = new SpeechModelPreviewHandler(async (_, token) =>
                {
                    using var registration = token.Register(() => canceled = true);
                    return await closedCompletion.Task;
                });
                closingForm.fetchSpeechModels.PerformClick(); Task closedPending = closingForm.speechModelFetchTask ?? throw new InvalidOperationException("关闭窗口预览检查未触发模型按钮。");
                closingForm.Close(); closedCompletion.SetResult(ModelResponse(confirmed)); await closedPending;
                if (!canceled || !closingForm.closing || !closingForm.IsDisposed) throw new InvalidOperationException("设置窗口关闭时获取语音模型请求未取消。");
            }
            return "PASS: real speech-model button clicks use independent speech address/key; only confirmed Japanese ASR IDs enter the list, unknown manual text is preserved outside the list and excluded from the count; HTTP/no-confirmed-model failures preserve selection/list and hide response bodies; address/key/provider/mode/Busy/close cancel requests, late/older responses cannot overwrite newer results, and translation edits stay isolated. All HTTP requests were simulated; no audio or real key was sent.";
        }
        finally
        {
            InvalidateSpeechModelRequest(false); previewSpeechModelsHandler = oldHandler; Busy(false);
            recognitionMode.SelectedIndex = oldMode; speechProvider.SelectedIndex = oldProvider; speechAddress.Text = oldAddress; speechKey.Text = oldKey;
            speechModel.Items.Clear(); speechModel.Items.AddRange(oldItems); speechModel.Text = oldModel; speechModelStatus.Text = oldStatus; speechHint.Text = oldHint;
            address.Text = oldTranslationAddress; key.Text = oldTranslationKey; apiModel.Text = oldTranslationModel; modelStatus.Text = oldTranslationStatus;
            pages.ShowPage(oldPage); PerformLayout(); Application.DoEvents();
        }
    }
    private sealed class SpeechModelPreviewHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => callback(request, token);
    }
    internal void ShowSubtitleStyleForPreview(bool? japanese = null)
    {
        pages.ShowPage(2);
        if (japanese.HasValue) showJapanese.Checked = japanese.Value;
    }
    internal void ShowContextForPreview(int count)
    {
        pages.ShowPage(0); translationContext.Value = Math.Clamp(count, 0, 30); PerformLayout(); Application.DoEvents();
        var viewport = (Panel)pages.PageAt(0);
        viewport.AutoScrollPosition = new Point(0, Math.Max(0, viewport.DisplayRectangle.Height));
        Application.DoEvents();
    }
    internal void SetContextForPreview(int count) => translationContext.Value = Math.Clamp(count, 0, 30);
    internal void SaveForPreview() => save.PerformClick();
    internal void CancelForPreview() => Close();
    internal bool HasExamplesButtonForPreview => DescendantButtons(this).Any(button => button.Text == "添加示例");
    private static IEnumerable<Button> DescendantButtons(Control root)
    {
        foreach (Control child in root.Controls)
        {
            if (child is Button button) yield return button;
            foreach (var nested in DescendantButtons(child)) yield return nested;
        }
    }
    internal string VerifyContextForPreview()
    {
        int originalValue = (int)translationContext.Value, originalPage = pages.SelectedPageIndex;
        var originalScroll = ((Panel)pages.PageAt(0)).AutoScrollPosition;
        string originalProvider = activeProvider;
        var originalProfile = CaptureApiProfile();
        var originalDrafts = new Dictionary<string, ApiProfile>(apiDrafts);
        try
        {
            if (translationContext.Minimum != 0 || translationContext.Maximum != 30) throw new InvalidOperationException("上下文段数范围不正确。");
            pages.ShowPage(0); provider.SelectedItem = ApiProviders.Get("deepseek");
            foreach (int count in new[] { 0, 3, 6, 30 })
            {
                translationContext.Value = count;
                if (ReadFields().TranslationContextSegments != count) throw new InvalidOperationException("所填上下文段数未用于翻译设置。");
                foreach (string platform in new[] { "openai", "chatgpt", "deepseek" })
                {
                    provider.SelectedItem = ApiProviders.Get(platform); PerformLayout(); Application.DoEvents();
                    if ((int)translationContext.Value != count || !translationContext.Visible)
                        throw new InvalidOperationException("切换翻译平台后上下文段数未保留，或公共选项被隐藏。");
                }
            }
            return "PASS: translation context accepts 0/3/6/30, enters saved settings, and stays visible/preserved across API and subscription providers; no API request was sent.";
        }
        finally
        {
            provider.SelectedItem = ApiProviders.Get(originalProvider);
            address.Text = originalProfile.Address; apiModel.Text = originalProfile.Model;
            key.Text = new AppSettings { EncryptedKey = originalProfile.EncryptedKey }.GetKey();
            protocol.SelectedItem = ApiProviders.Protocols.First(x => x.Id == originalProfile.Protocol);
            apiDrafts.Clear(); foreach (var pair in originalDrafts) apiDrafts.Add(pair.Key, pair.Value);
            translationContext.Value = originalValue; pages.ShowPage(originalPage); PerformLayout(); Application.DoEvents();
            ((Panel)pages.PageAt(0)).AutoScrollPosition = new Point(-originalScroll.X, -originalScroll.Y);
        }
    }
    internal string VerifyGlossaryForPreview()
    {
        string originalText = glossary.Text;
        int originalPage = pages.SelectedPageIndex;
        var originalScroll = ((Panel)pages.PageAt(0)).AutoScrollPosition;
        string folder = Path.Combine(Path.GetTempPath(), "Kotoshiro-glossary-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string imported = Path.Combine(folder, "import.txt"), exported = Path.Combine(folder, "export.txt");
        try
        {
            if (HasExamplesButtonForPreview) throw new InvalidOperationException("已移除的添加示例按钮仍出现在设置窗口。");
            string[] defaults = GlossaryFiles.DefaultExample.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
            if (defaults.Length != 5 || defaults[0] != "ひのめ → 火羽") throw new InvalidOperationException("默认术语表应为五行，首行应为ひのめ → 火羽。");
            if (Result.Glossary == GlossaryFiles.DefaultExample && originalText != GlossaryFiles.DefaultExample)
                throw new InvalidOperationException("新设置窗口未显示完整默认术语表。");
            pages.ShowPage(0); glossary.Text = GlossaryFiles.DefaultExample + "\r\nコメント → 弹幕";
            GlossaryFiles.WriteText(imported, "ひのめ → 火羽\n配信 → 直播\n初見 → 第一次来直播间");
            previewGlossaryImportPath = imported; importGlossary.PerformClick();
            if (!glossary.Text.Contains("コメント → 弹幕") || !glossary.Text.Contains("初見 → 第一次来直播间") || defaults.Any(line => glossary.Text.Split('\n').Count(x => x.Trim() == line) != 1))
                throw new InvalidOperationException("导入术语没有保留原有内容或未去重。");
            string firstImport = glossary.Text; importGlossary.PerformClick();
            if (glossary.Text != firstImport) throw new InvalidOperationException("重复导入术语 TXT 后出现重复内容。");
            previewGlossaryExportPath = exported; exportGlossary.PerformClick();
            if (GlossaryFiles.ReadText(exported).Replace("\r\n", "\n") != glossary.Text.Replace("\r\n", "\n"))
                throw new InvalidOperationException("导出术语表与当前编辑内容不一致。");
            return "PASS: five default glossary lines are retained with ひのめ -> 火羽 first; TXT import merges without duplicates and export writes current unsaved text; only Import/Export buttons remain and no key or history is exported.";
        }
        finally
        {
            previewGlossaryImportPath = previewGlossaryExportPath = null; glossary.Text = originalText; pages.ShowPage(originalPage);
            PerformLayout(); Application.DoEvents(); ((Panel)pages.PageAt(0)).AutoScrollPosition = new Point(-originalScroll.X, -originalScroll.Y);
            foreach (string path in new[] { imported, exported }) if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(folder)) Directory.Delete(folder);
        }
    }
    internal string VerifyLayoutForPreview()
    {
        var originalSize = ClientSize;
        int originalPage = pages.SelectedPageIndex, originalMode = recognitionMode.SelectedIndex;
        try
        {
            if (HasExamplesButtonForPreview) throw new InvalidOperationException("设置布局检查：添加示例按钮仍然存在。");
            foreach (var size in new[] { originalSize, MinimumSize })
            {
                ClientSize = size; PerformLayout(); Application.DoEvents();
                foreach (int mode in new[] { 0, 1 })
                {
                    recognitionMode.SelectedIndex = mode;
                    for (int page = 0; page < 3; page++)
                    {
                        pages.ShowPage(page); PerformLayout(); Application.DoEvents();
                        var panel = (Panel)pages.PageAt(page);
                        if (panel.Width < 460 || panel.Height < 440 || panel.HorizontalScroll.Visible)
                            throw new InvalidOperationException($"设置布局检查：{size.Width}×{size.Height} 页 {page + 1} 内容过窄或发生横向溢出。");
                        if (page == 1 && mode == 1)
                        {
                            panel.AutoScrollPosition = Point.Empty; PerformLayout(); Application.DoEvents();
                            var modelBounds = new Rectangle(panel.PointToClient(speechModel.PointToScreen(Point.Empty)), speechModel.Size);
                            var fetchBounds = new Rectangle(panel.PointToClient(fetchSpeechModels.PointToScreen(Point.Empty)), fetchSpeechModels.Size);
                            if (!speechModel.Visible || !fetchSpeechModels.Visible || !panel.ClientRectangle.Contains(modelBounds) || !panel.ClientRectangle.Contains(fetchBounds) || modelBounds.IntersectsWith(fetchBounds) || speechModel.Width < 180)
                                throw new InvalidOperationException("设置布局检查：日语识别模型与获取按钮在默认/最小窗口中被遮挡、过窄或重叠。");
                        }
                        foreach (var button in save.Parent!.Controls.OfType<Button>())
                        {
                            var point = PointToClient(button.PointToScreen(Point.Empty));
                            if (!ClientRectangle.Contains(new Rectangle(point, button.Size)))
                                throw new InvalidOperationException("设置布局检查：保存或取消按钮超出窗口。");
                        }
                        int bottom = panel.Controls.Cast<Control>().Where(x => x.Visible).Max(x => x.Bottom);
                        if (bottom > panel.ClientSize.Height && !panel.VerticalScroll.Visible)
                            throw new InvalidOperationException("设置布局检查：长页面未提供滚动条。");
                        panel.AutoScrollPosition = new Point(0, Math.Max(0, panel.DisplayRectangle.Height));
                        Application.DoEvents();
                        if (page == 0)
                            foreach (var button in new[] { importGlossary, exportGlossary })
                            {
                                var point = panel.PointToClient(button.PointToScreen(Point.Empty));
                                if (!panel.ClientRectangle.Contains(new Rectangle(point, button.Size)))
                                    throw new InvalidOperationException("设置布局检查：滚到页面底部后术语表操作按钮仍不可达。");
                            }
                        panel.AutoScrollPosition = Point.Empty;
                    }
                }
            }
            return "PASS: settings pages remain readable at default/minimum sizes; Japanese speech-model input and Fetch button are visible without overlap; long pages scroll vertically, glossary actions are reachable by scrolling, and Save/Cancel stay accessible.";
        }
        finally
        {
            recognitionMode.SelectedIndex = originalMode; ClientSize = originalSize; pages.ShowPage(originalPage); PerformLayout(); Application.DoEvents();
        }
    }
    internal void VerifyModelsForPreview()
    {
        provider.SelectedItem = ApiProviders.Get("deepseek");
        if (apiModel.DropDownStyle != ComboBoxStyle.DropDown || !apiModel.Items.Contains("deepseek-flash") || !apiModel.Items.Contains("deepseek-v4-pro")) throw new InvalidOperationException("模型选择缺少 DeepSeek 预设或手动输入。");
        string before = apiModel.Text;
        apiModel.SelectedItem = "deepseek-v4-pro";
        if (CaptureApiProfile().Model != "deepseek-v4-pro") throw new InvalidOperationException("所选模型未用于 API 设置。");
        provider.SelectedItem = ApiProviders.Get("custom"); apiModel.Text = "custom-translation-model";
        provider.SelectedItem = ApiProviders.Get("deepseek");
        if (apiModel.Text != "deepseek-v4-pro") throw new InvalidOperationException("切换平台后所选模型未恢复。");
        provider.SelectedItem = ApiProviders.Get("custom");
        if (apiModel.Text != "custom-translation-model") throw new InvalidOperationException("自定义模型未保留。");
        provider.SelectedItem = ApiProviders.Get("deepseek"); apiModel.Text = before;
        provider.SelectedItem = ApiProviders.Get(Result.ApiProvider);
    }
    private AppSettings ReadRecognitionFields()
    {
        var value = Result.Clone();
        value.RecognitionMode = recognitionMode.SelectedIndex == 1 ? "api" : "local";
        value.SpeechApiProvider = speechProvider.SelectedIndex == 1 ? "custom" : "openai";
        value.SpeechApiBase = speechAddress.Text.Trim(); value.SpeechApiModel = speechModel.Text.Trim(); value.SetSpeechKey(speechKey.Text);
        value.RecognitionModel = ((ModelInfo)recognition.SelectedItem!).Id; value.UseGpu = gpu.Checked; value.Threads = (int)threads.Value; value.SegmentSeconds = (int)seconds.Value;
        value.ModelFolder = modelFolder.Text; value.UiTheme = themeChoice.SelectedIndex == 1 ? "light" : "dark";
        return value;
    }
    private void Save()
    {
        try { var updated = ReadFields(); if (updated.RecognitionMode == "api") RecognitionEngines.Validate(updated); Result = updated; SettingsStore.Save(Result); DialogResult = DialogResult.OK; Close(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "设置未保存", MessageBoxButtons.OK, MessageBoxIcon.Information); }
    }
    private void Busy(bool enabled) { busy = enabled; if (enabled) { modelGeneration++; modelFetch?.Cancel(); InvalidateSpeechModelRequest(); } save.Enabled = test.Enabled = download.Enabled = recognition.Enabled = recognitionMode.Enabled = speechProvider.Enabled = speechAddress.Enabled = speechModel.Enabled = speechKey.Enabled = speechTest.Enabled = themeChoice.Enabled = enableGpu.Enabled = gpu.Enabled = browseFolder.Enabled = migrateModels.Enabled = provider.Enabled = protocol.Enabled = address.Enabled = apiModel.Enabled = key.Enabled = translationContext.Enabled = importGlossary.Enabled = exportGlossary.Enabled = !enabled; fetchModels.Enabled = !enabled && modelFetch == null; RefreshSpeechModelFetchButton(); chatgpt.Enabled = !enabled || chatgpt.IsBusy; }
    private bool ChooseFolder()
    {
        using var chooser = new FolderBrowserDialog { Description = "选择模型与显卡组件的存放文件夹（建议 D 盘或其他数据盘）", UseDescriptionForTitle = true, SelectedPath = modelFolder.Text };
        if (chooser.ShowDialog(this) != DialogResult.OK) return false;
        modelFolder.Text = chooser.SelectedPath; SettingsStore.SetModelDirectory(modelFolder.Text); UpdateModelStatus();
        gpuStatus.Text = GpuSetup.IsInstalled() ? "所选文件夹中的显卡组件已就绪。" : "显卡组件会自动下载到所选文件夹。";
        return true;
    }
    private bool EnsureFolder() => !string.IsNullOrWhiteSpace(modelFolder.Text) || ChooseFolder();
    private async Task MigrateModelsAsync()
    {
        if (operation != null || !EnsureFolder()) return;
        operation = new(); Busy(true);
        try
        {
            int moved = await ModelMigration.MigrateAsync(migrationSource, modelFolder.Text, new Progress<string>(text => { if (!IsDisposed) downloadStatus.Text = text; }), operation.Token, () =>
            {
                var updated = Result.Clone(); updated.ModelFolder = modelFolder.Text; SettingsStore.Save(updated); Result = updated;
                migrationSource = updated.ModelFolder;
                return Task.CompletedTask;
            });
            if (!IsDisposed) downloadStatus.Text = moved > 0 ? $"已迁移 {moved} 个模型，存放位置已保存。" : "原位置没有需要迁移的模型，或当前文件夹与原位置相同。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!IsDisposed) downloadStatus.Text = "迁移未完成 · " + ex.Message; }
        finally { if (!IsDisposed) Busy(false); operation?.Dispose(); operation = null; }
    }
    private async Task EnableGpuAsync()
    {
        if (operation != null || !EnsureFolder()) return;
        operation = new(); Busy(true);
        try
        {
            gpuStatus.Text = "正在检测显卡…";
            string? name = await GpuSetup.DetectNvidiaAsync(operation.Token);
            if (name == null) { gpuStatus.Text = "未检测到可用的 NVIDIA 显卡，请使用 CPU。"; return; }
            await GpuSetup.InstallAsync(new Progress<string>(text => { if (!IsDisposed) gpuStatus.Text = text; }), operation.Token);
            var model = (ModelInfo)recognition.SelectedItem!;
            if (!Models.IsReady(model))
            {
                gpuStatus.Text = "正在准备识别模型…";
                await Models.DownloadAsync(model, new Progress<int>(p => { if (!IsDisposed) gpuStatus.Text = $"正在下载识别模型：{p}%"; }), operation.Token);
                if (!IsDisposed) UpdateModelStatus();
            }
            gpuStatus.Text = "正在测试显卡加速…";
            var current = ReadRecognitionFields(); current.UseGpu = true;
            using var engine = new WhisperEngine(); await engine.StartAsync(current, operation.Token);
            if (!engine.Mode.StartsWith("显卡加速")) throw new InvalidOperationException("显卡暂不可用，已保留 CPU。请检查或更新 NVIDIA 驱动后重试。");
            await engine.TranscribeAsync(AudioSegmenter.WaveBytes(new float[16000]), operation.Token);
            if (!IsDisposed) { gpu.Checked = true; gpuStatus.Text = $"{name} · 显卡测试通过。点击“保存设置”即可使用。"; }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!IsDisposed) gpuStatus.Text = "显卡启用未完成 · " + ex.Message; }
        finally { if (!IsDisposed) Busy(false); operation?.Dispose(); operation = null; }
    }
    private async Task TestAsync()
    {
        try
        {
            var current = ReadFields(); operation = new(); Busy(true); testStatus.Text = "正在连接…";
            using var translator = new ApiTranslator(current);
            string text = await translator.TranslateAsync("こんにちは。", operation.Token, false);
            if (!IsDisposed) testStatus.Text = "连接成功 · " + text;
        }
        catch (OperationCanceledException) { if (!IsDisposed) testStatus.Text = "请求已取消或超时。"; }
        catch (Exception ex) { if (!IsDisposed) testStatus.Text = "连接失败 · " + ex.Message; }
        finally { if (!IsDisposed) Busy(false); operation?.Dispose(); operation = null; }
    }
    private async Task TestSpeechAsync()
    {
        if (operation != null) return;
        using var picker = new OpenFileDialog { Title = "选择日语录音（前 30 秒；使用语音识别 API）", Filter = "音频文件|*.wav;*.mp3;*.flac;*.m4a" };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var current = ReadRecognitionFields(); current.RecognitionMode = "api"; RecognitionEngines.Validate(current);
            operation = new(); Busy(true); speechStatus.Text = "正在发送音频并识别…";
            var sample = AudioFiles.Read(picker.FileName, 30);
            using var recognizer = RecognitionEngines.Create(current); await recognizer.StartAsync(current, operation.Token);
            var timer = System.Diagnostics.Stopwatch.StartNew(); string source = await recognizer.TranscribeAsync(sample.Bytes, operation.Token);
            if (!IsDisposed) { speechStatus.Text = $"识别成功 · {sample.Duration:F1} 秒录音，用时 {timer.Elapsed.TotalSeconds:F2} 秒"; MessageBox.Show(this, source.Length == 0 ? "未识别到语音。" : source, "API 识别结果（日语原文）"); }
        }
        catch (OperationCanceledException) { if (!IsDisposed) speechStatus.Text = "识别请求已取消或超时。"; }
        catch (Exception ex) { if (!IsDisposed) speechStatus.Text = ex.Message; }
        finally { if (!IsDisposed) Busy(false); operation?.Dispose(); operation = null; }
    }
    private async Task DownloadAsync()
    {
        if (operation != null || !EnsureFolder()) return;
        var model = (ModelInfo)recognition.SelectedItem!;
        try
        {
            operation = new(); Busy(true); downloadStatus.Text = "连接模型下载站…";
            await Models.DownloadAsync(model, new Progress<int>(p => { if (!IsDisposed) downloadStatus.Text = $"下载并校验：{p}%"; }), operation.Token);
            if (!IsDisposed) UpdateModelStatus();
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!IsDisposed) downloadStatus.Text = "下载失败。请检查网络，或按使用说明手动放入模型。"; }
        finally { if (!IsDisposed) Busy(false); operation?.Dispose(); operation = null; }
    }
    private void UpdateModelStatus() => downloadStatus.Text = string.IsNullOrWhiteSpace(SettingsStore.ModelDirectory) ? "请选择模型存放文件夹。" : Models.IsReady((ModelInfo)recognition.SelectedItem!) ? "所选模型已就绪。" : "尚未下载。首次下载需要网络，之后可离线识别。";
}

internal sealed class SettingsDeck : Panel
{
    private readonly FlowLayoutPanel navigation;
    private readonly Panel content;
    private readonly Panel body;
    private readonly List<(SidebarButton Button, Control Page)> pages = [];
    internal int SelectedPageIndex { get; private set; }
    public SettingsDeck()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        navigation = new FlowLayoutPanel { Dock = DockStyle.Left, Width = 196, Padding = new Padding(16, 25, 16, 16), BackColor = Theme.Background, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        navigation.Paint += (_, e) => { using var line = new Pen(Theme.Border); e.Graphics.DrawLine(line, navigation.Width - 1, 0, navigation.Width - 1, navigation.Height); };
        var title = Theme.Label("言代Kotoshiro"); title.Font = new Font("Microsoft YaHei UI", 11, FontStyle.Bold); title.Margin = new Padding(10, 0, 0, 6);
        var caption = Theme.Label("偏好设置", true); caption.Margin = new Padding(10, 0, 0, 26);
        navigation.Controls.Add(title); navigation.Controls.Add(caption);
        content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Margin = Padding.Empty };
        body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Margin = Padding.Empty };
        body.Controls.Add(content); Controls.Add(body); Controls.Add(navigation);
    }
    public void SetFooter(Control footer) => body.Controls.Add(footer);
    internal Control PageAt(int index) => pages[index].Page;
    public void AddPage(string name, Control page)
    {
        int index = pages.Count;
        var icon = index switch { 0 => ShellIcon.Translation, 1 => ShellIcon.Microphone, _ => ShellIcon.Appearance };
        var button = new SidebarButton(name, icon) { Size = new Size(164, 44), Margin = new Padding(0, 0, 0, 4) };
        button.Click += (_, _) => ShowPage(index); navigation.Controls.Add(button);
        var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Surface, Margin = Padding.Empty, Visible = false };
        viewport.Controls.Add(page); content.Controls.Add(viewport); pages.Add((button, viewport));
        if (index == 0) ShowPage(0);
    }
    public void ShowPage(int index)
    {
        SelectedPageIndex = index;
        for (int i = 0; i < pages.Count; i++)
        {
            bool selected = i == index;
            pages[i].Button.Selected = selected;
            pages[i].Page.Visible = selected;
            if (selected) pages[i].Page.BringToFront();
        }
    }
}
