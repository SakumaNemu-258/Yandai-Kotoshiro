using System.Net;
using System.Text;

namespace Kotoshiro;

internal sealed class ApiPlayerTestSpeech : HttpMessageHandler
{
    private readonly int initialFailures;
    private readonly HttpStatusCode failureStatus;
    public ApiPlayerTestSpeech(int initialFailures = 0, HttpStatusCode failureStatus = HttpStatusCode.TooManyRequests) { this.initialFailures = initialFailures; this.failureStatus = failureStatus; }
    public int Calls { get; private set; }
    public long AudioBytes { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (request.Method != HttpMethod.Post || request.RequestUri?.AbsolutePath != "/v1/audio/transcriptions" || request.Headers.Authorization?.Parameter != "speech-test-only")
            throw new InvalidOperationException("API recognition pipeline used the wrong endpoint or credential.");
        if (request.Content is not MultipartFormDataContent multipart) throw new InvalidOperationException("API recognition did not upload audio multipart.");
        var audio = multipart.Single(x => x.Headers.ContentDisposition?.Name?.Trim('"') == "file");
        byte[] bytes = await audio.ReadAsByteArrayAsync(token);
        if (bytes.Length <= 44 || Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" || Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
            throw new InvalidOperationException("Real video decode did not produce a WAV upload.");
        Calls++; AudioBytes += bytes.Length; await Task.Delay(150, token);
        if (Calls <= initialFailures) return new HttpResponseMessage(failureStatus) { Content = new StringContent("simulated failure body must not be shown") };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"text\":\"今日は一緒にゲームをやっていきましょう。\"}", Encoding.UTF8, "application/json")
        };
    }
}
