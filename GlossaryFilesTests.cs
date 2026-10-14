using System.Text;

namespace Kotoshiro;

internal static class GlossaryFilesTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: glossary TXT " + message); }

    internal static async Task<string[]> RunAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        const string glossary = "配信 → 直播\r\n\tリスナー  →  观众（保留注释）\r\n切り抜き → 直播切片\r\n𠮷 → 吉";
        string file = Path.Combine(folder, "share.txt");
        GlossaryFiles.WriteText(file, glossary + "\r\n\r\n");
        byte[] exported = await File.ReadAllBytesAsync(file);
        Check(exported.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }), "exports UTF-8 BOM");
        Check(GlossaryFiles.ReadText(file) == glossary, "Japanese/Chinese/non-BMP roundtrip preserves spacing and notes");
        Check(new UTF8Encoding(false, true).GetString(exported, 3, exported.Length - 3) == glossary, "exports only glossary text");

        foreach (var fixture in new (string Name, Encoding Encoding)[]
        {
            ("utf8", new UTF8Encoding(false, true)), ("utf8-bom", new UTF8Encoding(true, true)),
            ("utf16-le", new UnicodeEncoding(false, true, true)), ("utf16-be", new UnicodeEncoding(true, true, true)),
            ("gb18030", CodePagesEncodingProvider.Instance.GetEncoding(54936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)!)
        })
        {
            string path = Path.Combine(folder, fixture.Name + ".txt");
            string mixed = glossary.Replace("\r\n", "\n") + "\r\n\r";
            await File.WriteAllBytesAsync(path, fixture.Encoding.GetPreamble().Concat(fixture.Encoding.GetBytes(mixed)).ToArray());
            Check(GlossaryFiles.ReadText(path) == glossary, fixture.Name + " strict decode and newline normalization");
        }

        const string existing = "配信 → 直播\r\n手动术语 → 自定义译法  （注释）\r\n同じ → 原有译法";
        const string incoming = "配信 → 直播\n同じ → 分享译法\n新語 → 新词\n 新語 → 新词 ";
        string merged = GlossaryFiles.MergeImported(existing, incoming);
        Check(merged == existing + "\r\n同じ → 分享译法\r\n新語 → 新词", "full-line dedup, original order, spacing, conflicting translations remain");
        Check(GlossaryFiles.MergeImported(merged, incoming) == merged, "import merge is idempotent");
        Check(GlossaryFiles.MergeImported(existing, "\r\n\t\r\n") == existing, "empty import keeps existing entries");
        string examples = GlossaryFiles.MergeExamples(existing);
        Check(examples.StartsWith(existing + "\r\n", StringComparison.Ordinal) && !examples.Contains("配信 → 直播\r\n配信"), "examples preserve custom glossary");
        Check(examples.Split("配信 → 直播").Length == 2 && examples.Contains("アーカイブ → 直播回放"), "examples add missing entries only");
        Check(GlossaryFiles.MergeExamples(examples) == examples, "adding examples is idempotent");
        Check(GlossaryFiles.MergeExamples("") == GlossaryFiles.DefaultExample, "empty glossary gets complete default example");
        Check(GlossaryFiles.DefaultExample.Split("\r\n").SequenceEqual(new[] { "ひのめ → 火羽", "配信 → 直播", "リスナー → 观众", "切り抜き → 直播切片", "アーカイブ → 直播回放" }), "five exact defaults and user-defined name");
        Check(new AppSettings().Glossary == GlossaryFiles.DefaultExample, "new settings use the five defaults");
        const string previousDefaults = "配信 → 直播\nリスナー → 观众\n切り抜き → 直播切片\nアーカイブ → 直播回放";
        Check(GlossaryFiles.UpgradeDefault(previousDefaults) == GlossaryFiles.DefaultExample, "old built-in four-entry glossary upgrades");
        Check(GlossaryFiles.UpgradeDefault(existing) == existing && GlossaryFiles.UpgradeDefault("") == "", "custom and explicitly empty glossaries remain unchanged");
        string originalRoot = SettingsStore.Root, originalModelDirectory = SettingsStore.ModelDirectory;
        try
        {
            SettingsStore.Root = Path.Combine(folder, "default-settings");
            SettingsStore.Save(new AppSettings { Glossary = previousDefaults });
            Check(SettingsStore.Load().Glossary == GlossaryFiles.DefaultExample, "settings load upgrades only previous built-in defaults");
            SettingsStore.Save(new AppSettings { Glossary = existing });
            Check(SettingsStore.Load().Glossary == existing, "settings load preserves custom terms");
            SettingsStore.Save(new AppSettings { Glossary = "" });
            Check(SettingsStore.Load().Glossary == "", "settings load preserves intentional empty glossary");
        }
        finally { SettingsStore.Root = originalRoot; SettingsStore.SetModelDirectory(originalModelDirectory); }

        foreach (var invalid in new (string Name, byte[] Data)[]
        {
            ("oversize", new byte[GlossaryFiles.MaximumBytes + 1]),
            ("binary", new byte[] { 0x50, 0x4b, 3, 4, 0, 7 }),
            ("broken-utf8-bom", new byte[] { 0xef, 0xbb, 0xbf, 0xe3, 0x81 }),
            ("broken-utf16", new byte[] { 0xff, 0xfe, 0x42 }),
            ("unknown-invalid", new byte[] { 0xff }),
            ("utf32", new byte[] { 0xff, 0xfe, 0, 0, 0x42, 0, 0, 0 }),
            ("control", Encoding.UTF8.GetBytes("配信 → 直播\u0007")),
            ("replacement", Encoding.UTF8.GetBytes("配信 → \ufffd"))
        })
        {
            string path = Path.Combine(folder, invalid.Name + ".txt");
            await File.WriteAllBytesAsync(path, invalid.Data);
            try { GlossaryFiles.ReadText(path); throw new Exception("invalid import accepted: " + invalid.Name); }
            catch (InvalidDataException) { }
            Check((await File.ReadAllBytesAsync(path)).SequenceEqual(invalid.Data), invalid.Name + " read leaves source unchanged");
            Check(GlossaryFiles.ReadText(file) == glossary, invalid.Name + " keeps prior exported glossary");
        }
        foreach (string invalidText in new[] { "bad\0text", "broken\ud800", new string('a', GlossaryFiles.MaximumBytes) })
        {
            try { GlossaryFiles.WriteText(file, invalidText); throw new Exception("invalid export accepted"); }
            catch (InvalidDataException) { }
            Check((await File.ReadAllBytesAsync(file)).SequenceEqual(exported), "invalid export leaves prior file intact");
        }
        using (var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try { GlossaryFiles.WriteText(file, "不能替换锁定文件"); throw new Exception("locked destination replaced"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        Check((await File.ReadAllBytesAsync(file)).SequenceEqual(exported), "failed destination replacement preserves original file");
        try { GlossaryFiles.MergeImported(existing, "bad\0text"); throw new Exception("invalid merge accepted"); }
        catch (InvalidDataException) { Check(existing == "配信 → 直播\r\n手动术语 → 自定义译法  （注释）\r\n同じ → 原有译法", "failed import preserves source glossary"); }
        Check(Directory.GetFiles(folder, ".kotoshiro-glossary-*.tmp").Length == 0, "no temporary export files remain");

        GlossaryFiles.WriteText(file, "新語 → 新词");
        Check(GlossaryFiles.ReadText(file) == "新語 → 新词", "explicit destination overwrite succeeds");
        return [
            "PASS: glossary TXT exports UTF-8 BOM with only text; Japanese/Chinese/non-BMP, spacing and notes roundtrip",
            "PASS: glossary TXT imports strict UTF-8, UTF-16 LE/BE BOM and GB18030, normalizing line breaks",
            "PASS: glossary TXT merge keeps user entries/order/notes, removes identical lines and retains conflicts for review",
            "PASS: default Japanese livestream glossary examples merge without replacement or duplicates",
            "PASS: exact five default terms, including hinome name; old built-in defaults upgrade while custom and empty glossaries remain unchanged",
            "PASS: oversized/binary/control/malformed TXT imports are rejected without changing source or prior glossary",
            "PASS: validated atomic glossary export preserves existing files on invalid input or locked destinations and cleans temporary files"
        ];
    }
}
