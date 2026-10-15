namespace Kotoshiro;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            if (args.Length >= 2 && args[0] == "--self-test") { SelfTests.RunAsync(args[1]).GetAwaiter().GetResult(); return; }
            if (args.Length >= 2 && args[0] == "--models-check")
            {
                var current = SettingsStore.Load();
                var available = ApiModels.FetchAsync(current, CancellationToken.None).GetAwaiter().GetResult();
                Directory.CreateDirectory(args[1]);
                File.WriteAllLines(Path.Combine(args[1], "available-models.txt"), new[] { "Provider: " + current.ApiProvider, "Metadata GET only; no translation inference." }.Concat(available.Select(x => x.Id)));
                return;
            }
            if (args.Length >= 3 && args[0] == "--bilibili-check") { BilibiliTests.CheckRoomAsync(args[1], args[2]).GetAwaiter().GetResult(); return; }
            if (args.Length >= 3 && args[0] == "--bilibili-danmaku-test") { BilibiliDanmakuTests.LiveReadAsync(BilibiliResolver.RoomId(args[1]), args[2]).GetAwaiter().GetResult(); return; }
            if (args.Length >= 3 && args[0] == "--quality-ui-test")
            {
                Directory.CreateDirectory(args[2]); SettingsStore.Root = Path.Combine(args[2], "settings");
                using var form = new BilibiliPlayerForm(new AppSettings { BilibiliRoom = args[1] });
                form.Shown += async (_, _) => { try { await form.VerifyQualityUiAsync(args[2]); } catch (Exception ex) { await File.WriteAllTextAsync(Path.Combine(args[2], "quality-test-error.txt"), ex.ToString()); Environment.ExitCode = 1; } form.Close(); };
                Application.Run(form); return;
            }
            if (args.Length >= 3 && args[0] == "--player-test") { BilibiliTests.PlayerAsync(args[1], args[2]).GetAwaiter().GetResult(); return; }
            if (args.Length >= 3 && args[0] == "--live-ui-test")
            {
                Directory.CreateDirectory(args[2]); var current = SettingsStore.Load();
                current.RecognitionMode = "local"; current.ApiProvider = "deepseek"; current.ApiProtocol = "chat"; current.ApiBase = "https://api.deepseek.com"; current.SetKey("local-test-only"); current.PlaybackDelay = 10; current.ModelFolder = args[2];
                using var form = new BilibiliPlayerForm(current); var mock = new BilibiliTests.MockTranslation();
                form.Shown += async (_, _) =>
                {
                    try { await form.RunLiveTestAsync(args[1], mock); }
                    catch (Exception ex) { await File.WriteAllTextAsync(Path.Combine(args[2], "live-test-error.txt"), ex.ToString()); Environment.ExitCode = 1; }
                    form.Close();
                };
                Application.Run(form); return;
            }
            if (args.Length >= 3 && args[0] == "--player-ui-test")
            {
                string testInput = Path.GetFullPath(args[1]);
                Directory.CreateDirectory(args[2]); var current = SettingsStore.Load();
                current.RecognitionMode = "local"; current.ApiProvider = "deepseek"; current.ApiProtocol = "chat"; current.ApiBase = "https://api.deepseek.com"; current.SetKey("local-test-only"); current.PlaybackDelay = 6; current.ModelFolder = args[2];
                using var form = new BilibiliPlayerForm(current); var mock = new BilibiliTests.MockTranslation();
                form.Shown += async (_, _) =>
                {
                    await form.RunLocalTestAsync(testInput, mock);
                    await File.WriteAllTextAsync(Path.Combine(args[2], "player-ui-test.txt"), $"Completed: {form.CompletedTest}\nMock translation calls: {mock.Calls}\nStatus: {form.LastTestStatus}\nReal local recognizer and real embedded video player; mock translation, no API usage.");
                    if (!form.CompletedTest || mock.Calls == 0) Environment.ExitCode = 1;
                    else
                    {
                        try { await form.VerifyCancelTestAsync(testInput); await File.AppendAllTextAsync(Path.Combine(args[2], "player-ui-test.txt"), "\nPASS: second run stopped during startup/capture within 10s; recorder and cache cleaned."); }
                        catch (Exception ex) { await File.AppendAllTextAsync(Path.Combine(args[2], "player-ui-test.txt"), "\nFAIL: " + ex.Message); Environment.ExitCode = 1; }
                    }
                    form.Close();
                };
                Application.Run(form); return;
            }
            if (args.Length >= 3 && args[0] == "--api-player-ui-test")
            {
                string testInput = Path.GetFullPath(args[1]), folder = Path.GetFullPath(args[2]); Directory.CreateDirectory(folder);
                SettingsStore.Root = Path.Combine(folder, "settings");
                var current = new AppSettings { RecognitionMode = "api", SpeechApiBase = "https://speech.example.test/v1", SpeechApiModel = "whisper-1", ApiProvider = "deepseek", ApiProtocol = "chat", ModelFolder = folder, PlaybackDelay = 6 };
                current.SetKey("translation-test-only"); current.SetSpeechKey("speech-test-only");
                string scenario = args.Length >= 4 ? args[3] : "success";
                bool expectFailure = scenario is "repeated" or "unauthorized";
                using var form = new BilibiliPlayerForm(current); var translation = new BilibiliTests.MockTranslation(); var speech = new ApiPlayerTestSpeech(scenario == "repeated" ? 3 : scenario is "transient" or "unauthorized" ? 1 : 0, scenario == "unauthorized" ? System.Net.HttpStatusCode.Unauthorized : System.Net.HttpStatusCode.TooManyRequests);
                var recordedCaptions = new List<(string Source, string Chinese)>();
                form.CaptionTranslated += (source, chinese) => recordedCaptions.Add((source, chinese));
                form.Shown += async (_, _) =>
                {
                    try
                    {
                        if (scenario == "refresh")
                        {
                            var initialRun = form.RunApiLocalTestAsync(testInput, translation, speech);
                            await form.VerifyRefreshTestAsync(initialRun);
                            if (recordedCaptions.Count == 0 || recordedCaptions.Any(x => string.IsNullOrWhiteSpace(x.Source) || string.IsNullOrWhiteSpace(x.Chinese))) throw new InvalidOperationException("Successful delayed captions did not reach the history event.");
                            await File.WriteAllTextAsync(Path.Combine(folder, "api-player-ui-test.txt"), $"PASS: refresh button cancelled active media/recognition, removed old cache, restarted with an independent cache, and completed real playback with simulated APIs.\nPASS: {recordedCaptions.Count} successful delayed captions reached the history event.\nSpeech calls: {speech.Calls}; translation calls: {translation.Calls}; no paid calls.");
                            return;
                        }
                        await form.RunApiLocalTestAsync(testInput, translation, speech);
                        if (expectFailure)
                        {
                            if (form.CompletedTest || speech.Calls != (scenario == "unauthorized" ? 1 : 3) || translation.Calls != 0 || !form.LastTestStatus.Contains(scenario == "unauthorized" ? "密钥无效" : "请求过于频繁")) throw new InvalidOperationException("Recognition failure policy was not enforced: " + form.LastTestStatus);
                            if (Directory.Exists(Path.Combine(Branding.ModelRuntimeDirectory(folder), "player-cache")) && Directory.EnumerateFiles(Path.Combine(Branding.ModelRuntimeDirectory(folder), "player-cache"), "*", SearchOption.AllDirectories).Any()) throw new InvalidOperationException("Failed recognition session left player cache files.");
                            await File.WriteAllTextAsync(Path.Combine(folder, "api-player-ui-test.txt"), $"PASS: {scenario} recognition failure stopped safely after {speech.Calls} request(s); no text translation was called and own player cache was cleaned.\nNo paid API calls.");
                            return;
                        }
                        if (!form.CompletedTest || speech.Calls == 0 || translation.Calls == 0) throw new InvalidOperationException("API speech/player pipeline did not complete: " + form.LastTestStatus);
                        if (scenario == "transient" && (speech.Calls < 3 || translation.Calls < 2)) throw new InvalidOperationException("Player did not recover after the first recognition failure.");
                        await form.VerifyCancelTestAsync(testInput);
                        await File.WriteAllTextAsync(Path.Combine(folder, "api-player-ui-test.txt"), $"PASS: real video/audio decode and playback with API recognition backend + simulated speech and translation responses. Scenario: {scenario}.\nSpeech calls: {speech.Calls}; uploaded WAV bytes: {speech.AudioBytes}; translation calls: {translation.Calls}.\nNo Whisper process or model download, no paid API calls.\nPASS: second run cancelled and own cache cleaned.");
                    }
                    catch (Exception ex) { await File.WriteAllTextAsync(Path.Combine(folder, "api-player-test-error.txt"), ex.ToString()); Environment.ExitCode = 1; }
                    finally { form.Close(); }
                };
                Application.Run(form); return;
            }
            if (args.Length >= 3 && args[0] == "--gpu-test") { SelfTests.GpuAsync(args[1], args[2]).GetAwaiter().GetResult(); return; }
            if (args.Length >= 3 && args[0] == "--latency-test") { LatencyDiagnostics.RunSavedAsync(args[1], args[2]).GetAwaiter().GetResult(); return; }
            if (args.Length >= 2 && args[0] == "--translation-test") { SelfTests.LiveApiAsync(args[1]).GetAwaiter().GetResult(); return; }
            if (args.Length >= 2 && args[0] == "--preview")
            {
                Directory.CreateDirectory(args[1]);
                SettingsStore.Root = Path.Combine(Path.GetTempPath(), "Kotoshiro-preview-" + Guid.NewGuid().ToString("N"));
                var settings = new AppSettings { UiTheme = args.Length >= 3 ? Theme.Normalize(args[2]) : "light", OverlayHeight = 320 };
                Theme.Use(settings.UiTheme);
                using var form = new MainForm(settings);
                form.Shown += async (_, _) =>
                {
                    try
                    {
                    await Task.Delay(250); Application.DoEvents();
                    File.WriteAllText(Path.Combine(args[1], "stop-ui-checks.txt"), await form.VerifyStopForPreviewAsync());
                    File.WriteAllText(Path.Combine(args[1], "modeless-settings-checks.txt"), form.VerifyModelessSettingsForPreview(args[1]));
                    File.WriteAllText(Path.Combine(args[1], "history-export-ui-checks.txt"), await form.VerifyHistoryExportForPreviewAsync(args[1]));
                    File.WriteAllText(Path.Combine(args[1], "main-layout-checks.txt"), form.VerifyMainLayoutForPreview(args[1]));
                    File.WriteAllText(Path.Combine(args[1], "desktop-shell-checks.txt"), DesktopShell.VerifyForPreview(form, Path.Combine(args[1], "main-native-frame.png")));
                    File.WriteAllText(Path.Combine(args[1], "icon-window-checks.txt"), AppIconTests.VerifyForPreview(form));
                    if (LiveAudioCapture.Devices().Length > 0 && string.IsNullOrWhiteSpace(form.SelectedDeviceName)) throw new InvalidOperationException("预览检查：未选择声音设备。");
                    using var bitmap = DesktopShell.CapturePreview(form); bitmap.Save(Path.Combine(args[1], "main.png"));
                    using var dialog = new SettingsForm(settings); dialog.Show(form); await Task.Delay(200); Application.DoEvents();
                    File.WriteAllText(Path.Combine(args[1], "settings-layout-checks.txt"), dialog.VerifyLayoutForPreview());
                    File.AppendAllText(Path.Combine(args[1], "desktop-shell-checks.txt"), Environment.NewLine + DesktopShell.VerifyForPreview(dialog, Path.Combine(args[1], "settings-native-frame.png")));
                    File.AppendAllText(Path.Combine(args[1], "icon-window-checks.txt"), Environment.NewLine + AppIconTests.VerifyForPreview(dialog));
                    dialog.VerifyProviderSwitchForPreview();
                    dialog.VerifyModelsForPreview(); await Task.Delay(100); Application.DoEvents();
                    dialog.VerifyRecognitionForPreview();
                    dialog.VerifySpeechModelsForPreview();
                    File.WriteAllText(Path.Combine(args[1], "speech-model-fetch-ui-checks.txt"), await dialog.VerifySpeechModelFetchForPreviewAsync());
                    File.WriteAllText(Path.Combine(args[1], "context-glossary-ui-checks.txt"), dialog.VerifyContextForPreview() + Environment.NewLine + dialog.VerifyGlossaryForPreview());
                    File.WriteAllText(Path.Combine(args[1], "recognition-model-ui-checks.txt"), "PASS: editable custom speech model IDs, provider-specific presets and preservation across provider/mode switches.");
                    using var image = DesktopShell.CapturePreview(dialog); image.Save(Path.Combine(args[1], "settings.png"));
                    SaveMinimumPreview(dialog, Path.Combine(args[1], "settings-minimum.png"));
                    dialog.ShowContextForPreview(3); Application.DoEvents();
                    using var contextDefault = DesktopShell.CapturePreview(dialog); contextDefault.Save(Path.Combine(args[1], "context-and-glossary.png"));
                    dialog.ShowContextForPreview(6); Application.DoEvents();
                    using var contextSix = DesktopShell.CapturePreview(dialog); contextSix.Save(Path.Combine(args[1], "context-six.png"));
                    SaveMinimumPreview(dialog, Path.Combine(args[1], "context-glossary-minimum.png"), () => dialog.ShowContextForPreview(6));
                    dialog.ShowContextForPreview(3);
                    dialog.ShowChatGptForPreview(); Application.DoEvents();
                    using var loginImage = DesktopShell.CapturePreview(dialog); loginImage.Save(Path.Combine(args[1], "chatgpt.png"));
                    using (var auth = new ChatGptAuth())
                    {
                        var previewAccount = new ChatGptRegistration { Email = "preview@example.test", ClientId = "preview-client", Subject = "preview-subject", AccessToken = "preview-token-not-real", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), Scopes = ["chatgpt.tokens.use.direct"], Models = [new("preview-model", "示例订阅模型（仅界面预览）")] };
                        auth.SeedForTestAsync(previewAccount).GetAwaiter().GetResult(); dialog.SelectChatGptForPreview(previewAccount.Id, "preview-model"); Application.DoEvents();
                        using var signedInImage = DesktopShell.CapturePreview(dialog); signedInImage.Save(Path.Combine(args[1], "chatgpt-signed-in-demo.png"));
                    }
                    dialog.ShowRecognitionForPreview(); await Task.Delay(150); Application.DoEvents();
                    using var local = DesktopShell.CapturePreview(dialog); local.Save(Path.Combine(args[1], "recognition.png"));
                    SaveMinimumPreview(dialog, Path.Combine(args[1], "recognition-minimum.png"));
                    dialog.ShowApiRecognitionForPreview(); await Task.Delay(150); Application.DoEvents();
                    using var remote = DesktopShell.CapturePreview(dialog); remote.Save(Path.Combine(args[1], "api-recognition.png"));
                    dialog.ShowCustomSpeechForPreview(); await Task.Delay(150); Application.DoEvents();
                    using var customSpeech = DesktopShell.CapturePreview(dialog); customSpeech.Save(Path.Combine(args[1], "custom-api-recognition.png"));
                    dialog.ShowSubtitleStyleForPreview(); await Task.Delay(150); Application.DoEvents();
                    using var style = DesktopShell.CapturePreview(dialog); style.Save(Path.Combine(args[1], "subtitle-settings.png"));
                    SaveMinimumPreview(dialog, Path.Combine(args[1], "subtitle-settings-minimum.png"));
                    dialog.ShowSubtitleStyleForPreview(false); Application.DoEvents();
                    using var chineseStyle = DesktopShell.CapturePreview(dialog); chineseStyle.Save(Path.Combine(args[1], "subtitle-settings-chinese-only.png"));
                    dialog.ShowSubtitleStyleForPreview(settings.ShowJapanese); Application.DoEvents();
                    File.WriteAllText(Path.Combine(args[1], "ui-checks.txt"), SubtitleOverlayTests.Unit(args[1]));
                    using var overlay = new SubtitleOverlay(settings);
                    foreach (var (source, translated) in new[] { ("今日も配信に来てくれてありがとう！", "谢谢大家今天也来直播间！"), ("今日は一緒にゲームをやっていきましょう。", "今天一起玩游戏吧。"), ("ここは少し難しいので、ゆっくり進めます。", "这里有点难，我们慢慢来。"), ("みんなのコメントも見ていますよ。", "我也在看大家发的弹幕哦。"), ("次はこのルートを試してみましょう。", "接下来试试走这条路线吧。") }) overlay.SetCaption(source, translated);
                    overlay.Location = new Point(form.Left + 30, form.Top + 400); overlay.Show(form); await Task.Delay(250); Application.DoEvents();
                    using var caption = overlay.CapturePreview(); caption.Save(Path.Combine(args[1], "subtitle.png"));
                    using var bili = new BilibiliPlayerForm(settings); bili.SetRoomForPreview("https://live.bilibili.com/22748536"); bili.SeedCommunityPreview(); bili.Show(form); Application.DoEvents();
                    File.WriteAllText(Path.Combine(args[1], "player-layout-checks.txt"), bili.VerifyLayoutForPreview(args[1]));
                    File.AppendAllText(Path.Combine(args[1], "icon-window-checks.txt"), Environment.NewLine + AppIconTests.VerifyForPreview(bili));
                    using var playerPreview = new Bitmap(bili.Width, bili.Height); bili.DrawToBitmap(playerPreview, new Rectangle(Point.Empty, bili.Size)); playerPreview.Save(Path.Combine(args[1], "bilibili-player.png")); bili.Close();
                    using (var biliAccounts = new BilibiliAccountStore())
                    using (var biliLogin = new BilibiliLoginForm(biliAccounts, preview: true))
                    {
                        biliLogin.Show(form); Application.DoEvents(); using var loginPreview = new Bitmap(biliLogin.Width, biliLogin.Height); biliLogin.DrawToBitmap(loginPreview, new Rectangle(Point.Empty, biliLogin.Size)); loginPreview.Save(Path.Combine(args[1], "bilibili-login-demo.png")); biliLogin.Close();
                    }
                    overlay.Dispose(); dialog.Close();
                    }
                    catch (Exception ex) { File.WriteAllText(Path.Combine(args[1], "preview-error.txt"), ex.ToString()); Environment.ExitCode = 1; }
                    finally { form.Close(); }
                };
                Application.Run(form); return;
            }
            if (args.Length >= 2 && args[0] == "--engine-test") { SelfTests.EngineAsync(args[1], args.Length >= 3 ? args[2] : "base").GetAwaiter().GetResult(); return; }
            var currentSettings = SettingsStore.Load(); Theme.Use(currentSettings.UiTheme); Application.Run(new MainForm(currentSettings));
        }
        catch (Exception ex)
        {
            if (args.Length > 0) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "test-error.txt"), ex.ToString()); Environment.ExitCode = 1; }
            else MessageBox.Show(ex.Message, "言代Kotoshiro", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void SaveMinimumPreview(Form form, string path, Action? beforeCapture = null)
    {
        var original = form.ClientSize;
        try
        {
            form.ClientSize = form.MinimumSize; form.PerformLayout(); Application.DoEvents();
            beforeCapture?.Invoke();
            using var bitmap = DesktopShell.CapturePreview(form); bitmap.Save(path);
        }
        finally { form.ClientSize = original; form.PerformLayout(); Application.DoEvents(); }
    }
}
