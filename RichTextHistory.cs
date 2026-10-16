namespace Kotoshiro;

internal static class RichTextHistory
{
    internal static void RemovePrefix(RichTextBox history, int characterCount)
    {
        int count = Math.Clamp(characterCount, 0, history.TextLength);
        if (count == 0) return;

        // Empty SelectedText sends WM_CLEAR, which refuses to trim a read-only
        // RichTextBox and can play a system alert on every subsequent append.
        // This synchronous UI-thread edit restores the user's read-only state.
        bool wasReadOnly = history.ReadOnly;
        try
        {
            history.ReadOnly = false;
            history.Select(0, count);
            history.SelectedText = string.Empty;
        }
        finally
        {
            history.ReadOnly = wasReadOnly;
        }
    }
}
