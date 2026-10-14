using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Kotoshiro;

public record ApiModelInfo(string Id, string Name)
{
    public override string ToString() => Id;
}

/// <summary>Reads model metadata only; it never starts a billed inference or modifies settings.</summary>
public static class ApiModels
{
    private const int MaximumPageBytes = 2 * 1024 * 1024;
    private const int MaximumModels = 4096;
    private const int MaximumPages = 20;

    // Official IDs checked against https://api-docs.deepseek.com/api/list-models/.
    // Other providers are discovered from their own endpoint instead of retaining an aging model list.
    public static IReadOnlyList<ApiModelInfo> Defaults(string provider) => provider == "deepseek"
        ? [new("deepseek-flash", "DeepSeek Flash"), new("deepseek-v4-pro", "DeepSeek V4 Pro")]
        : [];

    public static Uri Endpoint(string address, string protocol = "chat")
    {
        Uri inference = ApiTranslator.Endpoint(address, protocol);
        string suffix = protocol switch { "chat" => "/chat/completions", "responses" => "/responses", "messages" => "/messages", _ => throw new ArgumentException("不支持的接口类型。") };
        return new Uri(inference.AbsoluteUri[..^suffix.Length] + "/models");
    }

    public static async Task<IReadOnlyList<ApiModelInfo>> FetchAsync(AppSettings settings, CancellationToken token, HttpMessageHandler? handler = null)
    {
        AppSettings snapshot = settings.Clone();
        ApiProviders.Normalize(snapshot);
        if (snapshot.ApiProvider == "chatgpt") throw new InvalidOperationException("ChatGPT 订阅的模型请在账号区域中选择。");
        Uri endpoint = Endpoint(snapshot.ApiBase, snapshot.ApiProtocol);
        string key = snapshot.GetKey().Trim();
        if (key.Length == 0) throw new InvalidOperationException("请先填写 API 密钥，再获取模型列表。");
        if (key.Contains('\r') || key.Contains('\n')) throw new ArgumentException("API 密钥包含换行，请重新填写。");

        using var client = handler == null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) : new HttpClient(handler);
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var models = new Dictionary<string, ApiModelInfo>(StringComparer.Ordinal);
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = null;
            bool messages = snapshot.ApiProtocol == "messages";
            for (int page = 0; page < MaximumPages; page++)
            {
                Uri pageEndpoint = messages ? new Uri(endpoint.AbsoluteUri + "?limit=100" + (cursor == null ? "" : "&after_id=" + Uri.EscapeDataString(cursor))) : endpoint;
                using var request = new HttpRequestMessage(HttpMethod.Get, pageEndpoint);
                if (messages)
                {
                    request.Headers.Add("x-api-key", key);
                    request.Headers.Add("anthropic-version", "2023-06-01");
                }
                else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if (!response.IsSuccessStatusCode) throw ModelError(response.StatusCode);
                if (response.Content.Headers.ContentLength > MaximumPageBytes) throw new InvalidDataException("模型列表过大，请手动填写模型名称。");
                await using Stream body = await response.Content.ReadAsStreamAsync(deadline.Token);
                using var buffer = new MemoryStream();
                byte[] chunk = new byte[8192];
                int read;
                while ((read = await body.ReadAsync(chunk, deadline.Token)) > 0)
                {
                    if (buffer.Length + read > MaximumPageBytes) throw new InvalidDataException("模型列表过大，请手动填写模型名称。");
                    buffer.Write(chunk, 0, read);
                }
                using var doc = JsonDocument.Parse(buffer.ToArray());
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("此 API 没有返回标准模型列表，可继续手动填写模型名称。");
                foreach (JsonElement item in data.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("id", out var idField) || idField.ValueKind != JsonValueKind.String) continue;
                    string id = idField.GetString() ?? "";
                    if (!SafeIdentifier(id)) continue;
                    string name = ReadName(item, id);
                    models.TryAdd(id, new(id, name));
                    if (models.Count > MaximumModels) throw new InvalidDataException("模型列表过大，请手动填写模型名称。");
                }
                bool hasMore = root.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True;
                if (!hasMore)
                {
                    if (models.Count == 0) throw new InvalidDataException("此账号没有返回可用模型，可继续手动填写模型名称。");
                    return models.Values.OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase).ToArray();
                }
                if (!messages || !root.TryGetProperty("last_id", out var last) || last.ValueKind != JsonValueKind.String || !SafeIdentifier(last.GetString() ?? "") || !cursors.Add(last.GetString()!))
                    throw new InvalidDataException("模型列表分页格式不受支持，可继续手动填写模型名称。");
                cursor = last.GetString();
            }
            throw new InvalidDataException("模型列表页数过多，请手动填写模型名称。");
        }
        catch (JsonException) { throw new InvalidDataException("此 API 的模型列表格式无法识别，可继续手动填写模型名称。"); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException("获取模型列表超时，可稍后重试或手动填写模型名称。"); }
    }

    private static bool SafeIdentifier(string value) => value.Length is > 0 and <= 256 && !value.Any(char.IsControl) && value == value.Trim();
    private static string ReadName(JsonElement item, string fallback)
    {
        foreach (string property in new[] { "display_name", "name" })
            if (item.TryGetProperty(property, out var name) && name.ValueKind == JsonValueKind.String && SafeIdentifier(name.GetString() ?? "")) return name.GetString()!;
        return fallback;
    }
    private static HttpRequestException ModelError(HttpStatusCode status)
    {
        string message = status switch
        {
            HttpStatusCode.Unauthorized => "API 密钥无效，无法获取模型列表。",
            HttpStatusCode.Forbidden => "API 拒绝访问模型列表，请检查账号权限。",
            HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed => "此 API 不提供模型列表，请手动填写模型名称。",
            HttpStatusCode.TooManyRequests => "获取模型列表过于频繁，请稍后重试。",
            _ => $"获取模型列表失败（HTTP {(int)status}），可继续手动填写模型名称。"
        };
        // Deliberately omit arbitrary error bodies, which can contain credentials.
        return new HttpRequestException(message, null, status);
    }
}
