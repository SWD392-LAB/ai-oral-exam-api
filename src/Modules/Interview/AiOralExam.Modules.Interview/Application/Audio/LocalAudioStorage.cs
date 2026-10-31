using Microsoft.Extensions.Options;
using AiOralExam.Modules.Interview.Application.Ai;

namespace AiOralExam.Modules.Interview.Application.Audio;

/// <summary>
/// Private local storage adapter for the demo. Only server-generated keys are
/// persisted in the database; the directory is never exposed as static files.
/// Replace this adapter with S3/MinIO without changing the audio service.
/// </summary>
internal sealed class LocalAudioStorage(IOptions<AiProviderOptions> options) : IAudioStorage
{
    private readonly string _root = ResolveRoot(options.Value.AudioStorageRoot);

    public async Task<string> PutAsync(Guid attemptId, Guid turnId, Stream content, string contentType, CancellationToken ct = default)
    {
        var extension = ExtensionFor(contentType);
        var key = $"{attemptId:N}/{turnId:N}{extension}";
        var path = ResolvePath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var temporary = path + ".uploading";
        await using (var target = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            await content.CopyToAsync(target, ct);

        File.Move(temporary, path, overwrite: true);
        return key;
    }

    public Task<StoredAudio?> OpenReadAsync(string key, CancellationToken ct = default)
    {
        var path = ResolvePath(key);
        if (!File.Exists(path)) return Task.FromResult<StoredAudio?>(null);
        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        return Task.FromResult<StoredAudio?>(new StoredAudio(stream, ContentTypeFor(Path.GetExtension(path))));
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var path = ResolvePath(key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private string ResolvePath(string key)
    {
        var normalized = key.Replace('/', Path.DirectorySeparatorChar);
        var root = Path.GetFullPath(_root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(root, normalized));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid audio storage key.");
        return path;
    }

    private static string ResolveRoot(string configured) =>
        Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(AppContext.BaseDirectory, configured);

    private static string ExtensionFor(string contentType) => contentType.ToLowerInvariant() switch
    {
        "audio/webm" => ".webm",
        "audio/wav" or "audio/x-wav" => ".wav",
        "audio/mpeg" or "audio/mp3" => ".mp3",
        "audio/ogg" => ".ogg",
        "audio/mp4" => ".m4a",
        _ => ".bin"
    };

    private static string ContentTypeFor(string extension) => extension.ToLowerInvariant() switch
    {
        ".webm" => "audio/webm",
        ".wav" => "audio/wav",
        ".mp3" => "audio/mpeg",
        ".ogg" => "audio/ogg",
        ".m4a" => "audio/mp4",
        _ => "application/octet-stream"
    };
}
