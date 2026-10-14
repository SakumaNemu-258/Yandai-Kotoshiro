using System.Runtime.InteropServices;

namespace Kotoshiro;

internal static class SubtitleOverlayTests
{
    internal static string Unit(string? previewFolder = null)
    {
        using var overlay = new SubtitleOverlay(new AppSettings { OverlayX = 100, OverlayY = 100, OverlayWidth = 900, OverlayHeight = 180 });
        _ = overlay.Handle;
        if (overlay.FormBorderStyle != FormBorderStyle.None || overlay.ControlBox || overlay.MinimizeBox || overlay.MaximizeBox || overlay.ShowInTaskbar)
            throw new InvalidOperationException("FAIL: subtitle title bar and window buttons must stay hidden");
        if ((GetWindowLong(overlay.Handle, -16) & 0x00C00000) != 0)
            throw new InvalidOperationException("FAIL: native subtitle window must have no caption style");
        if (overlay.Region is null || overlay.Region.IsVisible(0, 0) || !overlay.Region.IsVisible(overlay.Width / 2, overlay.Height / 2))
            throw new InvalidOperationException("FAIL: subtitle region must clip corners and retain the content area");
        foreach (var (point, expected) in new (Point, int)[]
        {
            (new(2, 2), 13), (new(898, 2), 14), (new(2, 178), 16), (new(898, 178), 17),
            (new(2, 90), 10), (new(898, 90), 11), (new(450, 2), 12), (new(450, 178), 15), (new(450, 90), 1)
        })
        { if (overlay.HitTest(point) != expected) throw new InvalidOperationException("FAIL: subtitle drag/resize hit regions"); }
        overlay.SetPassThrough(true);
        if (overlay.HitTest(new Point(450, 90)) != -1 || overlay.FormBorderStyle != FormBorderStyle.None || (GetWindowLong(overlay.Handle, -20) & 0x80020) != 0x80020)
            throw new InvalidOperationException("FAIL: enabling click-through must preserve the borderless subtitle");
        overlay.Apply(new AppSettings { OverlayOpacity = 100 });
        if ((GetWindowLong(overlay.Handle, -20) & 0x80020) != 0x80020 || !GetLayeredWindowAttributes(overlay.Handle, out _, out byte alpha, out uint flags) || alpha != 255 || (flags & 2) == 0)
            throw new InvalidOperationException("FAIL: fully opaque click-through subtitle must retain a valid layered alpha value");
        overlay.SetPassThrough(false);
        if (overlay.HitTest(new Point(450, 90)) != 1 || overlay.FormBorderStyle != FormBorderStyle.None || (GetWindowLong(overlay.Handle, -20) & 0x20) != 0)
            throw new InvalidOperationException("FAIL: disabling click-through must restore interaction without title buttons");
        overlay.Size = new Size(800, 150);
        if (overlay.Region is null || overlay.Region.IsVisible(0, 0) || !overlay.Region.IsVisible(400, 75))
            throw new InvalidOperationException("FAIL: rounded region must follow resized subtitle bounds");
        var saved = new AppSettings(); overlay.RememberBounds(saved);
        if (saved.OverlayWidth != 800 || saved.OverlayHeight != 150 || saved.OverlayX != overlay.Left || saved.OverlayY != overlay.Top)
            throw new InvalidOperationException("FAIL: subtitle bounds must remain persistent");
        overlay.Size = new Size(720, 320);
        overlay.Show(); Application.DoEvents();
        var history = overlay.HistoryForTest;
        if (!history.ReadOnly || !history.WordWrap || history.ScrollBars != RichTextBoxScrollBars.ForcedVertical || (GetWindowLong(history.Handle, -16) & 0x00200000) == 0)
            throw new InvalidOperationException("FAIL: subtitle history must be readonly, multiline and expose a visible vertical scrollbar");
        overlay.SetStatus("正在等待直播声音…");
        if (overlay.EntryCountForTest != 0 || history.TextLength != 0)
            throw new InvalidOperationException("FAIL: progress prompts must not become subtitle history");
        overlay.SetCaption("一行目\n二行目", "第一段翻译\n第二行中文");
        overlay.SetCaption("次の字幕", "第二段翻译");
        if (overlay.EntryCountForTest != 2 || !history.Text.Contains("一行目\n二行目") || !history.Text.Contains("第一段翻译\n第二行中文") || !history.Text.Contains("第二段翻译"))
            throw new InvalidOperationException("FAIL: complete bilingual multiline captions must accumulate");
        overlay.Apply(new AppSettings { ShowJapanese = false, FontSize = 20 });
        if (history.Text.Contains("一行目") || !history.Text.Contains("第一段翻译") || overlay.EntryCountForTest != 2)
            throw new InvalidOperationException("FAIL: hiding Japanese must retain Chinese history");
        overlay.Apply(new AppSettings { ShowJapanese = true, FontSize = 20 });
        if (!history.Text.Contains("一行目") || !history.Text.Contains("第二段翻译"))
            throw new InvalidOperationException("FAIL: showing Japanese must restore stored source history");
        for (int i = 0; i < 24; i++) overlay.SetCaption($"日语原文 {i}", $"中文历史第 {i} 段\n多行译文仍然完整保留。");
        Application.DoEvents();
        if (!overlay.FollowingLatestForTest) throw new InvalidOperationException("FAIL: new captions must follow the latest by default");
        SendMessage(history.Handle, 0xB6, IntPtr.Zero, (IntPtr)(-12)); Application.DoEvents();
        var before = Point.Empty; SendMessagePoint(history.Handle, 0x04DD, IntPtr.Zero, ref before);
        if (overlay.FollowingLatestForTest) throw new InvalidOperationException("FAIL: scrolling up must suspend latest following");
        overlay.SetCaption("新しい字幕", "用户查看历史时收到的新字幕");
        var after = Point.Empty; SendMessagePoint(history.Handle, 0x04DD, IntPtr.Zero, ref after);
        if (after != before || overlay.FollowingLatestForTest || !history.Text.Contains("用户查看历史时收到的新字幕"))
            throw new InvalidOperationException($"FAIL: appending during history review must preserve the viewport ({before} -> {after})");
        history.Select(10, 4); var selected = history.SelectedText;
        overlay.SetCaption("選択中", "选择文字时也不能跳到最新字幕");
        if (history.SelectionStart != 10 || history.SelectionLength != 4 || history.SelectedText != selected)
            throw new InvalidOperationException("FAIL: appending captions must preserve a user's text selection");
        VerifyThemeSwitch(overlay, following: false);
        history.Select(history.TextLength, 0); SendMessage(history.Handle, 0x115, (IntPtr)7, IntPtr.Zero); Application.DoEvents();
        if (!overlay.FollowingLatestForTest) throw new InvalidOperationException("FAIL: dragging the scrollbar to the bottom must resume latest following");
        overlay.SetCaption("最新", "返回底部后自动继续跟随最新字幕");
        if (!overlay.FollowingLatestForTest) throw new InvalidOperationException("FAIL: latest following must stay enabled after returning to bottom");
        VerifyThemeSwitch(overlay, following: true);
        overlay.ClearHistory();
        for (int i = 0; i <= SubtitleOverlay.MaximumEntries; i++) overlay.SetCaption($"source-{i:D4}", $"caption-{i:D4}");
        if (overlay.EntryCountForTest != SubtitleOverlay.MaximumEntries || history.Text.Contains("caption-0000") || !history.Text.Contains("caption-0300"))
            throw new InvalidOperationException("FAIL: bounded history must discard oldest entries and retain the newest caption");
        SendMessage(history.Handle, 0xB6, IntPtr.Zero, (IntPtr)(-12)); Application.DoEvents();
        int anchor = history.GetCharIndexFromPosition(new Point(1, 1));
        string anchorText = history.Text.Substring(anchor, Math.Min(15, history.TextLength - anchor));
        overlay.SetCaption("source-0301", "caption-0301");
        int newAnchor = history.GetCharIndexFromPosition(new Point(1, 1));
        string newAnchorText = history.Text.Substring(newAnchor, Math.Min(15, history.TextLength - newAnchor));
        if (anchorText != newAnchorText || overlay.FollowingLatestForTest)
            throw new InvalidOperationException("FAIL: removing old history must preserve the retained entry currently being reviewed");
        overlay.ClearHistory();
        overlay.SetCaption(new string('旧', 90_000), new string('旧', 90_000));
        overlay.SetCaption(new string('日', 90_000), new string('中', 90_000));
        overlay.SetCaption("最新内容", "字符上限之后仍显示最新字幕");
        if (history.TextLength > SubtitleOverlay.MaximumHistoryCharacters || history.Text.Contains('旧') || !history.Text.Contains("字符上限之后仍显示最新字幕"))
            throw new InvalidOperationException("FAIL: oversized subtitles must not exceed the history character budget");
        overlay.ClearHistory();
        if (history.TextLength != 0 || overlay.EntryCountForTest != 0 || !overlay.FollowingLatestForTest)
            throw new InvalidOperationException("FAIL: clear history must reset content and latest-follow state");
        overlay.Hide();
        string compact = CompactLayout(previewFolder);
        return "PASS: borderless subtitle window, rounded region, eight resize edges, click-through and saved bounds; bilingual multiline history, visible native scrollbar, Japanese display toggle, status exclusion, bounded storage, stable scrolled viewport/text selection and automatic bottom following; black/white runtime switches preserve reviewed text, selection, scroll, follow state and visible new captions in a real STA window\n" + compact;
    }

