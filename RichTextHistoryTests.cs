namespace Kotoshiro;

internal static class RichTextHistoryTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAIL: rich text history " + message);
    }

    internal static List<string> Run()
    {
        // Native controls can install a WinForms context. Restore the caller's
        // context before the command-line self-tests await further checks.
        var previousContext = SynchronizationContext.Current;
        try { return RunCore(); }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
    }

    private static List<string> RunCore()
    {
        Check(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "native control checks require STA");
        var results = new List<string>();
        using (var history = CreateHistory(false))
        using (var japanese = new Font("Microsoft YaHei UI", 10))
        using (var chinese = new Font("Microsoft YaHei UI", 14, FontStyle.Bold))
        {
            const string old = "古い記録\n";
            const string source = "残す日本語";
            const string translation = "保留最新中文 🎉";
            history.Text = old + source + "\n" + translation + "\n";
            history.Select(old.Length, source.Length);
            history.SelectionFont = japanese; history.SelectionColor = Color.DarkSlateBlue;
            history.Select(old.Length + source.Length + 1, translation.Length);
            history.SelectionFont = chinese; history.SelectionColor = Color.DarkGreen;
            history.ReadOnly = true;

            // RichEdit normalizes font metrics; compare native runs before and
            // after deletion rather than the original managed Font instances.
            history.Select(old.Length, source.Length);
            using var originalJapaneseFont = history.SelectionFont;
            Color originalJapaneseColor = history.SelectionColor;
            history.Select(old.Length + source.Length + 1, "保留最新中文".Length);
            using var originalChineseFont = history.SelectionFont;
            Color originalChineseColor = history.SelectionColor;
            Check(originalJapaneseFont is not null && originalChineseFont is not null && !originalJapaneseFont.Bold && originalChineseFont.Bold && originalJapaneseColor != originalChineseColor, "fixture contains distinct native Japanese and Chinese formatting");

            RichTextHistory.RemovePrefix(history, old.Length);
            Check(history.ReadOnly && history.Text == source + "\n" + translation + "\n", "prefix is removed from a readonly native control without changing the retained Unicode text");
            history.Select(0, source.Length);
            using (var font = history.SelectionFont)
                Check(font is not null && font.Equals(originalJapaneseFont) && history.SelectionColor.ToArgb() == originalJapaneseColor.ToArgb(), "retained Japanese font and color survive trimming");
            history.Select(source.Length + 1, "保留最新中文".Length);
            using (var font = history.SelectionFont)
                Check(font is not null && font.Equals(originalChineseFont) && history.SelectionColor.ToArgb() == originalChineseColor.ToArgb(), "retained Chinese font, weight and color survive trimming");
            results.Add("PASS: readonly native rich text prefix trimming removes old records while retaining Unicode, Japanese/Chinese fonts, weight and colors, and restoring read-only interaction");
        }

        foreach (bool readOnly in new[] { false, true })
        {
            using var history = CreateHistory(readOnly);
            history.AppendText("甲乙丙丁\n");
            history.Select(1, 2);
            RichTextHistory.RemovePrefix(history, 0);
            RichTextHistory.RemovePrefix(history, -100);
            Check(history.ReadOnly == readOnly && history.Text == "甲乙丙丁\n" && history.SelectedText == "乙丙", "nonpositive trim leaves text, selection and editability intact");
            RichTextHistory.RemovePrefix(history, 2);
            Check(history.ReadOnly == readOnly && history.Text == "丙丁\n", "an editable or readonly control retains its original editability");
            RichTextHistory.RemovePrefix(history, int.MaxValue);
            Check(history.ReadOnly == readOnly && history.TextLength == 0, "oversized deletion safely removes all available text");
            RichTextHistory.RemovePrefix(history, 100);
            Check(history.ReadOnly == readOnly && history.TextLength == 0, "an already empty control remains usable");
            history.AppendText("新的记录\n");
            Check(history.Text == "新的记录\n", "new records can be appended after complete trimming");
        }
        results.Add("PASS: zero, negative, oversized and empty-history trims are safe; originally editable controls remain editable and new records still append after clearing");

        foreach (var (maximum, retained) in new[] { (30_000, 24_000), (50_000, 40_000) })
        {
            using var history = CreateHistory(true);
            string expected = "";
            int trims = 0;
            for (int i = 0; i < 2_400; i++)
            {
                string next = $"{i:D4} 配信を見ながら日本語を勉強します。\n第{i:D4}条：保留最近翻译和弹幕，清理过期内容以后仍持续更新。🎉\n\n";
                history.AppendText(next); expected += next;
                if (history.TextLength > maximum)
                {
                    int remove = history.TextLength - retained;
                    RichTextHistory.RemovePrefix(history, remove);
                    expected = expected[remove..]; trims++;
                    Check(history.TextLength == retained, "every long-running trim reduces the native buffer to its retention limit");
                }
                Check(history.ReadOnly && history.TextLength <= maximum, "sustained appends remain readonly and bounded");
            }
            Check(trims >= 8 && history.Text == expected && history.Text.Contains("第2399条") && !history.Text.Contains("第0000条"), "repeated threshold crossings retain the complete newest tail instead of endlessly retrying an unsuccessful deletion");
            results.Add($"PASS: {maximum:N0}-character history survives 2,400 Unicode appends and {trims} retention trims; native contents stay bounded and preserve the newest records");
        }
        return results;
    }

    private static RichTextBox CreateHistory(bool readOnly)
    {
        var history = new RichTextBox { ReadOnly = readOnly, DetectUrls = false, Multiline = true, WordWrap = true };
        _ = history.Handle;
        return history;
    }
}
