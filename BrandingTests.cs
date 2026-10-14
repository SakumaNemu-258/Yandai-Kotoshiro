using System.Security.Cryptography;
using System.Text.Json;

namespace Kotoshiro;

internal static class BrandingTests
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception("FAIL: rebranding compatibility: " + message); }

    internal static async Task<List<string>> RunAsync(string folder)
    {
        var results = new List<string>();
        string originalRoot = SettingsStore.Root, originalModels = SettingsStore.ModelDirectory;
        string isolated = Path.GetFullPath(folder), current = Path.Combine(isolated, Branding.Identity), legacy = Path.Combine(isolated, "TingyiLive");
        Directory.CreateDirectory(isolated);
        try
        {
            Check(Branding.DisplayName == "言代Kotoshiro" && Branding.ResolveDataRoot(isolated) == current, "new installations use the new name");
            Check(!Directory.Exists(current) && !Directory.Exists(legacy), "path resolution creates no data folders");

            Directory.CreateDirectory(legacy);
            string models = Path.Combine(isolated, "chosen-model-folder"); Directory.CreateDirectory(models);
            var old = new AppSettings { UiTheme = "light", ModelFolder = models, UseGpu = true, BilibiliRoom = "22748536", RecognitionMode = "api", SpeechApiProvider = "custom", SpeechApiModel = "fixture-speech" };
            old.SetKey("rebranding-translation-fixture"); old.SetSpeechKey("rebranding-speech-fixture");
            SettingsStore.Root = legacy; SettingsStore.Save(old);
            byte[] settingsBytes = await File.ReadAllBytesAsync(Path.Combine(legacy, "settings.json"));
            var chatVault = new ChatGptVault { Accounts = [new ChatGptRegistration { Id = "fixture-legacy-account", Email = "legacy@example.test", ClientId = "fixture-legacy-client", Subject = "fixture-subject", AccessToken = "fixture-legacy-access", RefreshToken = "fixture-legacy-refresh", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), Scopes = ["chatgpt.tokens.use.direct"], Welcomed = true }] };
            var biliVault = new BilibiliAccountVault { UserId = 123456, DisplayName = "兼容测试账号", ExpiresAt = DateTimeOffset.UtcNow.AddDays(1), Cookies = new() { ["DedeUserID"] = "123456", ["DedeUserID__ckMd5"] = "fixture-md5", ["SESSDATA"] = "fixture-session", ["bili_jct"] = "fixture-csrf" } };
            byte[] chatBytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(chatVault), null, DataProtectionScope.CurrentUser);
            byte[] biliBytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(biliVault), null, DataProtectionScope.CurrentUser);
            await File.WriteAllBytesAsync(Path.Combine(legacy, "chatgpt.dat"), chatBytes);
            await File.WriteAllBytesAsync(Path.Combine(legacy, "bilibili.dat"), biliBytes);

            SettingsStore.Root = Branding.ResolveDataRoot(isolated);
            var restored = SettingsStore.Load();
            Check(SettingsStore.Root == legacy && restored.UiTheme == "light" && restored.ModelFolder == models && restored.UseGpu && restored.BilibiliRoom == old.BilibiliRoom, "legacy settings and chosen model folder are retained");
            Check(restored.GetKey() == "rebranding-translation-fixture" && restored.GetSpeechKey() == "rebranding-speech-fixture", "both DPAPI keys still decrypt");
            using (var chat = new ChatGptAuth())
            {
                var account = chat.Accounts().Single();
                Check(account.Id == "fixture-legacy-account" && account.IsSignedIn && account.CanUsePlan && account.Welcomed, "default ChatGPT login store reads legacy account");
            }
            using (var bili = new BilibiliAccountStore())
            { var account = await bili.GetAsync(); Check(account?.UserId == 123456 && account.Csrf == "fixture-csrf", "default Bilibili login store reads legacy account locally"); }
            Check(settingsBytes.SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(legacy, "settings.json"))) && chatBytes.SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(legacy, "chatgpt.dat"))) && biliBytes.SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(legacy, "bilibili.dat"))), "reading legacy settings and logins leaves the file bytes unchanged");
            restored.FontSize++; SettingsStore.Save(restored);
            Check(!Directory.Exists(current) && SettingsStore.Load().FontSize == restored.FontSize, "legacy installation continues saving in the same root");
            results.Add("PASS: rebranding preserves legacy settings, chosen model folder, both encrypted API keys and local ChatGPT/Bilibili login files; no migration or network request");

            string legacyRuntime = Path.Combine(models, ".tingyi"), gpu = Path.Combine(legacyRuntime, "engines", "cuda12-v1.8.2");
            Directory.CreateDirectory(gpu);
            await File.WriteAllBytesAsync(Path.Combine(gpu, "whisper-server.exe"), [1, 2, 3]);
            await File.WriteAllBytesAsync(Path.Combine(gpu, "ggml-cuda.dll"), [4, 5]);
            await File.WriteAllTextAsync(Path.Combine(gpu, "installed.json"), JsonSerializer.Serialize(new Dictionary<string, long> { ["whisper-server.exe"] = 3, ["ggml-cuda.dll"] = 2 }));
            Check(Branding.ModelRuntimeDirectory(models) == legacyRuntime && GpuSetup.DirectoryPath == gpu && GpuSetup.IsInstalled(), "installed legacy GPU components are recognized without download");
            Directory.CreateDirectory(Path.Combine(models, ".kotoshiro"));
            Check(Branding.ModelRuntimeDirectory(models) == legacyRuntime, "old components remain available if an empty new cache also exists");
            string freshModels = Path.Combine(isolated, "fresh-model-folder");
            Check(Branding.ModelRuntimeDirectory(freshModels) == Path.Combine(freshModels, ".kotoshiro") && !Directory.Exists(freshModels), "new model runtime path uses new name without creating folders");
            Check(Branding.ModelRuntimeDirectory("") == "", "empty model folder stays unconfigured");
            results.Add("PASS: existing GPU components/cache path is reused; fresh installations use the new runtime folder name without default model downloads");

            Directory.CreateDirectory(current);
            Check(Branding.ResolveDataRoot(isolated) == current, "explicit new data root takes priority when both roots exist");
            SettingsStore.Root = current; var fresh = new AppSettings { UiTheme = "dark" }; SettingsStore.Save(fresh);
            Check(SettingsStore.Load().UiTheme == "dark" && File.Exists(Path.Combine(legacy, "settings.json")) && chatBytes.SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(legacy, "chatgpt.dat"))) && biliBytes.SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(legacy, "bilibili.dat"))), "new root does not overwrite legacy settings or accounts");
            results.Add("PASS: new data root has deterministic priority and leaves legacy files intact; all compatibility checks use isolated fixtures");
            return results;
        }
        finally { SettingsStore.Root = originalRoot; SettingsStore.SetModelDirectory(originalModels); }
    }
}