    internal static string CompactLayout(string? previewFolder = null)
    {
        var settings = new AppSettings { OverlayX = 80, OverlayY = 80, OverlayWidth = 780, OverlayHeight = 300, FontSize = 26, OverlayOpacity = 100, ShowJapanese = true };
        using var overlay = new SubtitleOverlay(settings);
        overlay.Show(); Application.DoEvents();
        var history = overlay.HistoryForTest;
        overlay.SetCaption("あめ つち ほし そら", "天 地 星 空");
        overlay.SetCaption("やま かは みね たに", "山 川 峰 谷");
        ScrollToTop(history);
        const string bilingual = "あめ つち ほし そら\n天 地 星 空\nやま かは みね たに\n山 川 峰 谷\n";
        if (history.Text != bilingual)
            throw new InvalidOperationException("FAIL: compact captions must contain exactly one newline per displayed paragraph, without empty separators");
        int firstChinese = history.Text.IndexOf("天 地", StringComparison.Ordinal);
        int secondJapanese = history.Text.IndexOf("やま", StringComparison.Ordinal);
        int lastChinese = history.Text.IndexOf("山 川", StringComparison.Ordinal);
        int firstSourceY = history.GetPositionFromCharIndex(0).Y;
        int firstChineseY = history.GetPositionFromCharIndex(firstChinese).Y;
        int secondSourceY = history.GetPositionFromCharIndex(secondJapanese).Y;
        int lastChineseY = history.GetPositionFromCharIndex(lastChinese).Y;
        using var japanese = new Font("Microsoft YaHei UI", 16);
        using var chinese = new Font("Microsoft YaHei UI", 26, FontStyle.Bold);
        using var graphics = history.CreateGraphics();
        int japaneseHeight = (int)Math.Ceiling(japanese.GetHeight(graphics));
        int chineseHeight = (int)Math.Ceiling(chinese.GetHeight(graphics));
        int tolerance = (int)Math.Ceiling(5 * history.DeviceDpi / 96f);
        int sourceToChinese = firstChineseY - firstSourceY;
        int betweenEntries = secondSourceY - firstChineseY;
        if (sourceToChinese <= 0 || sourceToChinese > japaneseHeight + tolerance || betweenEntries <= 0 || betweenEntries > chineseHeight + tolerance)
            throw new InvalidOperationException($"FAIL: native paragraph geometry still reserves a blank line (source→Chinese={sourceToChinese}, Chinese→source={betweenEntries}; natural heights={japaneseHeight}/{chineseHeight})");
        int bilingualHeight = lastChineseY - firstSourceY;
        SaveCompactPreview(overlay, previewFolder, "subtitle-compact-bilingual.png");

        history.Select(firstChinese, "天 地 星 空".Length);
        string selected = history.SelectedText;
        settings.ShowJapanese = false; overlay.Apply(settings); Application.DoEvents();
        if (history.Text != "天 地 星 空\n山 川 峰 谷\n" || history.SelectionStart != 0 || history.SelectedText != selected || overlay.FollowingLatestForTest)
            throw new InvalidOperationException("FAIL: hiding source must start directly with Chinese and remap a Chinese text selection exactly");
        ScrollToTop(history);
        int chineseStartY = history.GetPositionFromCharIndex(0).Y;
        int chineseSecondY = history.GetPositionFromCharIndex(history.Text.IndexOf("山 川", StringComparison.Ordinal)).Y;
        if (chineseStartY < 0 || chineseStartY > tolerance || chineseSecondY - chineseStartY >= bilingualHeight || chineseSecondY - chineseStartY > chineseHeight + tolerance)
            throw new InvalidOperationException($"FAIL: Chinese-only captions retain source padding or an empty paragraph (firstY={chineseStartY}, span={chineseSecondY - chineseStartY}, bilingual span={bilingualHeight})");
        SaveCompactPreview(overlay, previewFolder, "subtitle-compact-chinese-only.png");

        settings.ShowJapanese = true; overlay.Apply(settings); Application.DoEvents();
        if (history.SelectionStart != firstChinese || history.SelectedText != selected)
            throw new InvalidOperationException("FAIL: restoring source must restore the retained Chinese selection at its exact bilingual offset");
        overlay.ClearHistory();
        for (int i = 0; i < 35; i++) overlay.SetCaption($"日文第 {i:D2} 段", $"中文第 {i:D2} 段");
        SendMessage(history.Handle, 0xB6, IntPtr.Zero, (IntPtr)(-20)); Application.DoEvents();
        int visibleCharacter = history.GetCharIndexFromPosition(new Point(1, 1));
        int visibleChinese = history.Text.IndexOf("中文第", visibleCharacter, StringComparison.Ordinal);
        if (visibleChinese < 0) throw new InvalidOperationException("FAIL: compact viewport test must find a visible Chinese caption");
        history.Select(visibleChinese, "中文第 00 段".Length);
        string visibleSelection = history.SelectedText;
        int visibleY = history.GetPositionFromCharIndex(visibleChinese).Y;
        settings.ShowJapanese = false; overlay.Apply(settings); Application.DoEvents();
        if (history.SelectedText != visibleSelection || overlay.FollowingLatestForTest || Math.Abs(history.GetPositionFromCharIndex(history.SelectionStart).Y - visibleY) > japaneseHeight + tolerance)
            throw new InvalidOperationException("FAIL: hiding source must retain the reviewed Chinese caption near its previous viewport position and preserve paused following");
        settings.ShowJapanese = true; overlay.Apply(settings); Application.DoEvents();
        if (history.SelectedText != visibleSelection || overlay.FollowingLatestForTest || Math.Abs(history.GetPositionFromCharIndex(history.SelectionStart).Y - visibleY) > japaneseHeight + tolerance)
            throw new InvalidOperationException("FAIL: restoring source must retain the compact history selection and visible reviewed caption");
        overlay.Hide();
        return $"PASS: real RichEdit compact paragraph geometry; source→Chinese {sourceToChinese}px, Chinese→next source {betweenEntries}px; Chinese-only first Y={chineseStartY}px and two-caption span={chineseSecondY - chineseStartY}px versus bilingual span={bilingualHeight}px; no source placeholder, exact selection remapping and stable historical review across source display switches";
    }

