using System.Text;

namespace Kotoshiro;

/// <summary>Plain-text glossary exchange. No settings, credentials or account data are exported.</summary>
public static class GlossaryFiles
{
    public const int MaximumBytes = 1024 * 1024;
    public const string DefaultExample = "ひのめ → 火羽\r\n配信 → 直播\r\nリスナー → 观众\r\n切り抜き → 直播切片\r\nアーカイブ → 直播回放";
    private const string PreviousDefaultExample = "配信 → 直播\r\nリスナー → 观众\r\n切り抜き → 直播切片\r\nアーカイブ → 直播回放";

    public static string UpgradeDefault(string? text) =>
        text is null ? "" : text.Replace("\r\n", "\n").Replace('\r', '\n').Trim() == PreviousDefaultExample.Replace("\r\n", "\n") ? DefaultExample : text;

    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly UnicodeEncoding Utf16LittleEndian = new(false, false, true);
    private static readonly UnicodeEncoding Utf16BigEndian = new(true, false, true);

    public static string ReadText(string path)
    {
        // Holding a read-only stream with no write sharing keeps the size check and read together.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > MaximumBytes) throw new InvalidDataException("术语表 TXT 不能超过 1 MiB。请分成较小的文件后导入。");
        byte[] data = new byte[checked((int)file.Length)];
        file.ReadExactly(data);
        if (file.ReadByte() != -1) throw new InvalidDataException("术语表文件在读取时发生变化，请重试。");

        string text;
        try
        {
            if (data.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 }) || data.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff }))
                throw new InvalidDataException("暂不支持 UTF-32 TXT，请另存为 UTF-8 后导入。");
            if (data.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) text = Utf8.GetString(data, 3, data.Length - 3);
            else if (data.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) text = Utf16LittleEndian.GetString(data, 2, data.Length - 2);
            else if (data.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) text = Utf16BigEndian.GetString(data, 2, data.Length - 2);
            else
            {
                try { text = Utf8.GetString(data); }
                catch (DecoderFallbackException)
                {
                    // Older Chinese Windows editors may save ANSI TXT as GB18030/GBK.
                    // This decoder is strict; malformed byte sequences never become replacement characters.
                    var legacy = CodePagesEncodingProvider.Instance.GetEncoding(54936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
                        ?? throw new InvalidDataException("无法读取此 TXT 编码，请另存为 UTF-8 后导入。");
                    text = legacy.GetString(data);
                }
            }
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException("TXT 编码损坏或不受支持，请另存为 UTF-8 后导入。");
        }
        return NormalizeText(text);
    }

    public static void WriteText(string path, string text)
    {
        // Prepare and validate the complete payload before creating or replacing any file.
        byte[] body = Utf8.GetBytes(NormalizeText(text));
        if (body.Length + 3 > MaximumBytes) throw new InvalidDataException("术语表 TXT 不能超过 1 MiB。请分成较小的文件后导出。");
        string target = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(target)!;
        string temporary = Path.Combine(directory, ".kotoshiro-glossary-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(new byte[] { 0xef, 0xbb, 0xbf });
                file.Write(body);
                file.Flush(true);
            }
            File.Move(temporary, target, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>Keep existing entries first. Conflicting translations remain visible for manual review.</summary>
    public static string MergeImported(string existing, string incoming)
    {
        string first = NormalizeText(existing), second = NormalizeText(incoming);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lines = new List<string>();
        foreach (string line in (first + "\n" + second).Replace("\r\n", "\n").Split('\n'))
        {
            string key = line.Trim();
            if (key.Length > 0 && seen.Add(key)) lines.Add(line);
        }
        return string.Join("\r\n", lines).TrimEnd();
    }

    public static string MergeExamples(string text) => MergeImported(text, DefaultExample);

    private static string NormalizeText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        for (int i = 0; i < text.Length; i++)
        {
            char value = text[i];
            if ((char.IsControl(value) && value is not ('\t' or '\r' or '\n')) || value == '\ufffd')
                throw new InvalidDataException("术语表必须是纯文本 TXT，不能包含二进制内容、控制字符或损坏的文字。");
            if (char.IsHighSurrogate(value))
            {
                if (++i >= text.Length || !char.IsLowSurrogate(text[i]))
                    throw new InvalidDataException("术语表包含损坏的 Unicode 文字，请修正后重试。");
            }
            else if (char.IsLowSurrogate(value))
                throw new InvalidDataException("术语表包含损坏的 Unicode 文字，请修正后重试。");
        }
        return text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n").TrimEnd();
    }
}
