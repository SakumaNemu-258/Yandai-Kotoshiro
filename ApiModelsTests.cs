using System.Net;
using System.Text;

namespace Kotoshiro;

internal static class ApiModelsTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: API models " + message); }
    internal static async Task<string[]> RunAsync()
    {
        Check(ApiModels.Defaults("deepseek").Select(x => x.Id).SequenceEqual(new[] { "deepseek-flash", "deepseek-v4-pro" }), "current DeepSeek choices");
        Check(ApiModels.Endpoint("https://api.deepseek.com/").AbsoluteUri == "https://api.deepseek.com/models", "base endpoint");
        Check(ApiModels.Endpoint("https://compatible.example/v1/chat/completions").AbsoluteUri == "https://compatible.example/v1/models", "complete chat endpoint");
        Check(ApiModels.Endpoint("https://compatible.example/v1/responses", "responses").AbsoluteUri == "https://compatible.example/v1/models", "complete Responses endpoint");
        Check(ApiModels.Endpoint("https://compatible.example/v1/messages", "messages").AbsoluteUri == "https://compatible.example/v1/models", "complete Messages endpoint");
        foreach (string address in new[] { "http://insecure.example", "https://user:password@compatible.example", "https://compatible.example/v1?key=unsafe" })
        {
            try { ApiModels.Endpoint(address); throw new Exception("insecure endpoint accepted"); } catch (ArgumentException) { }
        }

        var settings = new AppSettings { ApiProvider = "custom", ApiBase = "https://compatible.example/v1/chat/completions", ApiProtocol = "chat", ApiModel = "my-custom-model" };
        settings.SetKey("model-list-fixture-secret");
        var compatible = new Handler(async (request, token) =>
        {
            Check(request.Method == HttpMethod.Get && request.Content == null, "metadata-only GET");
            Check(request.RequestUri!.AbsoluteUri == "https://compatible.example/v1/models", "explicit custom endpoint retained");
            Check(request.Headers.Authorization?.Parameter == "model-list-fixture-secret", "Bearer auth");
            await Task.Yield();
            return Json("""
                {"data":[{"id":"z-model","name":"Friendly Z"},{"id":"a-model"},{"id":"z-model"},{"id":"bad\nvalue"},{"no_id":"ignored"}]}
                """);
        });
        var models = await ApiModels.FetchAsync(settings, CancellationToken.None, compatible);
        Check(models.Select(x => x.Id).SequenceEqual(new[] { "a-model", "z-model" }) && models[1].Name == "Friendly Z", "sorting, deduplication, valid IDs and display metadata");
        Check(settings.ApiModel == "my-custom-model" && settings.GetKey() == "model-list-fixture-secret", "caller model and credentials preserved");

        var claude = settings.Clone(); claude.ApiProvider = "claude"; claude.ApiBase = "https://api.anthropic.com/v1"; claude.ApiProtocol = "messages";
        int pages = 0;
        var paged = new Handler((request, token) =>
        {
            Check(request.Headers.Authorization == null && request.Headers.GetValues("x-api-key").Single() == "model-list-fixture-secret" && request.Headers.GetValues("anthropic-version").Single() == "2023-06-01", "Messages auth/version");
            Check(request.RequestUri!.AbsoluteUri == "https://api.anthropic.com/v1/models?limit=100" + (pages == 0 ? "" : "&after_id=claude-first"), "pagination endpoint");
            pages++;
            return Task.FromResult(Json(pages == 1 ? "{\"data\":[{\"id\":\"claude-first\",\"display_name\":\"Claude First\"}],\"has_more\":true,\"last_id\":\"claude-first\"}" : "{\"data\":[{\"id\":\"claude-second\"}],\"has_more\":false}"));
        });
        var claudeModels = await ApiModels.FetchAsync(claude, CancellationToken.None, paged);
        Check(pages == 2 && claudeModels.Count == 2 && claudeModels[0].Name == "Claude First", "Anthropic pagination and display names");

        var error = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("model-list-fixture-secret") }));
        try { await ApiModels.FetchAsync(settings, CancellationToken.None, error); throw new Exception("error accepted"); }
        catch (HttpRequestException ex) { Check(ex.StatusCode == HttpStatusCode.Unauthorized && !ex.ToString().Contains("model-list-fixture-secret"), "safe HTTP error"); }
        var invalid = new Handler((_, _) => Task.FromResult(Json("model-list-fixture-secret")));
        try { await ApiModels.FetchAsync(settings, CancellationToken.None, invalid); throw new Exception("invalid response accepted"); }
        catch (InvalidDataException ex) { Check(!ex.ToString().Contains("model-list-fixture-secret"), "safe malformed response"); }
        using var cancel = new CancellationTokenSource();
        var cancellable = new Handler(async (_, token) => { cancel.Cancel(); await Task.Delay(1000, token); return Json("{\"data\":[]}"); });
        try { await ApiModels.FetchAsync(settings, cancel.Token, cancellable); throw new Exception("cancellation ignored"); } catch (OperationCanceledException) { }

        return [
            "PASS: DeepSeek Flash/V4 Pro defaults and validated model-list endpoints",
            "PASS: model discovery uses metadata GET, preserves custom model/key, sorts and deduplicates IDs",
            "PASS: Anthropic model discovery auth/version and cursor pagination",
            "PASS: model discovery cancellation and credential-safe HTTP/malformed-response errors"
        ];
    }
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
