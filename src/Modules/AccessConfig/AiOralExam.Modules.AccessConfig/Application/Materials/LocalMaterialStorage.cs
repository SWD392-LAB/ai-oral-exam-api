using Microsoft.Extensions.Options;

namespace AiOralExam.Modules.AccessConfig.Application.Materials;

internal sealed class LocalMaterialStorage(IOptions<MaterialOptions> options) : IMaterialStorage
{
    private readonly string _root = ResolveRoot(options.Value.StorageRoot);

    public async Task<string> PutAsync(Guid courseId, Guid materialId, string fileName, Stream content, CancellationToken ct)
    {
        var safeName = Path.GetFileName(fileName);
        var key = $"{courseId:N}/{materialId:N}/{safeName}";
        var path = ResolvePath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".uploading";
        try
        {
            await using (var target = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true))
                await content.CopyToAsync(target, ct);
            File.Move(temporary, path, true);
        }
        catch
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch
            {
                // Preserve the original write/move error.
            }
            throw;
        }
        return key;
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken ct)
    {
        var path = ResolvePath(key);
        return Task.FromResult<Stream?>(File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true)
            : null);
    }

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        var path = ResolvePath(key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private string ResolvePath(string key)
    {
        var root = Path.GetFullPath(_root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid material storage key.");
        return path;
    }

    private static string ResolveRoot(string configured) => Path.IsPathRooted(configured) ? configured : Path.Combine(AppContext.BaseDirectory, configured);
}
