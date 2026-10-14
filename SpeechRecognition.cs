using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Kotoshiro;

public interface IRecognitionEngine : IDisposable
{
    string Mode { get; }
    Task StartAsync(AppSettings settings, CancellationToken token);
    Task<string> TranscribeAsync(byte[] wave, CancellationToken token);
}

public static class RecognitionEngines
{
    public static bool UsesApi(AppSettings settings) => settings.RecognitionMode == "api";
    public static string ModelLabel(AppSettings settings) => UsesApi(settings) ? "API 识别 · " + settings.SpeechApiModel : "本地识别 · " + Models.Get(settings.RecognitionModel).Id;

    public static IRecognitionEngine Create(AppSettings settings, HttpMessageHandler? handler = null) => settings.RecognitionMode switch
    {
        "" or "local" => new WhisperEngine(),
        "api" => new SpeechApiRecognition(handler),
        _ => throw new InvalidOperationException("请选择本地识别或 API 识别。")
    };

    public static bool IsConfigured(AppSettings settings)
    {
        try { Validate(settings); return true; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException) { return false; }
    }

    public static void Validate(AppSettings settings)
    {
        if (UsesApi(settings))
        {
            SpeechApiRecognition.Endpoint(settings.SpeechApiBase);
            SpeechApiRecognition.ValidateModel(settings.SpeechApiModel);
            if (string.IsNullOrWhiteSpace(settings.GetSpeechKey())) throw new InvalidOperationException("请在“语音识别”设置中填写识别 API 密钥。");
            return;
        }
        if (settings.RecognitionMode is not ("" or "local")) throw new InvalidOperationException("请选择本地识别或 API 识别。");
        if (string.IsNullOrWhiteSpace(settings.ModelFolder)) throw new InvalidOperationException("请在“语音识别”设置中选择本地模型存放文件夹。");
        string path = Path.Combine(Path.GetFullPath(settings.ModelFolder), Models.Get(settings.RecognitionModel).FileName);
        if (!File.Exists(path) || new FileInfo(path).Length != Models.Get(settings.RecognitionModel).Size)
            throw new InvalidOperationException("请先在“语音识别”设置中下载所选本地模型，或切换到 API 识别。");
    }
}

/// <summary>Transcribes each bounded WAV segment in its original Japanese, independently of the translation service.</summary>
public sealed class SpeechApiRecognition : IRecognitionEngine
{
    public static readonly string[] Models = ["whisper-1", "gpt-transcribe", "gpt-4o-mini-transcribe", "gpt-4o-transcribe"];
    private const int MaxAudioBytes = 25 * 1024 * 1024;
    private const int MaxResponseBytes = 1024 * 1024;
    private readonly HttpClient client;
    private readonly TimeSpan requestTimeout;
    private Uri? endpoint;
    private string model = "", key = "";
    private bool disposed;
    public string Mode => "API 识别" + (model.Length > 0 ? " · " + model : "");

