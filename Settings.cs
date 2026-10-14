using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kotoshiro;

public sealed class AppSettings
{
    public const int MaximumTranslationContextSegments = 30;
    public int TranslationContextSegments { get; set; } = 3;
    [JsonIgnore] public int NormalizedTranslationContextSegments => Math.Clamp(TranslationContextSegments, 0, MaximumTranslationContextSegments);
    public string UiTheme { get; set; } = "light";
    public string ApiBase { get; set; } = "https://api.deepseek.com";
    public string ApiModel { get; set; } = "deepseek-flash";
    public string EncryptedKey { get; set; } = "";
    public string ApiProvider { get; set; } = "";
    public string ApiProtocol { get; set; } = "";
    public string ChatGptAccountId { get; set; } = "";
    public string ChatGptModel { get; set; } = "";
    public string BilibiliRoom { get; set; } = "";
    public int BilibiliQuality { get; set; }
    public int PlaybackDelay { get; set; } = 10;
    public bool WaitForSubtitles { get; set; } = true;
    public Dictionary<string, ApiProfile> ApiProfiles { get; set; } = [];
    public string RecognitionModel { get; set; } = "base";
    public string RecognitionMode { get; set; } = "local";
    public string SpeechApiProvider { get; set; } = "openai";
    public string SpeechApiBase { get; set; } = "https://api.openai.com/v1";
    public string SpeechApiModel { get; set; } = "whisper-1";
    public string SpeechEncryptedKey { get; set; } = "";
    public string ModelFolder { get; set; } = "";
    public bool UseGpu { get; set; }
    public int Threads { get; set; } = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
    public int SegmentSeconds { get; set; } = 6;
    public bool ShowJapanese { get; set; } = true;
    public int FontSize { get; set; } = 26;
    public int OverlayOpacity { get; set; } = 88;
    public string Glossary { get; set; } = GlossaryFiles.DefaultExample;
    public int OverlayX { get; set; } = -1;
    public int OverlayY { get; set; } = -1;
    public int OverlayWidth { get; set; } = 900;
    public int OverlayHeight { get; set; } = 320;

    public string GetKey()
    {
        if (string.IsNullOrWhiteSpace(EncryptedKey)) return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(EncryptedKey), null, DataProtectionScope.CurrentUser)); }
        catch (CryptographicException) { return ""; }
        catch (FormatException) { return ""; }
    }
    public void SetKey(string key) => EncryptedKey = string.IsNullOrWhiteSpace(key) ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), null, DataProtectionScope.CurrentUser));
    public string GetSpeechKey()
    {
        if (string.IsNullOrWhiteSpace(SpeechEncryptedKey)) return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(SpeechEncryptedKey), null, DataProtectionScope.CurrentUser)); }
        catch (CryptographicException) { return ""; }
        catch (FormatException) { return ""; }
    }
    public void SetSpeechKey(string key) => SpeechEncryptedKey = string.IsNullOrWhiteSpace(key) ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), null, DataProtectionScope.CurrentUser));
    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this))!;
}

public static class SettingsStore
{
    public static string Root { get; set; } = Branding.ResolveDataRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    public static string ModelDirectory { get; private set; } = "";
    public static string LegacyModelDirectory => Path.Combine(Root, "models");
    public static void SetModelDirectory(string path) => ModelDirectory = string.IsNullOrWhiteSpace(path) ? "" : Path.GetFullPath(path.Trim());
    public static AppSettings Load()
    {
        string path = Path.Combine(Root, "settings.json");
        if (!File.Exists(path)) { SetModelDirectory(""); var fresh = new AppSettings(); ApiProviders.Normalize(fresh); return fresh; }
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new();
            settings.TranslationContextSegments = settings.NormalizedTranslationContextSegments;
            settings.Glossary = GlossaryFiles.UpgradeDefault(settings.Glossary);
            ApiProviders.Normalize(settings);
            SetModelDirectory(settings.ModelFolder); return settings;
        }
        catch (JsonException) { throw new InvalidDataException("设置文件损坏。请在本机数据目录备份 settings.json 后重新设置。"); }
    }
    public static void Save(AppSettings settings)
    {
        settings.TranslationContextSegments = settings.NormalizedTranslationContextSegments;
        ApiProviders.Normalize(settings);
        settings.ApiProfiles[settings.ApiProvider] = new(settings.ApiBase, settings.ApiModel, settings.EncryptedKey, settings.ApiProtocol);
        Directory.CreateDirectory(Root);
        string path = Path.Combine(Root, "settings.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
}

public record ModelInfo(string Id, string Label, long Size, string Sha256)
{
    public override string ToString() => Label;
    public string FileName => $"ggml-{Id}.bin";
    public string LocalPath => string.IsNullOrWhiteSpace(SettingsStore.ModelDirectory) ? "" : Path.Combine(SettingsStore.ModelDirectory, FileName);
}

public static class Models
{
    public static readonly ModelInfo[] All =
    [
        new("tiny", "Tiny · 轻量 / 约 78 MB", 77691713, "be07e048e1e599ad46341c8d2a135645097a538221678b7acdd1b1919c6e1b21"),
        new("base", "Base · 入门 / 约 148 MB", 147951465, "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe"),
        new("small", "Small · 更准确 / 约 488 MB", 487601967, "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b"),
        new("large-v3-turbo", "Turbo · 高配置 / 约 1.62 GB", 1624555275, "1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69")
    ];
    public static ModelInfo Get(string id) => All.FirstOrDefault(x => x.Id == id) ?? All[1];
    public static bool IsReady(ModelInfo info) => File.Exists(info.LocalPath) && new FileInfo(info.LocalPath).Length == info.Size;
    public static async Task DownloadAsync(ModelInfo info, IProgress<int> progress, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(SettingsStore.ModelDirectory)) throw new InvalidOperationException("请先选择模型存放文件夹。");
        Directory.CreateDirectory(SettingsStore.ModelDirectory);
        string destination = info.LocalPath;
        string temp = destination + ".part";
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            using var response = await client.GetAsync($"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{info.FileName}", HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = File.Create(temp))
            {
                byte[] buffer = new byte[128 * 1024]; long total = 0; int previous = -1;
                int count;
                while ((count = await input.ReadAsync(buffer, token)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, count), token); total += count;
                    int pct = (int)Math.Min(99, total * 100 / info.Size);
                    if (pct != previous) { progress.Report(pct); previous = pct; }
                }
            }
            await using var file = File.OpenRead(temp);
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(file, token));
            if (!hash.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("模型校验失败，请重新下载。");
            file.Close(); token.ThrowIfCancellationRequested(); File.Move(temp, destination, true); progress.Report(100);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
