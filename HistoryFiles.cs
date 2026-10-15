using System.Text;

namespace Kotoshiro;

/// <summary>Exports a captured record view as text without reading settings or account data.</summary>
internal static class HistoryFiles
{
    private static readonly UTF8Encoding Utf8 = new(true, true);

    internal static async Task WriteTextAsync(string path, string snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        // Finish encoding before touching the destination. RichTextBox uses LF; Windows TXT uses CRLF.
        byte[] body = Utf8.GetBytes(snapshot.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n"));
        string target = Path.GetFullPath(path);
        string temporary = Path.Combine(Path.GetDirectoryName(target)!, ".kotoshiro-history-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await file.WriteAsync(Utf8.GetPreamble());
                await file.WriteAsync(body);
                await file.FlushAsync();
            }
            File.Move(temporary, target, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