    private static void ScrollToTop(RichTextBox history)
    {
        SendMessage(history.Handle, 0x115, (IntPtr)6, IntPtr.Zero);
        Application.DoEvents();
    }

    private static void SaveCompactPreview(SubtitleOverlay overlay, string? folder, string filename)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        Directory.CreateDirectory(folder);
        using var image = overlay.CapturePreview();
        image.Save(Path.Combine(folder, filename), System.Drawing.Imaging.ImageFormat.Png);
    }

    private static void VerifyThemeSwitch(SubtitleOverlay overlay, bool following)
    {
        var history = overlay.HistoryForTest;
        string originalTheme = Theme.Current;
        try
        {
            foreach (string mode in new[] { "light", "dark" })
            {
                string content = history.Text, selected = history.SelectedText;
                int start = history.SelectionStart, length = history.SelectionLength, entries = overlay.EntryCountForTest;
                var before = Point.Empty; SendMessagePoint(history.Handle, 0x04DD, IntPtr.Zero, ref before);
                if (overlay.FollowingLatestForTest != following || (!following && length == 0))
                    throw new InvalidOperationException("FAIL: theme test must start with a real selected historical caption or bottom-follow state");

                Theme.Use(mode); Theme.RefreshOpenForms(); Application.DoEvents();
                var after = Point.Empty; SendMessagePoint(history.Handle, 0x04DD, IntPtr.Zero, ref after);
                if (history.Text != content || overlay.EntryCountForTest != entries || history.SelectionStart != start || history.SelectionLength != length || history.SelectedText != selected)
                    throw new InvalidOperationException($"FAIL: {mode} theme switch must preserve stored subtitle history and the user's selection");
                if ((!following && before != after) || overlay.FollowingLatestForTest != following)
                    throw new InvalidOperationException($"FAIL: {mode} theme switch must preserve history scroll and follow state ({before} -> {after}, follow={overlay.FollowingLatestForTest})");
                if (history.BackColor != Theme.Surface || history.ForeColor != Theme.Text ||
                    !history.Rtf!.Contains($"\\red{Theme.Muted.R}\\green{Theme.Muted.G}\\blue{Theme.Muted.B};", StringComparison.Ordinal) ||
                    history.ContextMenuStrip is not { } menu || menu.BackColor != Theme.Surface || menu.Items.Cast<ToolStripItem>().Any(x => x.ForeColor != Theme.Text))
                    throw new InvalidOperationException($"FAIL: {mode} subtitle history or context menu has stale theme colors");

                string caption = following ? $"{mode} 主题切换后新字幕仍自动显示" : $"{mode} 主题切换后浏览历史不跳动";
                overlay.SetCaption("テーマを切り替えた後の字幕です。", caption); Application.DoEvents();
                if (!history.Text.Contains(caption, StringComparison.Ordinal) || overlay.FollowingLatestForTest != following)
                    throw new InvalidOperationException($"FAIL: {mode} theme switch must retain incoming captions and the selected follow mode");
                if (following)
                {
                    var position = history.GetPositionFromCharIndex(history.Text.IndexOf(caption, StringComparison.Ordinal));
                    if (position.Y < 0 || position.Y >= history.ClientSize.Height)
                        throw new InvalidOperationException($"FAIL: latest {mode} subtitle must remain visible after theme switching and appending");
                }
                else
                {
                    SendMessagePoint(history.Handle, 0x04DD, IntPtr.Zero, ref after);
                    if (after != before || history.SelectionStart != start || history.SelectionLength != length || history.SelectedText != selected)
                        throw new InvalidOperationException($"FAIL: a new {mode} caption must not interrupt historical review after theme switching");
                }
            }
        }
        finally { Theme.Use(originalTheme); Theme.RefreshOpenForms(); Application.DoEvents(); }
    }
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr handle, int index);
    [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(IntPtr handle, out uint colorKey, out byte alpha, out uint flags);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr SendMessagePoint(IntPtr handle, int message, IntPtr wParam, ref Point lParam);
}
