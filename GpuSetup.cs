using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Kotoshiro;

public static class GpuSetup
{
    public const string ArchiveUrl = "https://github.com/ggml-org/whisper.cpp/releases/download/v1.8.2/whisper-cublas-12.4.0-bin-x64.zip";
    public const string ArchiveHash = "54a3c012e6c567e2e8bed874cdf49c55f018ec8d90bb5d5271568aa573291b2d";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public static string DirectoryPath => string.IsNullOrWhiteSpace(SettingsStore.ModelDirectory) ? "" : Path.Combine(Branding.ModelRuntimeDirectory(SettingsStore.ModelDirectory), "engines", "cuda12-v1.8.2");
    public static string Executable => Path.Combine(DirectoryPath, "whisper-server.exe");
    public static bool IsInstalled()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(DirectoryPath)) return false;
            string marker = Path.Combine(DirectoryPath, "installed.json");
            if (!File.Exists(marker)) return false;
            var files = JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(marker));
            return files != null && files.ContainsKey("whisper-server.exe") && files.ContainsKey("ggml-cuda.dll") && files.All(x =>
                Path.GetFileName(x.Key) == x.Key && File.Exists(Path.Combine(DirectoryPath, x.Key)) && new FileInfo(Path.Combine(DirectoryPath, x.Key)).Length == x.Value);
        }
        catch (IOException) { return false; }
        catch (JsonException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    public static async Task<string?> DetectNvidiaAsync(CancellationToken token = default)
    {
        string[] paths = [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe")];
        string? path = paths.FirstOrDefault(File.Exists);
        if (path == null) return null;
        try
        {
            using var process = new Process { StartInfo = new(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
            process.StartInfo.ArgumentList.Add("--query-gpu=name"); process.StartInfo.ArgumentList.Add("--format=csv,noheader");
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync(token); var error = process.StandardError.ReadToEndAsync(token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(4000);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); token.ThrowIfCancellationRequested(); return null; }
            string name = (await output).Trim(); await error;
            return process.ExitCode == 0 && name.Length > 0 ? name.Replace("\r\n", " / ").Replace("\n", " / ") : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }
    public static async Task InstallAsync(IProgress<string> progress, CancellationToken token, string? localArchive = null)
    {
        await Gate.WaitAsync(token);
        if (string.IsNullOrWhiteSpace(SettingsStore.ModelDirectory)) { Gate.Release(); throw new InvalidOperationException("请先选择模型存放文件夹。"); }
        string installDirectory = DirectoryPath;
        string cache = Path.Combine(Branding.ModelRuntimeDirectory(SettingsStore.ModelDirectory), "downloads");
        string archive = Path.Combine(cache, "whisper-cuda-v1.8.2.zip.part");
        try
        {
            if (IsInstalled()) { progress.Report("显卡组件已准备好，正在测试…"); return; }
            Directory.CreateDirectory(cache);
            // A per-user lock also prevents two software instances writing the same cache.
            await using var fileLock = new FileStream(Path.Combine(cache, "gpu-setup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (localArchive == null)
            {
                using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                progress.Report("正在下载显卡组件（约 436 MB，仅首次需要）…");
                using var response = await client.GetAsync(ArchiveUrl, HttpCompletionOption.ResponseHeadersRead, token); response.EnsureSuccessStatusCode();
                long expected = response.Content.Headers.ContentLength ?? 0;
                await using var input = await response.Content.ReadAsStreamAsync(token);
                await using var output = File.Create(archive);
                byte[] buffer = new byte[128 * 1024]; int count, previous = -1; long total = 0;
                while ((count = await input.ReadAsync(buffer, token)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, count), token); total += count;
                    int percent = expected > 0 ? (int)(total * 100 / expected) : (int)(total / (1024 * 1024));
                    if (percent != previous) { previous = percent; progress.Report(expected > 0 ? $"正在下载显卡组件：{percent}%" : $"正在下载显卡组件：{percent} MB"); }
                }
            }
            else { archive = localArchive; }
            progress.Report("正在校验显卡组件…");
            await using (var input = File.OpenRead(archive))
            {
                string hash = Convert.ToHexString(await SHA256.HashDataAsync(input, token));
                if (!hash.Equals(ArchiveHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("显卡组件校验失败，请重试。");
            }
            progress.Report("正在准备显卡组件…");
            Directory.CreateDirectory(installDirectory);
            string marker = Path.Combine(installDirectory, "installed.json");
            if (File.Exists(marker)) File.Delete(marker);
            var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            using (var zip = ZipFile.OpenRead(archive))
            {
                foreach (var entry in zip.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    string name = entry.Name;
                    if (string.IsNullOrEmpty(name) || !(name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || name.Equals("whisper-server.exe", StringComparison.OrdinalIgnoreCase))) continue;
                    if (sizes.ContainsKey(name) || name != Path.GetFileName(name) || name.Contains('\\') || name.Contains(':')) throw new InvalidDataException("显卡组件目录结构无效。");
                    string target = Path.Combine(installDirectory, name);
                    await using var input = entry.Open(); await using var output = File.Create(target);
                    await input.CopyToAsync(output, token); sizes.Add(name, entry.Length);
                }
            }
            if (!sizes.ContainsKey("whisper-server.exe") || !sizes.ContainsKey("ggml-cuda.dll") || !sizes.Keys.Any(x => x.StartsWith("cublas64_")))
                throw new InvalidDataException("显卡组件缺少必要文件，请重试。");
            await File.WriteAllTextAsync(marker, JsonSerializer.Serialize(sizes), token);
            progress.Report("组件已准备好，正在测试显卡…");
        }
        finally
        {
            try { if (localArchive == null && File.Exists(archive)) File.Delete(archive); }
            finally { Gate.Release(); }
        }
    }
}
