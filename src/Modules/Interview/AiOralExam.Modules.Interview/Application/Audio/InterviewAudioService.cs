using AiOralExam.Modules.Interview.Application.Ai;
using AiOralExam.Modules.Interview.Domain;
using AiOralExam.Modules.Interview.Infrastructure;
using AiOralExam.Modules.AccessConfig.Contracts;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiOralExam.Modules.Interview.Application.Audio;

internal sealed class InterviewAudioService(
    InterviewDbContext db,
    IInterviewService interview,
    IExamConfigurationApi examConfig,
    ISpeechToTextService speechToText,
    ITextToSpeechService textToSpeech,
    IAudioStorage storage,
    ICurrentUser currentUser,
    IOptions<AiProviderOptions> providerOptions,
    ILogger<InterviewAudioService> logger) : IInterviewAudioService
{
    private readonly AiProviderOptions _options = providerOptions.Value;

    public async Task<SubmitAnswerResponse> SubmitAudioAsync(
        Guid attemptId,
        Guid turnId,
        Stream audio,
        string contentType,
        long? declaredLength,
        CancellationToken ct = default)
    {
        if (!currentUser.IsInRole(Roles.Student))
            throw new ForbiddenException("Only the student who owns the attempt can submit audio.");
        if (turnId == Guid.Empty)
            throw new ValidationException(new Dictionary<string, string[]> { ["turnId"] = ["turnId is required."] });
        ValidateContentType(contentType);
        if (declaredLength is > 0 && declaredLength > _options.MaxAudioBytes)
            throw TooLarge();

        var attempt = await LoadAttemptAsync(attemptId, ct);
        if (attempt.Participant.StudentId != currentUser.Id)
            throw new NotFoundException("attempt_not_found", "Attempt not found.");
        attempt.EnsureInProgress();

        var currentTurn = attempt.CurrentResponse?.CurrentTurn;
        if (currentTurn is null || currentTurn.Id != turnId)
            throw new ConflictException("turn_not_current", "The uploaded audio does not belong to the current turn.");

        await using var buffer = new MemoryStream();
        await audio.CopyToAsync(buffer, ct);
        if (buffer.Length == 0) throw new ValidationException(new Dictionary<string, string[]> { ["file"] = ["Audio file cannot be empty."] });
        if (buffer.Length > _options.MaxAudioBytes) throw TooLarge();

        buffer.Position = 0;
        var key = await storage.PutAsync(attemptId, turnId, buffer, contentType, ct);
        var previousKey = currentTurn.AudioUrl;
        currentTurn.AudioUrl = key;
        try
        {
            // Commit the new immutable object reference before invoking STT/LLM.
            // The previous recording remains available until this save succeeds.
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            currentTurn.AudioUrl = previousKey;
            try
            {
                await storage.DeleteAsync(key, CancellationToken.None);
            }
            catch (Exception cleanupException)
            {
                logger.LogWarning(cleanupException, "Could not remove unreferenced audio upload {AudioKey}", key);
            }
            throw;
        }

        // A later successful upload for the same current turn replaces the
        // selected recording. Delete the old object only after its replacement
        // has been committed in the database.
        if (!string.IsNullOrWhiteSpace(previousKey) && !string.Equals(previousKey, key, StringComparison.Ordinal))
        {
            try
            {
                await storage.DeleteAsync(previousKey, CancellationToken.None);
            }
            catch (Exception cleanupException)
            {
                logger.LogWarning(cleanupException, "Could not remove replaced audio upload {AudioKey}", previousKey);
            }
        }

        buffer.Position = 0;
        string transcript;
        try
        {
            transcript = await speechToText.TranscribeAsync(buffer, contentType, _options.SpeechLanguage, ct);
        }
        catch (AiEvaluationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Speech transcription failed for attempt {AttemptId} turn {TurnId}", attemptId, turnId);
            throw new AiEvaluationException("Speech transcription provider failed.");
        }

        if (string.IsNullOrWhiteSpace(transcript))
            throw new AiEvaluationException("Speech transcription returned an empty transcript.");

        var result = await interview.SubmitAnswerAsync(attemptId, new SubmitAnswerRequest(transcript, turnId), ct);
        return result with { Transcript = transcript };
    }

    public async Task<StoredAudio> GetQuestionAudioAsync(Guid attemptId, Guid turnId, CancellationToken ct = default)
    {
        var attempt = await LoadAttemptAsync(attemptId, ct);
        await EnsureCanReadAsync(attempt, ct);
        var turn = FindTurn(attempt, turnId);

        try
        {
            var (audio, contentType) = await textToSpeech.SynthesizeAsync(turn.QuestionText, _options.SpeechLanguage, ct);
            return new StoredAudio(new MemoryStream(audio, writable: false), contentType);
        }
        catch (AiEvaluationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Text-to-speech failed for attempt {AttemptId} turn {TurnId}", attemptId, turnId);
            throw new AiEvaluationException("Text-to-speech provider failed.");
        }
    }

    public async Task<StoredAudio> GetAnswerAudioAsync(Guid attemptId, Guid turnId, CancellationToken ct = default)
    {
        var attempt = await LoadAttemptAsync(attemptId, ct);
        await EnsureCanReadAsync(attempt, ct);
        var turn = FindTurn(attempt, turnId);
        if (string.IsNullOrWhiteSpace(turn.AudioUrl))
            throw new NotFoundException("answer_audio_not_found", "No audio recording is stored for this turn.");

        return await storage.OpenReadAsync(turn.AudioUrl, ct)
               ?? throw new NotFoundException("answer_audio_not_found", "Audio recording not found.");
    }

    private async Task<InterviewAttempt> LoadAttemptAsync(Guid attemptId, CancellationToken ct) =>
        await db.InterviewAttempts
            .Include(x => x.Participant)
            .Include(x => x.Responses).ThenInclude(x => x.Turns)
            .AsSplitQuery()
            .FirstOrDefaultAsync(x => x.Id == attemptId, ct)
        ?? throw new NotFoundException("attempt_not_found", "Attempt not found.");

    private async Task EnsureCanReadAsync(InterviewAttempt attempt, CancellationToken ct)
    {
        if (currentUser.IsInRole(Roles.Student))
        {
            if (attempt.Participant.StudentId != currentUser.Id)
                throw new NotFoundException("attempt_not_found", "Attempt not found.");
            return;
        }
        if (currentUser.IsInRole(Roles.Administrator))
            return;
        if (currentUser.IsInRole(Roles.Lecturer) &&
            await examConfig.IsLecturerOfSessionAsync(currentUser.Id, attempt.Participant.ExamSessionId, ct))
            return;
        throw new ForbiddenException("You do not have permission to read this audio.");
    }

    private static InterviewTurn FindTurn(InterviewAttempt attempt, Guid turnId) =>
        attempt.Responses.SelectMany(x => x.Turns).FirstOrDefault(x => x.Id == turnId)
        ?? throw new NotFoundException("turn_not_found", "Turn not found.");

    private static void ValidateContentType(string contentType)
    {
        var accepted = new[] { "audio/webm", "audio/wav", "audio/x-wav", "audio/mpeg", "audio/mp3", "audio/ogg", "audio/mp4" };
        var mediaType = contentType.Split(';', 2)[0].Trim();
        if (!accepted.Contains(mediaType, StringComparer.OrdinalIgnoreCase))
            throw new ValidationException(new Dictionary<string, string[]> { ["file"] = ["Unsupported audio format."] });
    }

    private static ValidationException TooLarge() =>
        new(new Dictionary<string, string[]> { ["file"] = ["Audio file is too large."] });
}
