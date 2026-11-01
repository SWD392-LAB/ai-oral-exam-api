using AiOralExam.Modules.Interview.Application.Ai;
using AiOralExam.Modules.Interview.Application.Audio;
using Microsoft.Extensions.Options;

namespace AiOralExam.UnitTests;

public sealed class LocalAudioStorageTests
{
    [Fact]
    public async Task Parameterized_webm_uploads_get_unique_immutable_keys_and_canonical_content_type()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aives-audio-tests-{Guid.NewGuid():N}");
        var storage = new LocalAudioStorage(Options.Create(new AiProviderOptions { AudioStorageRoot = root }));
        var attemptId = Guid.NewGuid();
        var turnId = Guid.NewGuid();

        try
        {
            var firstKey = await storage.PutAsync(attemptId, turnId,
                new MemoryStream([1, 2, 3]), "audio/webm;codecs=opus");
            var retryKey = await storage.PutAsync(attemptId, turnId,
                new MemoryStream([4, 5, 6]), "audio/webm");

            Assert.NotEqual(firstKey, retryKey);
            Assert.EndsWith(".webm", firstKey, StringComparison.Ordinal);
            Assert.EndsWith(".webm", retryKey, StringComparison.Ordinal);

            var first = await storage.OpenReadAsync(firstKey);
            var retry = await storage.OpenReadAsync(retryKey);
            Assert.NotNull(first);
            Assert.NotNull(retry);
            Assert.Equal("audio/webm", first!.ContentType);
            Assert.Equal("audio/webm", retry!.ContentType);
            await using (first.Content)
                Assert.Equal(new byte[] { 1, 2, 3 }, await ReadAllAsync(first.Content));
            await using (retry.Content)
                Assert.Equal(new byte[] { 4, 5, 6 }, await ReadAllAsync(retry.Content));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        await using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }
}
