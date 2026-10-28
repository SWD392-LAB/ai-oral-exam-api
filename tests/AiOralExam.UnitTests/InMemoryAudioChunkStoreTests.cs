using AiOralExam.Modules.Interview.Application.Audio;
using AiOralExam.SharedKernel.Errors;

namespace AiOralExam.UnitTests;

public sealed class InMemoryAudioChunkStoreTests
{
    [Fact]
    public async Task Append_retries_conflicts_order_and_checkpoints_are_attempt_and_turn_scoped()
    {
        var store = new InMemoryAudioChunkStore();
        var attemptId = Guid.NewGuid();
        var turnId = Guid.NewGuid();

        var first = await store.AppendAsync(attemptId, turnId, 0, [1, 2], "audio/webm", default);
        var duplicate = await store.AppendAsync(attemptId, turnId, 0, [1, 2], "audio/webm", default);

        Assert.Equal(AudioChunkAppendStatus.Appended, first.Status);
        Assert.Equal(AudioChunkAppendStatus.Duplicate, duplicate.Status);
        Assert.Equal(1, duplicate.Checkpoint.NextSequence);
        Assert.Equal(2, duplicate.Checkpoint.BytesReceived);
        Assert.Equal(AudioChunkAppendStatus.OutOfOrder,
            (await store.AppendAsync(attemptId, turnId, 2, [3], "audio/webm", default)).Status);
        Assert.Equal(AudioChunkAppendStatus.ConflictingDuplicate,
            (await store.AppendAsync(attemptId, turnId, 0, [9], "audio/webm", default)).Status);
        Assert.Null(await store.GetCheckpointAsync(Guid.NewGuid(), turnId, default));
        Assert.Null(await store.GetCheckpointAsync(attemptId, Guid.NewGuid(), default));

        var checkpointAfterReconnect = await store.GetCheckpointAsync(attemptId, turnId, default);
        Assert.Equal(1, checkpointAfterReconnect!.NextSequence);
        Assert.Equal("audio/webm", checkpointAfterReconnect.ContentType);
    }

    [Fact]
    public async Task Concurrent_identical_chunk_requests_append_once()
    {
        var store = new InMemoryAudioChunkStore();
        var attemptId = Guid.NewGuid();
        var turnId = Guid.NewGuid();

        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ =>
            store.AppendAsync(attemptId, turnId, 0, [1, 2, 3], "audio/webm", default)));

        Assert.Single(results, result => result.Status == AudioChunkAppendStatus.Appended);
        Assert.Equal(31, results.Count(result => result.Status == AudioChunkAppendStatus.Duplicate));
        var checkpoint = await store.GetCheckpointAsync(attemptId, turnId, default);
        Assert.Equal(1, checkpoint!.NextSequence);
        Assert.Equal(3, checkpoint.BytesReceived);
    }

    [Fact]
    public async Task Completion_and_transcript_save_are_idempotent()
    {
        var store = new InMemoryAudioChunkStore();
        var attemptId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        await store.AppendAsync(attemptId, turnId, 0, [1, 2], "audio/webm", default);

        var completed = await store.CompleteUploadAsync(attemptId, turnId, default);
        var completedAgain = await store.CompleteUploadAsync(attemptId, turnId, default);
        Assert.Equal([1, 2], await ReadAllBytesAsync(completed.Audio));
        Assert.Equal([1, 2], await ReadAllBytesAsync(completedAgain.Audio));
        Assert.Equal("persisted transcript",
            await store.SaveTranscriptIfAbsentAsync(attemptId, turnId, "persisted transcript", default));
        Assert.Equal("persisted transcript",
            await store.SaveTranscriptIfAbsentAsync(attemptId, turnId, "replacement", default));

        var checkpoint = await store.GetCheckpointAsync(attemptId, turnId, default);
        Assert.True(checkpoint!.IsCompleted);
        Assert.Equal("persisted transcript", checkpoint.Transcript);
        Assert.Equal(AudioChunkAppendStatus.UploadCompleted,
            (await store.AppendAsync(attemptId, turnId, 1, [3], "audio/webm", default)).Status);
    }

    [Fact]
    public async Task Chunk_size_and_content_type_are_validated_by_store()
    {
        var store = new InMemoryAudioChunkStore();
        var tooLarge = new byte[AudioChunkLimits.MaximumChunkBytes + 1];

        await Assert.ThrowsAsync<ValidationException>(() =>
            store.AppendAsync(Guid.NewGuid(), Guid.NewGuid(), 0, tooLarge, "audio/webm", default));
        await Assert.ThrowsAsync<ValidationException>(() =>
            store.AppendAsync(Guid.NewGuid(), Guid.NewGuid(), -1, [1], "audio/webm", default));
        await Assert.ThrowsAsync<ValidationException>(() =>
            store.AppendAsync(Guid.NewGuid(), Guid.NewGuid(), 0, [1], "application/octet-stream", default));
    }

    [Fact]
    public async Task Completed_upload_expires_after_24_hours_without_access()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2025-01-01T00:00:00Z"));
        var store = new InMemoryAudioChunkStore(clock);
        var attemptId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        await store.AppendAsync(attemptId, turnId, 0, [1], "audio/webm", default);
        await store.CompleteUploadAsync(attemptId, turnId, default);

        clock.Advance(TimeSpan.FromHours(23));
        Assert.NotNull(await store.GetCheckpointAsync(attemptId, turnId, default));
        clock.Advance(TimeSpan.FromHours(23));
        Assert.NotNull(await store.GetCheckpointAsync(attemptId, turnId, default));
        clock.Advance(TimeSpan.FromHours(25));

        Assert.Null(await store.GetCheckpointAsync(attemptId, turnId, default));
    }

    [Fact]
    public async Task Cleanup_retains_active_uploads_and_recovers_expired_capacity()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2025-01-01T00:00:00Z"));
        var store = new InMemoryAudioChunkStore(clock);
        var activeAttemptId = Guid.NewGuid();
        var activeTurnId = Guid.NewGuid();
        await store.AppendAsync(activeAttemptId, activeTurnId, 0, [1], "audio/webm", default);
        var expiredAttemptId = Guid.Empty;
        var expiredTurnId = Guid.Empty;

        for (var index = 1; index < InMemoryAudioChunkStore.MaximumUploads; index++)
        {
            var attemptId = Guid.NewGuid();
            var turnId = Guid.NewGuid();
            if (index == 1)
            {
                expiredAttemptId = attemptId;
                expiredTurnId = turnId;
            }
            await store.AppendAsync(attemptId, turnId, 0, [1], "audio/webm", default);
            await store.CompleteUploadAsync(attemptId, turnId, default);
        }

        clock.Advance(TimeSpan.FromHours(25));
        var newAttemptId = Guid.NewGuid();
        var newTurnId = Guid.NewGuid();
        var newUpload = await store.AppendAsync(newAttemptId, newTurnId, 0, [2], "audio/webm", default);

        Assert.Equal(1, newUpload.Checkpoint.NextSequence);
        Assert.NotNull(await store.GetCheckpointAsync(activeAttemptId, activeTurnId, default));
        Assert.Null(await store.GetCheckpointAsync(expiredAttemptId, expiredTurnId, default));
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream)
    {
        await using (stream)
        {
            using var output = new MemoryStream();
            await stream.CopyToAsync(output);
            return output.ToArray();
        }
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }
}
