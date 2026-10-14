using System.Net;
using System.Text;
using System.Text.Json;

namespace Kotoshiro;

internal static class TranslationContextTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: translation context: " + message);
    }

    internal static async Task<List<string>> RunAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        var results = new List<string>();
        VerifySettings(folder, results);

        foreach (string protocol in new[] { "chat", "responses", "messages" })
        {
            foreach (int limit in new[] { 0, 1, 3, 6, 30 })
            {
                var settings = Settings(protocol, limit);
                var handler = new CaptureHandler(protocol);
                using var translator = new ApiTranslator(settings, handler);
                var expected = new List<Pair>();
                for (int index = 0; index < limit + 4; index++)
                {
                    string current = "日文 " + index;
                    string translated = await translator.TranslateAsync(current, CancellationToken.None);
                    VerifyRequest(handler.Requests[^1], expected.TakeLast(limit), current, protocol + " / " + limit + " / " + index);
                    Check(translated == "译文 " + (index + 1), protocol + " parsed response");
                    expected.Add(new(current, translated));
                }
                Check(handler.Requests[^1].Length == limit * 2 + 1, protocol + " exact full rolling window");
                results.Add($"PASS: {protocol} translation payload preserves exactly the newest {limit} successful source/translation pairs, in order, plus the current source");
            }
            await VerifyFailuresAsync(protocol);
            results.Add($"PASS: {protocol} context excludes empty responses, failed HTTP requests and remember=false probes; probes still use prior context; a new translator starts empty");
        }

        await VerifySubscriptionAsync(Path.Combine(folder, "subscription"));
        results.Add("PASS: isolated ChatGPT subscription fixture sends the newest 6 successful pairs through streamed Responses with an OAuth bearer; no real login or inference request");
        return results;
    }

    private static AppSettings Settings(string protocol, int limit)
    {
        var settings = new AppSettings
        {
            ApiProvider = "custom", ApiProtocol = protocol,
            ApiBase = "https://context.example.test/v1", ApiModel = "fixture-context-model",
            TranslationContextSegments = limit
        };
        settings.SetKey("fixture-context-key");
        return settings;
    }

    private static void VerifySettings(string folder, List<string> results)
    {
        Check(new AppSettings().TranslationContextSegments == 3, "new settings preserve default of 3");
        var legacy = JsonSerializer.Deserialize<AppSettings>("{\"ApiBase\":\"https://api.deepseek.com\",\"ApiModel\":\"deepseek-flash\"}")!;
        Check(legacy.TranslationContextSegments == 3 && legacy.NormalizedTranslationContextSegments == 3, "legacy settings with absent field preserve 3");
        foreach (var (value, expected) in new[] { (-20, 0), (0, 0), (1, 1), (3, 3), (6, 6), (30, 30), (31, 30), (int.MaxValue, 30) })
            Check(new AppSettings { TranslationContextSegments = value }.NormalizedTranslationContextSegments == expected, "safe normalization for " + value);
        results.Add("PASS: translation context defaults to 3 for fresh and legacy settings; effective value clamps to 0..30");

        string oldRoot = SettingsStore.Root, oldModelDirectory = SettingsStore.ModelDirectory;
        try
        {
            SettingsStore.Root = Path.Combine(folder, "settings");
            foreach (int value in new[] { 0, 6, 30 })
            {
                var settings = Settings("chat", value);
                var clone = settings.Clone();
                Check(clone.TranslationContextSegments == value && clone.NormalizedTranslationContextSegments == value, "clone preserves custom value");
                clone.TranslationContextSegments = value == 0 ? 1 : 0;
                Check(settings.TranslationContextSegments == value, "clone remains independent");
                SettingsStore.Save(settings);
                var loaded = SettingsStore.Load();
                Check(loaded.TranslationContextSegments == value && loaded.NormalizedTranslationContextSegments == value, "saved value reloads");
            }
            File.WriteAllText(Path.Combine(SettingsStore.Root, "settings.json"), "{\"ApiBase\":\"https://api.deepseek.com\",\"ApiModel\":\"deepseek-flash\"}");
            Check(SettingsStore.Load().TranslationContextSegments == 3, "legacy file reload defaults to 3");
        }
        finally
        {
            SettingsStore.Root = oldRoot;
            SettingsStore.SetModelDirectory(oldModelDirectory);
        }
        results.Add("PASS: user-selected context values 0, 6 and 30 survive independent clone and isolated settings save/load; legacy file loads 3 without modifying real user settings");
    }

    private static async Task VerifyFailuresAsync(string protocol)
    {
        var settings = Settings(protocol, 6);
        var handler = new CaptureHandler(protocol);
        var expected = new List<Pair>();
        using (var translator = new ApiTranslator(settings, handler))
        {
            async Task Success(string source, bool remember = true)
            {
                string result = await translator.TranslateAsync(source, CancellationToken.None, remember);
                VerifyRequest(handler.Requests[^1], expected, source, protocol + " successful/probe request");
                Check(result.Length > 0, "successful fixture text");
                if (remember) expected.Add(new(source, result));
            }
            await Success("成功 一");
            await Success("成功 二");

            handler.Next = Reply.Empty;
            try
            {
                string empty = await translator.TranslateAsync("空译文", CancellationToken.None);
                Check(string.IsNullOrWhiteSpace(empty), "empty fixture may only return empty text");
            }
            catch (InvalidDataException) { /* Text-block protocols may reject an empty body. */ }
            VerifyRequest(handler.Requests[^1], expected, "空译文", protocol + " empty-response request");

            handler.Next = Reply.HttpFailure;
            try
            {
                await translator.TranslateAsync("HTTP 失败", CancellationToken.None);
                throw new Exception("FAIL: translation context: failed HTTP unexpectedly succeeded");
            }
            catch (HttpRequestException ex) { Check(ex.StatusCode == HttpStatusCode.ServiceUnavailable, "expected failed HTTP"); }
            VerifyRequest(handler.Requests[^1], expected, "HTTP 失败", protocol + " failed-HTTP request");

            await Success("临时测试 不记住", remember: false);
            await Success("成功 三");
            Check(handler.Requests[^1].Length == 5, "only two successes precede third success");
        }

        var freshHandler = new CaptureHandler(protocol);
        using var fresh = new ApiTranslator(settings, freshHandler);
        await fresh.TranslateAsync("新会话", CancellationToken.None);
        VerifyRequest(freshHandler.Requests.Single(), [], "新会话", protocol + " new-translator request");
    }

    private static async Task VerifySubscriptionAsync(string folder)
    {
        using var auth = new ChatGptAuth(folder, new RejectNetworkHandler());
        var registration = new ChatGptRegistration
        {
            ClientId = "fixture-context-client", Subject = "fixture-context-subject",
            AccessToken = "fixture-context-oauth", RefreshToken = "fixture-context-refresh",
            Scopes = ChatGptAuth.Scope.Split(' '), ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };
        await auth.SeedForTestAsync(registration);
        var settings = new AppSettings
        {
            ApiProvider = "chatgpt", ApiBase = "https://ignored.example.test", ApiProtocol = "messages",
            ChatGptAccountId = registration.Id, ChatGptModel = "fixture-context-model",
            TranslationContextSegments = 6
        };
        var handler = new CaptureHandler("responses", subscription: true);
        using var translator = new ApiTranslator(settings, handler, auth);
        var expected = new List<Pair>();
        for (int index = 0; index < 10; index++)
        {
            string current = "订阅 日文 " + index;
            string translated = await translator.TranslateAsync(current, CancellationToken.None);
            VerifyRequest(handler.Requests[^1], expected.TakeLast(6), current, "subscription / " + index);
            Check(translated == "译文 " + (index + 1), "completed subscription stream text");
            expected.Add(new(current, translated));
        }
        Check(handler.Requests[^1].Length == 13, "subscription supports 6 context pairs rather than old hardcoded 3");
    }

    private static void VerifyRequest(Message[] actual, IEnumerable<Pair> context, string current, string label)
    {
        var expected = context.SelectMany(pair => new[] { new Message("user", pair.Source), new Message("assistant", pair.Translation) })
            .Append(new Message("user", current)).ToArray();
        Check(actual.SequenceEqual(expected), label + " has exact ordered context and current user text");
    }

    private sealed record Pair(string Source, string Translation);
    private sealed record Message(string Role, string Content);
    private enum Reply { Success, Empty, HttpFailure }

    private sealed class CaptureHandler(string protocol, bool subscription = false) : HttpMessageHandler
    {
        internal List<Message[]> Requests { get; } = [];
        internal Reply Next { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var root = document.RootElement;
            Check(root.GetProperty("model").GetString() == "fixture-context-model", "selected model preserved");
            Check(root.GetProperty("stream").GetBoolean() == subscription, "stream mode preserved");
            if (subscription)
                Check(request.RequestUri!.AbsoluteUri == ChatGptAuth.Resource + "/responses" && request.Headers.Authorization?.Parameter == "fixture-context-oauth", "subscription fixed route and OAuth auth");
            else
            {
                Check(request.RequestUri == ApiTranslator.Endpoint("https://context.example.test/v1", protocol), "generic endpoint");
                if (protocol == "messages")
                    Check(request.Headers.GetValues("x-api-key").Single() == "fixture-context-key", "Messages auth");
                else
                    Check(request.Headers.Authorization?.Parameter == "fixture-context-key", "generic Bearer auth");
            }
            var raw = root.GetProperty(protocol == "responses" ? "input" : "messages").EnumerateArray().ToArray();
            if (protocol == "chat")
            {
                Check(raw.Length > 0 && raw[0].GetProperty("role").GetString() == "system", "Chat instruction separate from history");
                raw = raw.Skip(1).ToArray();
            }
            else
                Check(!string.IsNullOrWhiteSpace(root.GetProperty(protocol == "responses" ? "instructions" : "system").GetString()), "separate instruction present");
            Requests.Add(raw.Select(item => new Message(item.GetProperty("role").GetString()!, item.GetProperty("content").GetString()!)).ToArray());

            var reply = Next; Next = Reply.Success;
            if (reply == Reply.HttpFailure) return Response(HttpStatusCode.ServiceUnavailable, "{\"error\":\"fixture unavailable\"}");
            string text = reply == Reply.Empty ? "   " : "译文 " + Requests.Count;
            string body = protocol switch
            {
                "chat" => JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = text } } } }),
                "messages" => JsonSerializer.Serialize(new { stop_reason = "end_turn", content = new[] { new { type = "text", text } } }),
                _ => JsonSerializer.Serialize(new { status = "completed", output = new[] { new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text } } } } })
            };
            if (!subscription) return Response(HttpStatusCode.OK, body);
            string completed = JsonSerializer.Serialize(new { type = "response.completed", response = JsonSerializer.Deserialize<JsonElement>(body) });
            return new(HttpStatusCode.OK) { Content = new StringContent("data: " + completed + "\n\n", Encoding.UTF8, "text/event-stream") };
        }
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class RejectNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            throw new Exception("FAIL: translation context: seeded subscription auth unexpectedly attempted a network request");
    }
}