    public SpeechApiRecognition(HttpMessageHandler? handler = null, TimeSpan? requestTimeout = null)
    {
        client = handler == null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) : new HttpClient(handler);
        // Apply one deadline to both response headers and body reading, so cancellation also interrupts slow streams.
        client.Timeout = Timeout.InfiniteTimeSpan;
        this.requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(60);
        if (this.requestTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(requestTimeout));
    }

    public static Uri Endpoint(string address)
    {
        if (!Uri.TryCreate(address?.Trim().TrimEnd('/'), UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("识别 API 地址需要使用 HTTPS，例如 https://api.openai.com/v1。");
        if (uri.Host.Equals("api.deepseek.com", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("DeepSeek 官方 API 目前不支持语音识别。请在“翻译服务”中使用 DeepSeek，识别选择本地或支持语音转写的 API。");
        string value = uri.AbsoluteUri.TrimEnd('/');
        const string suffix = "/audio/transcriptions";
        if (new[] { "/chat/completions", "/responses", "/messages", "/audio/translations", "/audio/speech" }.Any(x => value.EndsWith(x, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("请填写支持 audio/transcriptions 的语音识别 API 地址，文本翻译接口不能用于识别。");
        return new Uri(value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? value : value + suffix);
    }

    internal static void ValidateModel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("请在“语音识别”设置中选择或填写识别模型。");
        if (value.Length > 200 || value.Any(char.IsControl)) throw new ArgumentException("识别模型名称无效，请检查设置。");
    }

    public Task StartAsync(AppSettings settings, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        token.ThrowIfCancellationRequested();
        var nextEndpoint = Endpoint(settings.SpeechApiBase);
        ValidateModel(settings.SpeechApiModel);
        string nextKey = settings.GetSpeechKey();
        if (string.IsNullOrWhiteSpace(nextKey)) throw new InvalidOperationException("请在“语音识别”设置中填写识别 API 密钥。");
        if (nextKey.Any(char.IsControl)) throw new ArgumentException("识别 API 密钥包含无效字符，请重新填写。");
        endpoint = nextEndpoint; model = settings.SpeechApiModel.Trim(); key = nextKey;
        // Configuration only: starting API mode never launches/downloads the local model or makes a billable request.
        return Task.CompletedTask;
    }

    public async Task<string> TranscribeAsync(byte[] wave, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        token.ThrowIfCancellationRequested();
        if (endpoint == null) throw new InvalidOperationException("识别 API 尚未初始化，请重新开始翻译。");
        if (wave == null || wave.Length == 0 || wave.Length > MaxAudioBytes) throw new ArgumentException("待识别音频为空或超过 25 MB，请缩短音频分段。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(requestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var body = new MultipartFormDataContent();
            var audio = new ByteArrayContent(wave);
            audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            body.Add(audio, "file", "live.wav");
            body.Add(new StringContent(model), "model");
            // SiliconFlow documents file/model only; SenseVoice detects the language.
            if (!endpoint.Host.Equals("api.siliconflow.cn", StringComparison.OrdinalIgnoreCase))
            {
                // Current GPT Transcribe accepts an array. Existing Whisper/4o and compatible services use a single hint.
                body.Add(new StringContent("ja"), model.Equals("gpt-transcribe", StringComparison.OrdinalIgnoreCase) || model.StartsWith("gpt-transcribe-", StringComparison.OrdinalIgnoreCase) ? "languages[]" : "language");
                body.Add(new StringContent("json"), "response_format");
            }
            request.Content = body;
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode) throw ServiceError(response.StatusCode);
            byte[] content = await ReadBoundedAsync(response.Content, deadline.Token);
            try
            {
                using var json = JsonDocument.Parse(content);
                if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("识别 API 没有返回 JSON text 字段，请检查接口和模型。");
                return text.GetString()?.Trim() ?? "";
            }
            catch (JsonException) { throw new InvalidDataException("识别 API 返回格式不正确，需要兼容 audio/transcriptions 的 JSON 响应。"); }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("识别 API 请求超时，请检查网络或更换识别服务。"); }
        catch (HttpRequestException ex) when (ex.StatusCode == null) { throw new HttpRequestException("无法连接识别 API，请检查网络和识别 API 地址。", null); }
    }

    private static HttpRequestException ServiceError(HttpStatusCode status)
    {
        string message = status switch
        {
            HttpStatusCode.Unauthorized => "识别 API 密钥无效，请检查“语音识别”设置。",
            HttpStatusCode.PaymentRequired => "识别 API 余额不足，请到识别服务平台检查余额。",
            HttpStatusCode.Forbidden => "识别 API 拒绝访问，请检查账号权限或地区限制。",
            HttpStatusCode.NotFound => "识别 API 地址或模型不存在，请检查“语音识别”设置。",
            HttpStatusCode.BadRequest => "识别 API 不接受这段音频或所选模型，请检查接口和模型。",
            HttpStatusCode.TooManyRequests => "识别 API 请求过于频繁或额度不足，请稍后重试。",
            _ when (int)status is >= 300 and < 400 => "识别 API 地址发生重定向，请填写服务商提供的最终 HTTPS 地址。",
            _ => $"识别服务返回 HTTP {(int)status}，请稍后重试。"
        };
        // Never expose arbitrary server bodies, request URLs, transcripts, or credentials in UI errors.
        return new HttpRequestException(message, null, status);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken token)
    {
        if (content.Headers.ContentLength > MaxResponseBytes) throw new InvalidDataException("识别 API 响应过大，请检查服务和模型。");
        await using var input = await content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + count > MaxResponseBytes) throw new InvalidDataException("识别 API 响应过大，请检查服务和模型。");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    public void Dispose() { disposed = true; key = ""; client.Dispose(); }
}
