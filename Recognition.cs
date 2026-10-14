using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Kotoshiro;

public sealed class WhisperEngine : IRecognitionEngine
{
    private Process? process;
    private readonly HttpClient client = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(120) };
    private Uri? baseUri;
    private volatile bool cudaActive;
    public string Mode { get; private set; } = "CPU";
    public async Task StartAsync(AppSettings settings, CancellationToken token)
    {
        if (settings.UseGpu)
        {
            string gpuPath = GpuSetup.IsInstalled() ? GpuSetup.Executable : Path.Combine(AppContext.BaseDirectory, "engine-gpu", "whisper-server.exe");
            if (File.Exists(gpuPath))
            {
                try
                {
                    await LaunchAsync(gpuPath, settings, false, token);
                    if (!cudaActive) throw new InvalidOperationException("未能使用显卡计算。");
                    Mode = "显卡加速（NVIDIA）"; return;
                }
                catch (OperationCanceledException) { throw; }
                catch { StopProcess(); }
            }
        }
        string cpuPath = Path.Combine(AppContext.BaseDirectory, "engine", "whisper-server.exe");
        await LaunchAsync(cpuPath, settings, true, token);
        Mode = settings.UseGpu ? "CPU（显卡暂不可用）" : "CPU";
    }
    private async Task LaunchAsync(string path, AppSettings settings, bool cpu, CancellationToken token)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("识别引擎缺失。请完整解压软件压缩包后运行。");
        var model = Models.Get(settings.RecognitionModel);
        if (!Models.IsReady(model)) throw new FileNotFoundException("请先在设置的“本地识别”页下载模型。");
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        baseUri = new Uri($"http://127.0.0.1:{port}/");
        var info = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = Path.GetDirectoryName(path)! };
        if (!cpu)
        {
            string cudaCache = Path.Combine(Branding.ModelRuntimeDirectory(SettingsStore.ModelDirectory), "cuda-cache");
            Directory.CreateDirectory(cudaCache);
            info.Environment["CUDA_CACHE_PATH"] = cudaCache;
        }
        foreach (string arg in new[] { "-m", model.LocalPath, "-l", "ja", "-t", Math.Clamp(settings.Threads, 1, 32).ToString(), "--host", "127.0.0.1", "--port", port.ToString(), "-nc", "-nf", "-sns", "-bs", "1", "-bo", "1" }) info.ArgumentList.Add(arg);
        if (cpu) info.ArgumentList.Add("-ng");
        process = new Process { StartInfo = info };
        cudaActive = false;
        process.ErrorDataReceived += (_, e) =>
        {
            // Observe the selected compute backend, not just the 'use_gpu' preference.
            string line = e.Data ?? "";
            if (line.Contains("CUDA", StringComparison.OrdinalIgnoreCase) && (line.Contains("compute buffer", StringComparison.OrdinalIgnoreCase) || line.Contains("using CUDA", StringComparison.OrdinalIgnoreCase))) cudaActive = true;
        };
        process.OutputDataReceived += (_, _) => { };
        if (!process.Start()) throw new InvalidOperationException("无法启动识别引擎。");
        process.BeginErrorReadLine(); process.BeginOutputReadLine();
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            if (process.HasExited) throw new InvalidOperationException("识别引擎启动失败。请检查模型；旧电脑可能不支持引擎需要的指令集。");
            try
            {
                using var check = CancellationTokenSource.CreateLinkedTokenSource(token); check.CancelAfter(700);
                using var response = await client.GetAsync(new Uri(baseUri, "health"), check.Token);
                if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound) return;
            }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            await Task.Delay(250, token);
        }
        throw new TimeoutException("模型加载超时。请尝试较小的模型。");
    }
    public async Task<string> TranscribeAsync(byte[] wave, CancellationToken token)
    {
        if (baseUri == null || process == null || process.HasExited) throw new InvalidOperationException("本地识别引擎已经停止，请重新开始。");
        using var body = new MultipartFormDataContent();
        body.Add(new ByteArrayContent(wave), "file", "live.wav");
        body.Add(new StringContent("ja"), "language");
        body.Add(new StringContent("false"), "translate");
        body.Add(new StringContent("json"), "response_format");
        body.Add(new StringContent("0"), "temperature");
        using var response = await client.PostAsync(new Uri(baseUri, "inference"), body, token);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return json.RootElement.GetProperty("text").GetString()?.Trim() ?? "";
    }
    private void StopProcess()
    {
        if (process == null) return;
        try { if (!process.HasExited) { process.Kill(true); process.WaitForExit(2000); } } catch (InvalidOperationException) { }
        process.Dispose(); process = null;
    }
    public void Dispose() { StopProcess(); client.Dispose(); }
}
