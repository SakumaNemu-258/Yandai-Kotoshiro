using System.Text;

namespace Kotoshiro;

internal static class HistoryFilesTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: history TXT " + message); }

    internal static async Task<string[]> RunAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        const string snapshot = "12:03:04  配信を始めます。\n开始直播。\n\n12:03:08  𠮷さん、ありがとう！\r\n谢谢吉先生！ 🎉\r\n\r\n12:03:09  次の行\r下一行\n\n";
        const string expected = "12:03:04  配信を始めます。\r\n开始直播。\r\n\r\n12:03:08  𠮷さん、ありがとう！\r\n谢谢吉先生！ 🎉\r\n\r\n12:03:09  次の行\r\n下一行\r\n\r\n";
        string path = Path.Combine(folder, "言代-翻译记录.txt");
        await HistoryFiles.WriteTextAsync(path, snapshot);
        byte[] saved = await File.ReadAllBytesAsync(path);
        Check(saved.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }), "UTF-8 BOM is present for Windows text editors");
        Check(new UTF8Encoding(false, true).GetString(saved, 3, saved.Length - 3) == expected, "only captured timestamps, original and translated text are exported in order with CRLF and Unicode intact");

        string liveText = snapshot;
        string captured = liveText;
        Task exporting = HistoryFiles.WriteTextAsync(path, captured);
        liveText += "12:04:00  後から来た字幕\n导出后到来的字幕\n\n";
        await exporting;
        Check(await File.ReadAllTextAsync(path) == expected && liveText.Contains("导出后到来的字幕"), "export uses its snapshot while subsequent translation records remain available");

        string empty = Path.Combine(folder, "empty.txt");
        await HistoryFiles.WriteTextAsync(empty, "");
        Check((await File.ReadAllBytesAsync(empty)).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }), "empty snapshot adds no metadata, settings, account data or log content");
        try { await HistoryFiles.WriteTextAsync(path, "损坏的文字\ud800"); throw new Exception("invalid Unicode accepted"); }
        catch (EncoderFallbackException) { }
        Check((await File.ReadAllBytesAsync(path)).SequenceEqual(saved), "invalid text preserves the existing export");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try { await HistoryFiles.WriteTextAsync(path, "不能替换正在使用的文件"); throw new Exception("locked export replaced"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        Check((await File.ReadAllBytesAsync(path)).SequenceEqual(saved), "failed replacement preserves the existing export");
        Check(Directory.GetFiles(folder, ".kotoshiro-history-*.tmp").Length == 0, "temporary files are cleaned after a failed save");
        await HistoryFiles.WriteTextAsync(path, "新しい字幕\n新的字幕\n");
        Check(await File.ReadAllTextAsync(path) == "新しい字幕\r\n新的字幕\r\n", "explicit overwrite succeeds");
        return [
            "PASS: history TXT writes only the captured timestamps, Japanese original and Chinese translation in retained order, using UTF-8 BOM and Windows line breaks with non-BMP Unicode intact",
            "PASS: history export snapshot is unaffected by later appended captions; empty snapshots contain no unrelated metadata, settings, account information or internal logs",
            "PASS: failed or invalid history TXT saves preserve existing exports and clean temporary files; explicit overwrite succeeds"
        ];
    }
}
