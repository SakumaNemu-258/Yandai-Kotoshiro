namespace Kotoshiro;

public record ApiProfile(string Address, string Model, string EncryptedKey, string Protocol);
public record ApiProviderInfo(string Id, string Name, string Address, string Model, string Protocol)
{
    public override string ToString() => Name;
}
public record ApiProtocolInfo(string Id, string Name)
{
    public override string ToString() => Name;
}
public static class ApiProviders
{
    public static readonly ApiProviderInfo[] All =
    [
        new("deepseek", "DeepSeek", "https://api.deepseek.com", "deepseek-flash", "chat"),
        new("chatgpt", "ChatGPT 订阅 · 账号登录", "https://api.openai.com/v1", "", "responses"),
        new("openai", "OpenAI", "https://api.openai.com/v1", "", "responses"),
        new("gemini", "Google Gemini", "https://generativelanguage.googleapis.com/v1beta/openai", "", "chat"),
        new("claude", "Anthropic Claude", "https://api.anthropic.com/v1", "", "messages"),
        new("custom", "自定义 API", "", "", "chat")
    ];
    public static readonly ApiProtocolInfo[] Protocols =
    [new("chat", "OpenAI 兼容 / Chat Completions"), new("responses", "OpenAI Responses"), new("messages", "Anthropic Messages")];
    public static ApiProviderInfo Get(string id) => All.FirstOrDefault(x => x.Id == id) ?? All[^1];
    public static void Normalize(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiProvider))
        {
            string host = Uri.TryCreate(settings.ApiBase, UriKind.Absolute, out var uri) ? uri.Host : "";
            settings.ApiProvider = host switch { "api.deepseek.com" => "deepseek", "api.openai.com" => "openai", "generativelanguage.googleapis.com" => "gemini", "api.anthropic.com" => "claude", _ => "custom" };
            // Existing releases always used Chat Completions. Preserve their wire format.
            if (string.IsNullOrWhiteSpace(settings.ApiProtocol)) settings.ApiProtocol = "chat";
        }
        if (!Protocols.Any(x => x.Id == settings.ApiProtocol)) settings.ApiProtocol = Get(settings.ApiProvider).Protocol;
        settings.ApiProfiles ??= [];
    }
}
