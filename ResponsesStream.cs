using System.Text;
using System.Text.Json;

namespace Kotoshiro;

internal static class ResponsesStream
{
    internal static async Task<string> ReadAsync(Stream stream, CancellationToken token)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
        var data = new StringBuilder(); var deltas = new StringBuilder();
        while (true)
        {
            string? line = await reader.ReadLineAsync(token);
            if (line == null) throw new ChatGptPlanException("stream_interrupted");
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line[5..].TrimStart(' '));
                if (data.Length > 2_000_000) throw new InvalidDataException("ChatGPT 返回的事件过大。");
            }
            if (line.Length != 0 || data.Length == 0) continue;
            string value = data.ToString(); data.Clear();
            if (value == "[DONE]") throw new ChatGptPlanException("stream_interrupted");
            using var doc = JsonDocument.Parse(value); var root = doc.RootElement;
            string type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            if (type == "response.output_text.delta")
            {
                deltas.Append(root.GetProperty("delta").GetString());
                if (deltas.Length > 16000) throw new InvalidDataException("ChatGPT 返回的译文过长。");
            }
            else if (type == "response.failed") throw ChatGptPlanException.FromJson(root.GetProperty("response"));
            else if (type == "error") throw ChatGptPlanException.FromJson(root.TryGetProperty("error", out _) ? root : JsonSerializer.SerializeToElement(new { error = root }));
            else if (type == "response.incomplete") throw new ChatGptPlanException("response_incomplete");
            else if (type == "response.completed")
            {
                var response = root.GetProperty("response");
                if (!response.TryGetProperty("status", out var status) || status.GetString() != "completed") throw new ChatGptPlanException("response_incomplete");
                if (!response.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array) throw new InvalidDataException("ChatGPT 未返回最终译文。");
                var texts = output.EnumerateArray().Where(x => x.TryGetProperty("type", out var mt) && mt.GetString() == "message" && x.TryGetProperty("role", out var role) && role.GetString() == "assistant")
                    .Where(x => x.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array)
                    .SelectMany(x => x.GetProperty("content").EnumerateArray()).Where(x => x.TryGetProperty("type", out var bt) && bt.GetString() == "output_text")
                    .Select(x => x.GetProperty("text").GetString());
                return string.Join("", texts).Trim();
            }
        }
    }
}
