using AiOralExam.Modules.AccessConfig.Contracts;
using AiOralExam.Modules.Interview.Contracts;
using AiOralExam.Modules.Interview.Domain;
using AiOralExam.Modules.Interview.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AiOralExam.Modules.Interview.Application.Audio;

internal sealed class InterviewAudioSubmissionService(
    InterviewDbContext db,
    IExamConfigurationApi examConfig,
    ICurrentUser currentUser,
    IAudioChunkStore audioStore,
    IInterviewService interview,
    TimeProvider clock,
    IOptions<InterviewOptions> options) : IInterviewAudioSubmissionService
{
    private readonly InterviewOptions _options = options.Value;

    public async Task<AudioChunkCheckpointDto> SendAudioChunkAsync(
        Guid attemptId,
        Guid turnId,
        int sequence,
        byte[] chunk,
        string contentType,
        CancellationToken ct = default)
    {
        ValidateChunk(sequence, chunk, contentType);
        await ValidateCurrentTurnAsync(attemptId, turnId, ct);

        var result = await audioStore.AppendAsync(attemptId, turnId, sequence, chunk, contentType, ct);
        return result.Status switch
        {
            AudioChunkAppendStatus.Appended => ToDto(result.Checkpoint, duplicate: false),
            AudioChunkAppendStatus.Duplicate => ToDto(result.Checkpoint, duplicate: true),
            AudioChunkAppendStatus.OutOfOrder => throw new ConflictException(
                "audio_chunk_out_of_order", $"Expected audio chunk sequence {result.Checkpoint.NextSequence}."),
            AudioChunkAppendStatus.ConflictingDuplicate => throw new ConflictException(
                "audio_chunk_duplicate_conflict", "The sequence was already used by a different audio chunk."),
            AudioChunkAppendStatus.UploadCompleted => throw new ConflictException(
                "audio_upload_completed", "The audio upload has already been completed."),
            AudioChunkAppendStatus.ContentTypeConflict => throw new ConflictException(
                "audio_content_type_conflict", "All chunks in an upload must use the same content type."),
            _ => throw new InvalidOperationException("Unknown audio chunk append status.")
        };
    }

    public async Task<AudioChunkCheckpointDto?> GetAudioCheckpointAsync(
        Guid attemptId,
        Guid turnId,
        CancellationToken ct = default)
    {
        await ValidateCurrentTurnAsync(attemptId, turnId, ct);
        var checkpoint = await audioStore.GetCheckpointAsync(attemptId, turnId, ct);
        return checkpoint is null ? null : ToDto(checkpoint, duplicate: false);
    }

    public async Task<SubmitAnswerResponse> CompleteAudioAnswerAsync(
        Guid attemptId,
        Guid turnId,
        CancellationToken ct = default)
    {
        var receiptAt = clock.GetUtcNow().UtcDateTime;
        return await interview.SubmitAudioAnswerAsync(attemptId, turnId, receiptAt, ct);
    }

    private async Task ValidateCurrentTurnAsync(Guid attemptId, Guid turnId, CancellationToken ct)
    {
        var (attempt, turn) = await LoadOwnedTurnAsync(attemptId, turnId, ct);
        if (attempt.Status != AttemptStatus.InProgress)
            throw new ConflictException("attempt_not_in_progress", "The attempt is no longer in progress.");

        var currentTurn = attempt.CurrentResponse?.CurrentTurn;
        if (currentTurn is null || currentTurn.Id != turnId)
            throw new ConflictException("turn_not_current", "The submitted turn is no longer current.");

        var session = await examConfig.GetSessionAsync(attempt.Participant.ExamSessionId, ct)
            ?? throw new NotFoundException("exam_session_not_found", "Exam session not found.");
        var now = clock.GetUtcNow().UtcDateTime;
        var deadline = turn.AskedAt.AddSeconds(
            session.TimeLimitPerAnswerSeconds + Math.Max(0, _options.TimeLimitGraceSeconds));
        if (!session.IsOpenAt(now) || now > session.EndTime || now > deadline)
            throw new ConflictException("answer_deadline_passed", "The answer deadline has passed.");
    }

    private async Task<(InterviewAttempt Attempt, InterviewTurn Turn)> LoadOwnedTurnAsync(
        Guid attemptId, Guid turnId, CancellationToken ct)
    {
        if (!currentUser.IsAuthenticated)
            throw new UnauthorizedException("unauthenticated", "You need to sign in.");
        if (!currentUser.IsInRole(Roles.Student))
            throw new ForbiddenException("Only the student taking the attempt can submit audio.");

        var attempt = await db.InterviewAttempts.AsNoTracking()
            .Include(x => x.Participant)
            .Include(x => x.Responses).ThenInclude(x => x.Turns)
            .AsSplitQuery()
            .FirstOrDefaultAsync(x => x.Id == attemptId, ct)
            ?? throw new NotFoundException("attempt_not_found", "Attempt not found.");
        if (attempt.Participant.StudentId != currentUser.Id)
            throw new NotFoundException("attempt_not_found", "Attempt not found.");
        var turn = attempt.Responses.SelectMany(response => response.Turns)
            .FirstOrDefault(candidate => candidate.Id == turnId)
            ?? throw new ConflictException("turn_not_current", "The submitted turn does not belong to this attempt.");
        return (attempt, turn);
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

    private static AudioChunkCheckpointDto ToDto(AudioChunkCheckpoint checkpoint, bool duplicate) =>
        new(checkpoint.NextSequence, checkpoint.BytesReceived, checkpoint.IsCompleted, duplicate);
}
