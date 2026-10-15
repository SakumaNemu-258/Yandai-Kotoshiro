using System.Net;
using System.Text;
using System.Text.Json;
using NAudio.Wave;

namespace Kotoshiro;

internal static class SelfTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); }
    internal static async Task LiveApiAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        var saved = SettingsStore.Load(); using var translator = new ApiTranslator(saved);
        string text = await translator.TranslateAsync("こんにちは。今日は一緒にゲームをしましょう。", CancellationToken.None, false);
        Check(!string.IsNullOrWhiteSpace(text), "real API returns text");
        await File.WriteAllTextAsync(Path.Combine(folder, "live-api.txt"), $"PASS: real saved {ApiProviders.Get(saved.ApiProvider).Name} / {saved.ApiProtocol} API call\nModel: {saved.ApiModel}\nElapsed: {translator.LastTiming!.TotalSeconds:F3}s\nChinese: {text}\nNo settings were modified. No credentials were logged.");
    }
    internal static async Task RunAsync(string folder)
    {
        Directory.CreateDirectory(folder); SettingsStore.Root = Path.Combine(folder, "settings");
        var results = new List<string>();
        results.AddRange(AppIconTests.Run());
        if (Environment.ProcessPath is { } executable && executable.EndsWith("Kotoshiro.exe", StringComparison.OrdinalIgnoreCase))
            results.AddRange(AppIconTests.VerifyPublishedExecutable(executable));
        results.AddRange(await TranslationContextTests.RunAsync(Path.Combine(folder, "translation-context")));
        results.AddRange(await GlossaryFilesTests.RunAsync(Path.Combine(folder, "glossary-files")));
        results.AddRange(await HistoryFilesTests.RunAsync(Path.Combine(folder, "history-files")));
        results.AddRange(await BrandingTests.RunAsync(Path.Combine(folder, "rebranding")));
        results.AddRange(await BilibiliTests.UnitAsync());
        results.AddRange(PlaybackGateTests.Run());
        results.AddRange(await ApiModelsTests.RunAsync());
        results.AddRange(await SpeechApiModelsTests.RunAsync());
        results.AddRange(await ApiRecognitionTests.RunAsync());
        results.AddRange(await BilibiliAccountTests.RunAsync(Path.Combine(folder, "bilibili-account")));
        results.AddRange(await BilibiliDanmakuTests.UnitAsync());
        results.AddRange(await ChatGptTests.RunAsync(Path.Combine(folder, "chatgpt")));
        var settings = new AppSettings(); settings.SetKey("fake-key-for-local-test"); SettingsStore.Save(settings);
        Check(!File.ReadAllText(Path.Combine(SettingsStore.Root, "settings.json")).Contains("fake-key"), "key not plaintext");
        Check(SettingsStore.Load().GetKey() == "fake-key-for-local-test", "DPAPI roundtrip"); results.Add("PASS: encrypted key save/load");
        Check(string.IsNullOrWhiteSpace(SettingsStore.ModelDirectory) && !Models.IsReady(Models.Get("base")), "no default C drive model directory");
        var speechSettings = settings.Clone(); speechSettings.UiTheme = "light"; speechSettings.RecognitionMode = "api";
        speechSettings.SpeechApiProvider = "custom"; speechSettings.SpeechApiBase = "https://speech.example.test/v1"; speechSettings.SpeechApiModel = "fixture-speech"; speechSettings.SetSpeechKey("separate-speech-test-secret");
        SettingsStore.Save(speechSettings); var savedSpeech = SettingsStore.Load();
        Check(savedSpeech.UiTheme == "light" && savedSpeech.RecognitionMode == "api" && savedSpeech.SpeechApiProvider == "custom" && savedSpeech.SpeechApiBase == speechSettings.SpeechApiBase && savedSpeech.SpeechApiModel == "fixture-speech", "theme and speech settings persist");
        Check(savedSpeech.GetKey() == settings.GetKey() && savedSpeech.GetSpeechKey() == "separate-speech-test-secret", "speech and translation keys persist separately");
        Check(!File.ReadAllText(Path.Combine(SettingsStore.Root, "settings.json")).Contains("separate-speech-test-secret"), "saved speech key not plaintext");
        RecognitionEngines.Validate(savedSpeech); SettingsStore.Save(settings); SettingsStore.Load();
        results.Add("PASS: white theme and API recognition settings persist without a model folder; speech and translation keys remain separate and encrypted");
        settings.ModelFolder = Path.Combine(folder, "chosen-models"); SettingsStore.Save(settings); SettingsStore.Load();
        Check(SettingsStore.ModelDirectory == Path.GetFullPath(settings.ModelFolder), "custom directory persists");
        Check(GpuSetup.DirectoryPath.StartsWith(SettingsStore.ModelDirectory), "GPU components follow model folder");
        results.Add("PASS: explicit model folder, persistence, and GPU component location");
        byte[] fixture = [1, 2, 3, 4]; var known = new ModelInfo("fixture", "fixture", 4, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fixture)));
        string migrationFrom = Path.Combine(folder, "migration-source"), migrationTo = Path.Combine(folder, "migration-destination"); Directory.CreateDirectory(migrationFrom);
        await File.WriteAllBytesAsync(Path.Combine(migrationFrom, known.FileName), fixture);
        bool committed = false;
        int migrated = await ModelMigration.MigrateAsync(migrationFrom, migrationTo, new Progress<string>(), CancellationToken.None, () => { Check(File.Exists(Path.Combine(migrationFrom, known.FileName)), "source remains until commit"); committed = true; return Task.CompletedTask; }, [known]);
        Check(migrated == 1 && committed && !File.Exists(Path.Combine(migrationFrom, known.FileName)) && File.Exists(Path.Combine(migrationTo, known.FileName)), "verified move after commit");
        results.Add("PASS: model migration copies/verifies before commit and removes only original known model");
        await File.WriteAllBytesAsync(Path.Combine(migrationFrom, known.FileName), fixture);
        await File.WriteAllBytesAsync(Path.Combine(migrationTo, known.FileName), [9, 9, 9, 9]);
        try { await ModelMigration.MigrateAsync(migrationFrom, migrationTo, new Progress<string>(), CancellationToken.None, () => Task.CompletedTask, [known]); throw new Exception("conflicting model accepted"); }
        catch (InvalidDataException) { Check(File.Exists(Path.Combine(migrationFrom, known.FileName)), "conflict keeps source"); }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { await ModelMigration.MigrateAsync(migrationFrom, Path.Combine(folder, "cancelled-move"), new Progress<string>(), cancelled.Token, () => Task.CompletedTask, [known]); throw new Exception("cancel ignored"); }
            catch (OperationCanceledException) { Check(File.Exists(Path.Combine(migrationFrom, known.FileName)), "cancel keeps source"); }
        }
        results.Add("PASS: cancelled or conflicting migration preserves original model");
        string badArchive = Path.Combine(folder, "bad-gpu.zip"); await File.WriteAllBytesAsync(badArchive, fixture);
        try { await GpuSetup.InstallAsync(new Progress<string>(), CancellationToken.None, badArchive); throw new Exception("checksum accepted wrong GPU package"); }
        catch (InvalidDataException) { Check(!GpuSetup.IsInstalled(), "corrupt package not installed"); }
        results.Add("PASS: GPU installer rejects invalid archive checksum");
        Check(ApiTranslator.Endpoint("https://api.deepseek.com/v1/").AbsoluteUri == "https://api.deepseek.com/v1/chat/completions", "endpoint join");
        bool rejects = false; try { ApiTranslator.Endpoint("http://insecure.example"); } catch (ArgumentException) { rejects = true; } Check(rejects, "HTTPS required");
        var handler = new FakeHandler(); using (var translator = new ApiTranslator(settings, handler))
        {
            Check(await translator.TranslateAsync("こんにちは。", CancellationToken.None) == "你好。", "translation result");
            Check(await translator.TranslateAsync("ありがとう。", CancellationToken.None) == "你好。", "second translation");
            Check(handler.LastMessages == 4, "context follows first request");
            var apiTiming = translator.LastTiming;
            Check(apiTiming != null && apiTiming.ResponseWaitSeconds >= 0 && apiTiming.BodyReadSeconds >= 0 && apiTiming.TotalSeconds >= apiTiming.ResponseWaitSeconds + apiTiming.BodyReadSeconds, "API latency spans include response body");
        }
        results.Add("PASS: DeepSeek request/auth/disabled thinking/response/context");
        using (var translator = new ApiTranslator(settings, new FakeHandler(HttpStatusCode.Unauthorized)))
        {
            try { await translator.TranslateAsync("テスト", CancellationToken.None); throw new Exception("missing API error"); }
            catch (HttpRequestException ex) { Check(!ex.Message.Contains("fake-key") && ex.StatusCode == HttpStatusCode.Unauthorized, "safe API error"); }
        }
        results.Add("PASS: API failure doesn't expose server body or key");
        foreach (string providerId in new[] { "openai", "gemini", "claude", "custom" })
        {
            var preset = ApiProviders.Get(providerId); var other = settings.Clone();
            other.ApiProvider = providerId; other.ApiProtocol = preset.Protocol; other.ApiBase = preset.Address.Length > 0 ? preset.Address : "https://compatible.example/v1"; other.ApiModel = "fixture-model";
            var adapter = new ProviderHandler(other);
            using var translator = new ApiTranslator(other, adapter);
            Check(await translator.TranslateAsync("こんにちは。", CancellationToken.None) == "你好。", providerId + " text parsing");
            Check(await translator.TranslateAsync("ありがとう。", CancellationToken.None) == "你好。", providerId + " followup context");
            Check(adapter.Calls == 2, providerId + " calls");
        }
        results.Add("PASS: OpenAI Responses, Gemini compatible, Claude Messages, custom request/auth/context/text parsing");
        var legacy = new AppSettings { ApiBase = "https://compatible.example/v1", ApiModel = "existing" }; legacy.SetKey("legacy-custom-secret"); ApiProviders.Normalize(legacy);
        Check(legacy.ApiProvider == "custom" && legacy.ApiProtocol == "chat" && legacy.GetKey() == "legacy-custom-secret", "legacy custom setup preserved");
        var saved = settings.Clone(); var profileSecret = new AppSettings(); profileSecret.SetKey("other-provider-secret");
        saved.ApiProfiles["openai"] = new("https://api.openai.com/v1", "fixture-model", profileSecret.EncryptedKey, "responses"); SettingsStore.Save(saved);
        var reloaded = SettingsStore.Load();
        Check(reloaded.GetKey() == "fake-key-for-local-test" && new AppSettings { EncryptedKey = reloaded.ApiProfiles["openai"].EncryptedKey }.GetKey() == "other-provider-secret", "profiles keep distinct encrypted keys");
        Check(!File.ReadAllText(Path.Combine(SettingsStore.Root, "settings.json")).Contains("other-provider-secret"), "profile key not plaintext");
        results.Add("PASS: legacy configuration migration and separate encrypted provider credentials");
        try { ApiTranslator.Endpoint("https://api.openai.com/v1/responses", "chat"); throw new Exception("mismatched endpoint accepted"); } catch (ArgumentException) { }
        Check(ApiTranslator.Endpoint("https://api.anthropic.com/v1/messages", "messages").AbsoluteUri.EndsWith("/v1/messages"), "complete endpoint not duplicated");
        var segmenter = new AudioSegmenter(3); var chunks = new List<float[]>(); segmenter.Ready += (values, _) => chunks.Add(values);
        for (int i = 0; i < 40; i++) segmenter.Push(new float[1600]); Check(chunks.Count == 0, "silence skipped");
        float[] tone = Enumerable.Range(0, 1600).Select(i => (float)(0.1 * Math.Sin(i * Math.PI / 18))).ToArray();
        for (int i = 0; i < 15; i++) segmenter.Push(tone); for (int i = 0; i < 7; i++) segmenter.Push(new float[1600]);
        Check(chunks.Count == 1, "signal emits at pause");
        using (var wave = new WaveFileReader(new MemoryStream(AudioSegmenter.WaveBytes(chunks[0])))) Check(wave.WaveFormat.SampleRate == 16000 && wave.WaveFormat.Channels == 1 && wave.WaveFormat.BitsPerSample == 16, "PCM format");
        results.Add("PASS: silence gate, pause segmentation, 16 kHz mono WAV");
        foreach (int limit in new[] { 3, 6 })
        {
            var continuous = new AudioSegmenter(limit); int blocks = 0, firstBlock = -1;
            continuous.Ready += (_, _) => { if (firstBlock < 0) firstBlock = blocks; };
            for (blocks = 1; blocks <= limit * 10 + 1; blocks++) continuous.Push(tone);
            Check(firstBlock == limit * 10, "continuous audio waits for selected segment limit");
        }
        results.Add("PASS: continuous audio submits at 3s/6s according to selected segment limit");
        using (var audio = new LiveAudioCapture(3))
        {
            for (int i = 0; i < 4; i++) audio.Chunks.Writer.TryWrite(new([1], DateTime.UtcNow, i));
            Check(audio.Chunks.Reader.TryRead(out var oldest) && oldest.Duration == 2, "bounded queue drops old audio");
        }
        results.Add("PASS: bounded capture queue keeps fresh chunks");
        var devices = LiveAudioCapture.Devices(); results.Add($"INFO: {devices.Length} active Windows output device(s)");
        if (devices.Length > 0)
        {
            using var audio = new LiveAudioCapture(3); audio.Start(devices[0].Id); await Task.Delay(800);
            results.Add("PASS: real Windows loopback device opened and closed (audio content not evaluated)");
        }
        await File.WriteAllLinesAsync(Path.Combine(folder, "self-test.txt"), results);
    }
    internal static async Task EngineAsync(string folder, string modelId)
    {
        Directory.CreateDirectory(folder); SettingsStore.Root = folder;
        SettingsStore.SetModelDirectory(Path.Combine(folder, "models"));
        var model = Models.Get(modelId);
        if (!Models.IsReady(model)) await Models.DownloadAsync(model, new Progress<int>(), CancellationToken.None);
        using var engine = new WhisperEngine(); var timer = System.Diagnostics.Stopwatch.StartNew();
        await engine.StartAsync(new AppSettings { RecognitionModel = model.Id, ModelFolder = SettingsStore.ModelDirectory }, CancellationToken.None);
        var sample = AudioFiles.Read(Path.Combine(folder, "sample.flac"), 30);
        var loaded = timer.Elapsed.TotalSeconds; timer.Restart();
        string text = await engine.TranscribeAsync(sample.Bytes, CancellationToken.None);
        Check(!string.IsNullOrWhiteSpace(text), "engine returns transcription");
        await File.WriteAllTextAsync(Path.Combine(folder, "engine-test-" + model.Id + ".txt"), $"Japanese sample loaded and transcribed through real localhost inference. Model: {model.Id}. Model load: {loaded:F1}s. Audio duration: {sample.Duration:F1}s. Inference: {timer.Elapsed.TotalSeconds:F1}s.\n{text}\nSingle clean Japanese sample; not a live-stream accuracy benchmark. No DeepSeek API call was made.");
    }
    internal static async Task GpuAsync(string folder, string archive)
    {
        Directory.CreateDirectory(folder); SettingsStore.Root = folder; SettingsStore.SetModelDirectory(Path.Combine(folder, "models"));
        string? name = await GpuSetup.DetectNvidiaAsync(); Check(name != null, "NVIDIA detected");
        var updates = new List<string>();
        await GpuSetup.InstallAsync(new SyncProgress<string>(updates.Add), CancellationToken.None, archive);
        Check(GpuSetup.IsInstalled(), "GPU bundle installed with runtime DLLs");
        var settings = new AppSettings { RecognitionModel = "small", ModelFolder = SettingsStore.ModelDirectory, UseGpu = true };
        var sample = AudioFiles.Read(Path.Combine(folder, "sample.flac"), 30);
        string text; double elapsed;
        using (var engine = new WhisperEngine())
        {
            await engine.StartAsync(settings, CancellationToken.None);
            Check(engine.Mode.StartsWith("显卡加速"), "real CUDA compute backend selected");
            await engine.TranscribeAsync(AudioSegmenter.WaveBytes(new float[16000]), CancellationToken.None);
            var timer = System.Diagnostics.Stopwatch.StartNew(); text = await engine.TranscribeAsync(sample.Bytes, CancellationToken.None); elapsed = timer.Elapsed.TotalSeconds;
            Check(text.Contains("マレーシア"), "Japanese GPU transcription");
        }
        settings.UseGpu = false;
        using (var cpu = new WhisperEngine()) { await cpu.StartAsync(settings, CancellationToken.None); Check(cpu.Mode == "CPU", "CPU selection still respected with installed GPU components"); }
        await File.WriteAllTextAsync(Path.Combine(folder, "gpu-test.txt"), $"PASS: detected {name}\nPASS: official SHA-256-verified package installed in selected model directory\nPASS: real CUDA backend initialization and inference\nPASS: forced CPU mode with GPU package installed\nSmall model / {sample.Duration:F1}s Japanese sample / warmed GPU inference {elapsed:F2}s\n{text}\nNo DeepSeek request was made.\n" + string.Join("\n", updates));
    }
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
    private sealed class ProviderHandler(AppSettings expected) : HttpMessageHandler
    {
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Check(request.RequestUri == ApiTranslator.Endpoint(expected.ApiBase, expected.ApiProtocol), "provider endpoint");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)); var root = body.RootElement;
            Check(root.GetProperty("model").GetString() == "fixture-model" && !root.GetProperty("stream").GetBoolean(), "provider model and non-streaming");
            Check(!root.TryGetProperty("thinking", out _), "no leaked DeepSeek-specific field");
            string response;
            if (expected.ApiProtocol == "responses")
            {
                Check(request.Headers.Authorization?.Parameter == "fake-key-for-local-test", "Responses Bearer auth");
                Check(!root.GetProperty("store").GetBoolean() && root.GetProperty("instructions").GetString()!.Contains("中文字幕"), "Responses instructions/store");
                Check(root.GetProperty("input").GetArrayLength() == (Calls == 1 ? 1 : 3), "Responses conversation");
                response = "{\"status\":\"completed\",\"output\":[{\"type\":\"reasoning\",\"summary\":[]},{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"你好。\"}]}]}";
            }
            else if (expected.ApiProtocol == "messages")
            {
                Check(request.Headers.Authorization == null && request.Headers.GetValues("x-api-key").Single() == "fake-key-for-local-test" && request.Headers.GetValues("anthropic-version").Single() == "2023-06-01", "Claude auth/version");
                Check(root.GetProperty("max_tokens").GetInt32() == 1024 && root.GetProperty("system").GetString()!.Contains("中文字幕"), "Claude system/max tokens");
                Check(root.GetProperty("messages").GetArrayLength() == (Calls == 1 ? 1 : 3), "Claude conversation");
                response = "{\"stop_reason\":\"end_turn\",\"content\":[{\"type\":\"thinking\",\"thinking\":\"not translation\"},{\"type\":\"text\",\"text\":\"你好。\"}]}";
            }
            else
            {
                Check(request.Headers.Authorization?.Parameter == "fake-key-for-local-test", "compatible Bearer auth");
                Check(root.GetProperty("messages").GetArrayLength() == (Calls == 1 ? 2 : 4), "compatible conversation");
                response = "{\"choices\":[{\"message\":{\"content\":\"你好。\"}}]}";
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
    private sealed class FakeHandler(HttpStatusCode code = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int LastMessages;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Check(request.RequestUri!.AbsoluteUri == "https://api.deepseek.com/chat/completions", "official URL");
            Check(request.Headers.Authorization?.Parameter == "fake-key-for-local-test", "auth header");
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Check(json.RootElement.GetProperty("thinking").GetProperty("type").GetString() == "disabled", "no reasoning latency");
            LastMessages = json.RootElement.GetProperty("messages").GetArrayLength();
            return new HttpResponseMessage(code) { Content = new StringContent(code == HttpStatusCode.OK ? "{\"choices\":[{\"message\":{\"content\":\"你好。\"}}]}" : "fake-key-for-local-test", Encoding.UTF8, "application/json") };
        }
    }
}
