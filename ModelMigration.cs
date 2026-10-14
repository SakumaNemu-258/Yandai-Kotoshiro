using System.Security.Cryptography;

namespace Kotoshiro;

public static class ModelMigration
{
    public static async Task<int> MigrateAsync(string source, string destination, IProgress<string> progress, CancellationToken token, Func<Task> commitDestination, IReadOnlyList<ModelInfo>? models = null)
    {
        source = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        destination = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (source.Equals(destination, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(source)) return 0;
        var files = (models ?? Models.All).Where(x => File.Exists(Path.Combine(source, x.FileName))).ToArray();
        if (files.Length == 0) return 0;
        Directory.CreateDirectory(destination);
        foreach (var model in files)
        {
            token.ThrowIfCancellationRequested();
            string from = Child(source, model.FileName), to = Child(destination, model.FileName);
            progress.Report($"正在校验和复制 {model.Id} 模型…");
            await VerifyAsync(from, model.Sha256, token);
            if (File.Exists(to)) { await VerifyAsync(to, model.Sha256, token); continue; }
            string temporary = Child(destination, model.FileName + ".transfer-" + Guid.NewGuid().ToString("N"));
            try
            {
                await using (var input = File.OpenRead(from))
                await using (var output = File.Create(temporary)) await input.CopyToAsync(output, token);
                await VerifyAsync(temporary, model.Sha256, token); File.Move(temporary, to);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        token.ThrowIfCancellationRequested();
        // Persist the destination before deleting any source. Failure before commit leaves original models intact.
        await commitDestination();
        foreach (var model in files)
        {
            string from = Child(source, model.FileName), to = Child(destination, model.FileName);
            await VerifyAsync(to, model.Sha256, CancellationToken.None);
            await VerifyAsync(from, model.Sha256, CancellationToken.None); File.Delete(from);
        }
        return files.Length;
    }
    private static string Child(string parent, string name)
    {
        if (name != Path.GetFileName(name) || name.Contains(':') || name.Contains('\\') || name.Contains('/')) throw new InvalidDataException("模型文件名无效。");
        string full = Path.GetFullPath(Path.Combine(parent, name));
        if (!full.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("模型路径超出所选目录。");
        return full;
    }
    private static async Task VerifyAsync(string file, string expected, CancellationToken token)
    {
        await using var input = File.OpenRead(file);
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(input, token));
        if (!hash.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("模型文件不完整或目标有不同的同名文件，请检查后重试。");
    }
}
