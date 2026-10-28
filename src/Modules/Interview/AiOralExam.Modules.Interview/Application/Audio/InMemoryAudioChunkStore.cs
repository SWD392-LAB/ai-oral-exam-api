using AiOralExam.SharedKernel.Errors;

namespace AiOralExam.Modules.Interview.Application.Audio;

/// <summary>
/// Development/testing audio storage only. Completed uploads expire after 24 hours without access;
/// active uploads are never evicted. Data is lost when the process restarts, and separate server
/// instances do not share uploads or checkpoints.
/// </summary>
internal sealed class InMemoryAudioChunkStore : IAudioChunkStore
{
    internal const int MaximumUploads = 1000;
    private const int MaximumChunksPerUpload = 4096;
    private const long MaximumUploadBytes = 25 * 1024 * 1024;
    private const long MaximumStoredBytes = 128 * 1024 * 1024;
    private static readonly TimeSpan CompletedUploadRetention = TimeSpan.FromHours(24);

    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly Dictionary<(Guid AttemptId, Guid TurnId), AudioUpload> _uploads = [];
    private long _storedBytes;

    public InMemoryAudioChunkStore(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    public Task<AudioChunkAppendResult> AppendAsync(
        Guid attemptId, Guid turnId, int sequence, byte[] chunk, string contentType, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ValidateChunk(sequence, chunk, contentType);

        lock (_gate)
        {
            RemoveExpiredCompletedUploads();
            var key = (attemptId, turnId);
            if (!_uploads.TryGetValue(key, out var upload))
            {
                if (_uploads.Count >= MaximumUploads)
                    throw new AppException(503, "audio_storage_capacity_reached", "The in-memory audio store is full.");
                if (_storedBytes + chunk.Length > MaximumStoredBytes)
                    throw new AppException(413, "audio_upload_too_large", "The audio upload exceeds the configured storage limit.");
                upload = new AudioUpload(contentType);
                _uploads.Add(key, upload);
            }

            AudioChunkAppendStatus status;
            if (upload.IsCompleted)
            {
                Touch(upload);
                status = AudioChunkAppendStatus.UploadCompleted;
            }
            else if (!string.Equals(upload.ContentType, contentType, StringComparison.OrdinalIgnoreCase))
                status = AudioChunkAppendStatus.ContentTypeConflict;
            else if (sequence < upload.Chunks.Count)
                status = upload.Chunks[sequence].AsSpan().SequenceEqual(chunk)
                    ? AudioChunkAppendStatus.Duplicate
                    : AudioChunkAppendStatus.ConflictingDuplicate;
            else if (sequence > upload.Chunks.Count)
                status = AudioChunkAppendStatus.OutOfOrder;
            else if (sequence >= MaximumChunksPerUpload)
                throw new AppException(413, "audio_upload_too_large", "The audio upload contains too many chunks.");
            else if (upload.BytesReceived + chunk.Length > MaximumUploadBytes
                || _storedBytes + chunk.Length > MaximumStoredBytes)
                throw new AppException(413, "audio_upload_too_large", "The audio upload exceeds the configured storage limit.");
            else
            {
                upload.Chunks.Add(chunk.ToArray());
                upload.BytesReceived += chunk.Length;
                _storedBytes += chunk.Length;
                status = AudioChunkAppendStatus.Appended;
            }

            return Task.FromResult(new AudioChunkAppendResult(status, upload.ToCheckpoint()));
        }
    }

    public Task<AudioChunkCheckpoint?> GetCheckpointAsync(Guid attemptId, Guid turnId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            RemoveExpiredCompletedUploads();
            return Task.FromResult(_uploads.TryGetValue((attemptId, turnId), out var upload)
                ? GetCheckpoint(upload)
                : null);
        }
    }

    public Task<CompletedAudio> CompleteUploadAsync(Guid attemptId, Guid turnId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            RemoveExpiredCompletedUploads();
            if (!_uploads.TryGetValue((attemptId, turnId), out var upload) || upload.Chunks.Count == 0)
                throw new ConflictException("audio_upload_empty", "Upload at least one audio chunk before completing.");

            upload.IsCompleted = true;
            Touch(upload);
            var audio = upload.Chunks.SelectMany(chunk => chunk).ToArray();
            return Task.FromResult(new CompletedAudio(new MemoryStream(audio, writable: false),
                upload.ContentType, upload.Transcript));
        }
    }

    public Task<string> SaveTranscriptIfAbsentAsync(
        Guid attemptId, Guid turnId, string transcript, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            RemoveExpiredCompletedUploads();
            if (!_uploads.TryGetValue((attemptId, turnId), out var upload) || !upload.IsCompleted)
                throw new ConflictException("audio_upload_not_completed", "Complete the audio upload before saving its transcript.");

            upload.Transcript ??= transcript;
            Touch(upload);
            return Task.FromResult(upload.Transcript);
        }
    }

    private AudioChunkCheckpoint GetCheckpoint(AudioUpload upload)
    {
        Touch(upload);
        return upload.ToCheckpoint();
    }

    private void Touch(AudioUpload upload)
    {
        if (upload.IsCompleted)
            upload.LastAccessedAt = _clock.GetUtcNow();
    }

    private void RemoveExpiredCompletedUploads()
    {
        var expirationTime = _clock.GetUtcNow() - CompletedUploadRetention;
        foreach (var key in _uploads
            .Where(item => item.Value.IsCompleted
                && item.Value.LastAccessedAt is DateTimeOffset lastAccess && lastAccess <= expirationTime)
            .Select(item => item.Key)
            .ToArray())
        {
            _storedBytes -= _uploads[key].BytesReceived;
            _uploads.Remove(key);
        }
    }

    private static void ValidateChunk(int sequence, byte[] chunk, string contentType)
    {
        var errors = new Dictionary<string, string[]>();
        if (sequence < 0)
            errors[nameof(sequence)] = ["Chunk sequence must be zero or greater."];
        if (chunk is null || chunk.Length == 0)
            errors[nameof(chunk)] = ["Audio chunks cannot be empty."];
        else if (chunk.Length > AudioChunkLimits.MaximumChunkBytes)
            errors[nameof(chunk)] = [$"Audio chunks cannot exceed {AudioChunkLimits.MaximumChunkBytes} bytes."];
        if (string.IsNullOrWhiteSpace(contentType) || contentType.Length > 100
            || !contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            errors[nameof(contentType)] = ["Content type must be an audio media type."];
        if (errors.Count > 0)
            throw new ValidationException(errors);
    }

    private sealed class AudioUpload(string contentType)
    {
        public string ContentType { get; } = contentType;
        public List<byte[]> Chunks { get; } = [];
        public long BytesReceived { get; set; }
        public bool IsCompleted { get; set; }
        public DateTimeOffset? LastAccessedAt { get; set; }
        public string? Transcript { get; set; }

        public AudioChunkCheckpoint ToCheckpoint() => new(
            Chunks.Count, BytesReceived, ContentType, IsCompleted, Transcript);
    }
}
