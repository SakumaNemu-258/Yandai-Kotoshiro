using System.Net;
using System.Text;

namespace Kotoshiro;

internal static class ApiRecognitionTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); }
    public static async Task<List<string>> RunAsync()
    {
        var results = new List<string>();
        var settings = new AppSettings { RecognitionMode = "api", SpeechApiBase = "https://speech.example/v1/", SpeechApiModel = "whisper-1", ModelFolder = "" };
        settings.SetKey("translation-only-fixture-secret");
        Check(!RecognitionEngines.IsConfigured(settings), "translation credentials do not configure speech recognition");
        settings.SetSpeechKey("speech-only-fixture-secret");
        Check(RecognitionEngines.IsConfigured(settings), "API recognition is configured without a local folder/model");
        Check(settings.GetKey() == "translation-only-fixture-secret" && settings.GetSpeechKey() == "speech-only-fixture-secret", "separate DPAPI speech key does not alter translation key");
        var clone = settings.Clone();
        Check(clone.GetSpeechKey() == settings.GetSpeechKey() && clone.SpeechEncryptedKey != "speech-only-fixture-secret", "speech key clone remains encrypted");
        using (var engine = RecognitionEngines.Create(settings)) Check(engine is SpeechApiRecognition, "API factory does not create Whisper process");
        using (var engine = RecognitionEngines.Create(new AppSettings())) Check(engine is WhisperEngine, "legacy local default still uses Whisper");
        Check(!RecognitionEngines.IsConfigured(new AppSettings()), "local mode still requires downloaded model");
        results.Add("PASS: independent encrypted speech/translation credentials; API mode needs no local model/GPU/folder; local default preserved");

        Check(SpeechApiRecognition.Endpoint(settings.SpeechApiBase).AbsoluteUri == "https://speech.example/v1/audio/transcriptions", "speech endpoint join");
        Check(SpeechApiRecognition.Endpoint("https://speech.example/v1/audio/transcriptions/").AbsoluteUri == "https://speech.example/v1/audio/transcriptions", "complete endpoint not duplicated");
        foreach (string address in new[] { "http://speech.example/v1", "https://user:secret@speech.example/v1", "https://speech.example/v1?key=secret", "https://speech.example/v1#key", "https://speech.example/v1/chat/completions", "https://speech.example/v1/audio/translations" })
        {
            try { SpeechApiRecognition.Endpoint(address); throw new Exception("invalid speech endpoint accepted"); } catch (ArgumentException) { }
        }
        results.Add("PASS: HTTPS speech endpoint joins correctly and rejects credentials/query/text-translation endpoints");
        foreach (string address in new[] { "https://api.deepseek.com", "https://api.deepseek.com/v1/audio/transcriptions" })
        {
            try { SpeechApiRecognition.Endpoint(address); throw new Exception("DeepSeek text-only official endpoint accepted for speech recognition"); }
            catch (ArgumentException ex) { Check(ex.Message.Contains("DeepSeek") && ex.Message.Contains("翻译服务"), "unsupported speech endpoint explains recognition/translation distinction"); }
        }
        results.Add("PASS: DeepSeek official text API is rejected for speech recognition before sending audio or credentials");

        byte[] wave = AudioSegmenter.WaveBytes(Enumerable.Range(0, 1600).Select(i => (float)(0.1 * Math.Sin(i * Math.PI / 18))).ToArray());
        foreach (string model in new[] { "whisper-1", "gpt-transcribe", "gpt-4o-mini-transcribe", "custom-japanese-asr" })
        {
            settings.SpeechApiModel = model;
            var handler = new CaptureHandler(settings, wave);
            using var engine = new SpeechApiRecognition(handler);
            await engine.StartAsync(settings, CancellationToken.None);
            Check(handler.Calls == 0, "API startup is configuration only, no paid probe");
            Check(await engine.TranscribeAsync(wave, CancellationToken.None) == "こんにちは。", "Japanese transcript text result");
            Check(handler.Calls == 1 && engine.Mode.Contains(model), "selected model reaches engine and request");
        }
        results.Add("PASS: WAV multipart preserves audio, selected/custom model, Japanese language schema and speech-only Bearer auth; no translation call");
        var siliconSettings = settings.Clone(); siliconSettings.SpeechApiBase = "https://api.siliconflow.cn/v1"; siliconSettings.SpeechApiModel = "FunAudioLLM/SenseVoiceSmall";
        using (var engine = new SpeechApiRecognition(new CaptureHandler(siliconSettings, wave)))
        {
            await engine.StartAsync(siliconSettings, CancellationToken.None);
            Check(await engine.TranscribeAsync(wave, CancellationToken.None) == "こんにちは。", "SiliconFlow transcription returns Japanese using its documented schema");
        }
        results.Add("PASS: SiliconFlow multipart contains only file/model and independent speech credentials; no unsupported OpenAI parameters");

        foreach (HttpStatusCode code in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.PaymentRequired, HttpStatusCode.NotFound, HttpStatusCode.TooManyRequests, HttpStatusCode.Redirect })
        {
            using var engine = new SpeechApiRecognition(new StaticHandler(code, "server-secret speech-only-fixture-secret"));
            await engine.StartAsync(settings, CancellationToken.None);
            try { await engine.TranscribeAsync(wave, CancellationToken.None); throw new Exception("speech HTTP error missing"); }
            catch (HttpRequestException ex) { Check(ex.StatusCode == code && !ex.ToString().Contains("server-secret") && !ex.ToString().Contains("speech-only-fixture-secret"), "speech error omits arbitrary server body and key"); }
        }
        using (var engine = new SpeechApiRecognition(new ThrowingHandler()))
        {
            await engine.StartAsync(settings, CancellationToken.None);
            try { await engine.TranscribeAsync(wave, CancellationToken.None); throw new Exception("network error missing"); }
            catch (HttpRequestException ex) { Check(!ex.ToString().Contains("raw-private-url") && ex.StatusCode == null, "network exception sanitized"); }
        }
        results.Add("PASS: API HTTP/network failures use safe actionable messages without server bodies or secrets");

        foreach (string content in new[] { "not JSON server-secret", "{\"error\":\"server-secret\"}", "{\"text\":42}" })
        {
            using var engine = new SpeechApiRecognition(new StaticHandler(HttpStatusCode.OK, content));
            await engine.StartAsync(settings, CancellationToken.None);
            try { await engine.TranscribeAsync(wave, CancellationToken.None); throw new Exception("malformed transcript accepted"); }
            catch (InvalidDataException ex) { Check(!ex.ToString().Contains("server-secret"), "bad JSON sanitized"); }
        }
        using (var engine = new SpeechApiRecognition(new StaticHandler(HttpStatusCode.OK, "{\"text\":\"\"}")))
        {
            await engine.StartAsync(settings, CancellationToken.None);
            Check(await engine.TranscribeAsync(wave, CancellationToken.None) == "", "silence can return empty transcript");
        }
        using (var engine = new SpeechApiRecognition(new OversizedHandler()))
        {
            await engine.StartAsync(settings, CancellationToken.None);
            try { await engine.TranscribeAsync(wave, CancellationToken.None); throw new Exception("oversized response accepted"); } catch (InvalidDataException) { }
        }
        results.Add("PASS: malformed/missing transcript is rejected safely, empty transcript is allowed, and unknown-length responses are bounded");

        var cancelling = new WaitingHandler();
        using (var engine = new SpeechApiRecognition(cancelling))
        using (var stop = new CancellationTokenSource())
        {
            await engine.StartAsync(settings, CancellationToken.None);
            Task<string> pending = engine.TranscribeAsync(wave, stop.Token);
            await cancelling.Started.Task;
            stop.Cancel();
            try { await pending; throw new Exception("speech cancellation ignored"); } catch (OperationCanceledException) { }
            Check(cancelling.Cancelled, "request token cancels pending recognition");
        }
        using (var engine = new SpeechApiRecognition(new WaitingHandler(), TimeSpan.FromMilliseconds(40)))
        {
            await engine.StartAsync(settings, CancellationToken.None);
            try { await engine.TranscribeAsync(wave, CancellationToken.None); throw new Exception("speech deadline ignored"); } catch (TimeoutException) { }
        }
        using (var engine = new SpeechApiRecognition(new SlowBodyHandler(), TimeSpan.FromMilliseconds(40)))
        {
            await engine.StartAsync(settings, CancellationToken.None);
            try { await engine.TranscribeAsync(wave, CancellationToken.None); throw new Exception("speech body deadline ignored"); } catch (TimeoutException) { }
        }
        results.Add("PASS: stop cancels pending speech recognition; header/body deadlines prevent stalled API requests");
        return results;
    }

    private sealed class CaptureHandler(AppSettings expected, byte[] wave) : HttpMessageHandler
    {
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Check(request.Method == HttpMethod.Post && request.RequestUri == SpeechApiRecognition.Endpoint(expected.SpeechApiBase), "speech POST transcriptions endpoint");
            Check(request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == "speech-only-fixture-secret", "speech-only Bearer key");
            Check(!request.Headers.Any(x => x.Key.Equals("x-api-key", StringComparison.OrdinalIgnoreCase)), "no text-provider-specific authentication");
            Check(request.Content is MultipartFormDataContent, "speech multipart content");
            var body = (MultipartFormDataContent)request.Content!;
            var parts = body.ToDictionary(x => x.Headers.ContentDisposition!.Name!.Trim('"'));
            bool silicon = request.RequestUri!.Host == "api.siliconflow.cn";
            Check(parts.Count == (silicon ? 2 : 4) && !parts.ContainsKey("translate") && !parts.ContainsKey("prompt"), "transcription only, supported provider fields, no translation request or unrelated text context");
            Check(parts["file"].Headers.ContentDisposition!.FileName!.Trim('"') == "live.wav" && parts["file"].Headers.ContentType?.MediaType == "audio/wav", "WAV filename/media metadata");
            Check((await parts["file"].ReadAsByteArrayAsync(token)).SequenceEqual(wave), "WAV audio preserved");
            Check(await parts["model"].ReadAsStringAsync(token) == expected.SpeechApiModel, "selected model preserved");
            if (!silicon)
            {
                string languageField = expected.SpeechApiModel == "gpt-transcribe" ? "languages[]" : "language";
                Check(parts.ContainsKey(languageField) && await parts[languageField].ReadAsStringAsync(token) == "ja" && !parts.ContainsKey(languageField == "language" ? "languages[]" : "language"), "correct Japanese language field by model");
                Check(await parts["response_format"].ReadAsStringAsync(token) == "json", "JSON transcript format");
            }
            string serialized = Encoding.UTF8.GetString(await body.ReadAsByteArrayAsync(token));
            Check(serialized.Contains("RIFF") && serialized.Contains("live.wav") && serialized.Contains(expected.SpeechApiModel), "serialized multipart contains actual audio/model");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"text\":\" こんにちは。 \"}", Encoding.UTF8, "application/json") };
        }
    }

    private sealed class StaticHandler(HttpStatusCode code, string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(content) });
    }
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => throw new HttpRequestException("raw-private-url request secret");
    }
    private sealed class WaitingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); throw new Exception("waiting handler completed unexpectedly"); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
        }
    }
    private sealed class OversizedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekableMemoryStream(new byte[1024 * 1024 + 1])) });
    }
    private sealed class NonSeekableMemoryStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    }
    private sealed class SlowBodyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new SlowBodyStream()) });
    }
    private sealed class SlowBodyStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) { await Task.Delay(Timeout.Infinite, token); return 0; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
    }
}
