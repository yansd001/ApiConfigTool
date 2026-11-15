using System.IO;
using System.Text;

namespace ApiConfigTool.Services;

internal static class AtomicFile
{
    public static byte[]? Read(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;
    public static byte[]? Encode(string? text) => text is null ? null : new UTF8Encoding(false).GetBytes(text);

    public static void Write(string path, byte[]? content)
    {
        if (content is null)
        {
            File.Delete(path);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    // Same-directory replacements plus rollback, including the provider store. This is
    // not a filesystem-wide transaction; do not claim crash-atomic multi-file writes.
    public static void Commit(IReadOnlyList<(string Path, byte[]? Content)> files,
        Action<string, byte[]?>? writer = null)
    {
        writer ??= Write;
        var originals = files.Select(file => Read(file.Path)).ToArray();
        var committed = 0;
        try
        {
            foreach (var file in files)
            {
                writer(file.Path, file.Content);
                committed++;
            }
        }
        catch (Exception failure)
        {
            var errors = new List<Exception> { failure };
            for (var i = committed - 1; i >= 0; i--)
            {
                try { Write(files[i].Path, originals[i]); }
                catch (Exception rollback) { errors.Add(rollback); }
            }
            if (errors.Count > 1)
                throw new AggregateException("写入失败，部分文件未能恢复。请检查配置目录。", errors);
            throw;
        }
    }
}
