using System.Net;
using System.Text;

namespace Kotoshiro;

internal static class SpeechApiModelsTests
{
    private const string SpeechSecret = "speech-model-metadata-fixture-secret";
    private const string TranslationSecret = "translation-metadata-fixture-secret";
    private static void Check(bool value, string message) { if (!value) throw new Exception("FAIL: speech API models " + message); }

    internal static async Task<string[]> RunAsync()
    {
        Check(SpeechApiModels.Endpoint("https://speech.example/v1/").AbsoluteUri == "https://speech.example/v1/models", "base model endpoint");
        Check(SpeechApiModels.Endpoint("https://speech.example/v1/audio/transcriptions/").AbsoluteUri == "https://speech.example/v1/models", "complete transcription endpoint");
        Check(SpeechApiModels.Endpoint("https://api.siliconflow.cn/v1/audio/transcriptions").AbsoluteUri == "https://api.siliconflow.cn/v1/models?type=audio&sub_type=speech-to-text", "official speech-to-text metadata filter");
        foreach (string invalid in new[] { "http://speech.example/v1", "https://user:password@speech.example/v1", "https://speech.example/v1?key=unsafe", "https://speech.example/v1#unsafe", "https://speech.example/v1/chat/completions", "https://api.deepseek.com/v1" })
        {
            try { SpeechApiModels.Endpoint(invalid); throw new Exception("unsafe speech metadata endpoint accepted"); }
            catch (ArgumentException) { }
        }

        var settings = new AppSettings
        {
            RecognitionMode = "api", SpeechApiProvider = "custom", SpeechApiBase = "https://api.siliconflow.cn/v1/audio/transcriptions", SpeechApiModel = "handwritten-japanese-model",
            // Invalid unrelated translation settings must neither be used nor normalized.
            ApiBase = "http://translation-invalid.example/chat/completions", ApiProvider = "chatgpt", ApiProtocol = "invalid", ApiModel = "translation-selection"
        };
        settings.SetSpeechKey(SpeechSecret); settings.SetKey(TranslationSecret);
        string before = System.Text.Json.JsonSerializer.Serialize(settings);
        int requests = 0;
        var siliconModels = await SpeechApiModels.FetchAsync(settings, CancellationToken.None, new Handler((request, _) =>
        {
            requests++;
            Check(request.Method == HttpMethod.Get && request.Content == null, "metadata-only GET without audio/inference");
            Check(request.RequestUri!.AbsoluteUri == "https://api.siliconflow.cn/v1/models?type=audio&sub_type=speech-to-text", "speech endpoint independent of translation address");
            Check(request.Headers.Authorization?.Parameter == SpeechSecret && !request.Headers.ToString().Contains(TranslationSecret), "speech credential isolation");
            return Task.FromResult(Json("""
                {"data":[
                  {"id":"Qwen/Qwen3-ASR-1.7B","name":"Qwen Japanese ASR"},
                  {"id":"FunAudioLLM/SenseVoiceSmall"},
                  {"id":"Qwen/Qwen3-ASR-1.7B"},
                  {"id":"Qwen/Qwen3-ForcedAligner-0.6B"},
                  {"id":"deepseek-ai/DeepSeek-V4-Flash"},
                  {"id":"TeleAI/TeleSpeechASR"},
                  {"id":"Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice"},
                  {"id":"unknown-japanese-asr"},
                  {"id":"Qwen/Qwen3-ASR-1.7B-zh-finetuned"},
                  {"id":"gpt-4o-mini-transcribe"}
                ]}
                """));
        }));
        Check(requests == 1 && siliconModels.Select(x => x.Id).SequenceEqual(new[] { "FunAudioLLM/SenseVoiceSmall", "Qwen/Qwen3-ASR-1.7B" }), "exact documented Japanese-ASR intersection, excluding text/TTS/Chinese-only/unknown/aligner");
        Check(siliconModels[1].Name == "Qwen Japanese ASR", "display metadata retained");
        Check(System.Text.Json.JsonSerializer.Serialize(settings) == before, "caller settings and both encrypted credentials unchanged");

        var custom = settings.Clone(); custom.SpeechApiBase = "https://speech.example/v1";
        var openaiModels = await SpeechApiModels.FetchAsync(custom, CancellationToken.None, new Handler((request, _) =>
        {
            Check(request.RequestUri!.AbsoluteUri == "https://speech.example/v1/models", "custom provider does not receive SiliconFlow-only query");
            return Task.FromResult(Json("""
                {"data":[{"id":"whisper-1"},{"id":"gpt-4o-mini-transcribe"},{"id":"gpt-4o-mini-transcribe-2025-12-15"},{"id":"whisper-large-v3-en"},{"id":"gpt-live-transcribe"},{"id":"gpt-4o-mini-tts"},{"id":"unknown-transcribe"},{"id":"Qwen/Qwen3-ASR-1.7B"}]}
                """));
        }));
        Check(openaiModels.Select(x => x.Id).SequenceEqual(SpeechApiModels.Defaults().Select(x => x.Id)), "confirmed Japanese compatible IDs only; no realtime/new snapshot or name-based heuristics");

        foreach (string payload in new[] { "{\"data\":[]}", "{\"data\":[{\"id\":\"unknown-japanese-asr\"},{\"id\":\"TeleAI/TeleSpeechASR\"}]}" })
        {
            try { await SpeechApiModels.FetchAsync(settings, CancellationToken.None, new Handler((_, _) => Task.FromResult(Json(payload)))); throw new Exception("empty supported result accepted"); }
            catch (InvalidDataException ex) { Check(!ex.ToString().Contains(SpeechSecret) && settings.SpeechApiModel == "handwritten-japanese-model", "empty/unknown result retains manual model and safe error"); }
        }
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.NotFound, HttpStatusCode.Redirect })
        {
            try
            {
                await SpeechApiModels.FetchAsync(settings, CancellationToken.None, new Handler((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(SpeechSecret + TranslationSecret) })));
                throw new Exception("HTTP error accepted");
            }
            catch (HttpRequestException ex) { Check(ex.StatusCode == status && !ex.ToString().Contains(SpeechSecret) && !ex.ToString().Contains(TranslationSecret), "credential-safe status error " + status); }
        }
        try { await SpeechApiModels.FetchAsync(settings, CancellationToken.None, new Handler((_, _) => throw new HttpRequestException(SpeechSecret))); throw new Exception("network error accepted"); }
        catch (HttpRequestException ex) { Check(ex.StatusCode == null && !ex.ToString().Contains(SpeechSecret), "raw network exception sanitized"); }
        try { await SpeechApiModels.FetchAsync(settings, CancellationToken.None, new Handler((_, _) => Task.FromResult(Json(SpeechSecret)))); throw new Exception("malformed JSON accepted"); }
        catch (InvalidDataException ex) { Check(!ex.ToString().Contains(SpeechSecret), "raw malformed response sanitized"); }
        try
        {
            await SpeechApiModels.FetchAsync(settings, CancellationToken.None, new Handler((_, _) =>
            {
                var response = Json("{}"); response.Content.Headers.ContentLength = 2 * 1024 * 1024 + 1; return Task.FromResult(response);
            }));
            throw new Exception("oversize list accepted");
        }
        catch (InvalidDataException) { }

        var missing = settings.Clone(); missing.SetSpeechKey("");
        try { await SpeechApiModels.FetchAsync(missing, CancellationToken.None, new Handler((_, _) => throw new Exception("must not send with translation key"))); throw new Exception("translation key used as speech fallback"); }
        catch (InvalidOperationException ex) { Check(ex.Message.Contains("识别 API 密钥"), "speech key required separately"); }
        var badKey = settings.Clone(); badKey.SetSpeechKey("fixture\nsecret");
        try { await SpeechApiModels.FetchAsync(badKey, CancellationToken.None, new Handler((_, _) => throw new Exception("must not send unsafe key"))); throw new Exception("control character key accepted"); }
        catch (ArgumentException) { }

        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await SpeechApiModels.FetchAsync(settings, cancelled.Token, new Handler((_, _) => throw new Exception("cancelled request sent"))); throw new Exception("pre-cancellation ignored"); }
        catch (OperationCanceledException) { }
        using var during = new CancellationTokenSource();
        try
        {
            await SpeechApiModels.FetchAsync(settings, during.Token, new Handler(async (_, token) => { during.Cancel(); await Task.Delay(1000, token); return Json("{\"data\":[]}"); }));
            throw new Exception("active cancellation ignored");
        }
        catch (OperationCanceledException) { }

        return
        [
            "PASS: speech model discovery validates base/full endpoints and requests SiliconFlow speech-to-text metadata only",
            "PASS: speech model discovery uses independent address/key and never changes translation/manual settings",
            "PASS: Japanese transcription discovery excludes text, TTS, Chinese-only, realtime, aligner, unknown and fine-tuned IDs",
            "PASS: speech discovery empty/error results preserve manual models, sanitize credentials and retain HTTP statuses",
            "PASS: speech discovery enforces metadata size limits, separate credentials and cancellation"
        ];
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
