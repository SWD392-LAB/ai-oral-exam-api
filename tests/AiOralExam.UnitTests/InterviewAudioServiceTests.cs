using AiOralExam.Modules.AccessConfig.Contracts;
using AiOralExam.Modules.Interview.Application;
using AiOralExam.Modules.Interview.Application.Ai;
using AiOralExam.Modules.Interview.Application.Audio;
using AiOralExam.Modules.Interview.Contracts;
using AiOralExam.Modules.Interview.Domain;
using AiOralExam.Modules.Interview.Infrastructure;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiOralExam.UnitTests;

public sealed class InterviewAudioServiceTests
{
    [Fact]
    public async Task Failed_audio_reference_save_deletes_only_the_new_upload()
    {
        var studentId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        const string oldAudioKey = "existing/accepted-audio.webm";
        var interceptor = new FailAudioReferenceSaveInterceptor();
        var options = new DbContextOptionsBuilder<InterviewDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(interceptor)
            .Options;
        await using var db = new InterviewDbContext(options);
        var participant = new Participant
        {
            Id = Guid.NewGuid(),
            ExamSessionId = Guid.NewGuid(),
            StudentId = studentId
        };
        var attempt = new InterviewAttempt
        {
            Id = attemptId,
            ParticipantId = participant.Id,
            Participant = participant,
            StartedAt = DateTime.UtcNow
        };
        var response = new QuestionResponse
        {
            Id = Guid.NewGuid(),
            AttemptId = attemptId,
            QuestionId = Guid.NewGuid()
        };
        var turn = new InterviewTurn
        {
            Id = turnId,
            QuestionResponseId = response.Id,
            TurnNo = 1,
            Type = TurnType.Main,
            QuestionText = "Explain the topic.",
            AudioUrl = oldAudioKey,
            AskedAt = DateTime.UtcNow
        };
        response.Turns.Add(turn);
        attempt.Responses.Add(response);
        participant.Attempts.Add(attempt);
        db.Participants.Add(participant);
        db.InterviewAttempts.Add(attempt);
        db.QuestionResponses.Add(response);
        db.InterviewTurns.Add(turn);
        await db.SaveChangesAsync();

        var storage = new MemoryAudioStorage();
        storage.Files.Add(oldAudioKey);
        interceptor.FailAudioReferenceWrites = true;
        var service = new InterviewAudioService(
            db,
            new StubInterviewService(),
            examConfig: null!,
            new StubSpeechToTextService(),
            textToSpeech: null!,
            storage,
            new TestCurrentUser(studentId),
            Options.Create(new AiProviderOptions()),
            NullLogger<InterviewAudioService>.Instance);

        await Assert.ThrowsAsync<DbUpdateException>(() => service.SubmitAudioAsync(
            attemptId, turnId, new MemoryStream([1, 2, 3]), "audio/webm;codecs=opus", 3));

        var savedTurn = await db.InterviewTurns.SingleAsync(x => x.Id == turnId);
        Assert.Equal(oldAudioKey, savedTurn.AudioUrl);
        Assert.Contains(oldAudioKey, storage.Files);
        Assert.Single(storage.DeletedKeys);
        Assert.DoesNotContain(storage.DeletedKeys[0], storage.Files);
    }

    private sealed class FailAudioReferenceSaveInterceptor : SaveChangesInterceptor
    {
        public bool FailAudioReferenceWrites { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var audioReferenceChanged = eventData.Context?.ChangeTracker.Entries<InterviewTurn>()
                .Any(entry => entry.State == EntityState.Modified
                    && entry.Property(turn => turn.AudioUrl).IsModified) == true;
            if (FailAudioReferenceWrites && audioReferenceChanged)
                return ValueTask.FromException<InterceptionResult<int>>(
                    new DbUpdateException("Simulated audio-reference persistence failure."));
            return ValueTask.FromResult(result);
        }
    }

    private sealed class MemoryAudioStorage : IAudioStorage
    {
        private int _uploadNo;
        public HashSet<string> Files { get; } = [];
        public List<string> DeletedKeys { get; } = [];

        public Task<string> PutAsync(Guid attemptId, Guid turnId, Stream content, string contentType, CancellationToken ct = default)
        {
            var key = $"{attemptId:N}/{turnId:N}/{++_uploadNo}.webm";
            Files.Add(key);
            return Task.FromResult(key);
        }

        public Task<StoredAudio?> OpenReadAsync(string key, CancellationToken ct = default) => Task.FromResult<StoredAudio?>(null);

        public Task DeleteAsync(string key, CancellationToken ct = default)
        {
            DeletedKeys.Add(key);
            Files.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class StubInterviewService : IInterviewService
    {
        public Task<IReadOnlyList<MySessionDto>> GetMySessionsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(AttemptStateDto State, bool Created)> StartAttemptAsync(StartAttemptRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SubmitAnswerResponse> SubmitAnswerAsync(Guid attemptId, SubmitAnswerRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AttemptStateDto> GetAttemptStateAsync(Guid attemptId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class StubSpeechToTextService : ISpeechToTextService
    {
        public Task<string> TranscribeAsync(Stream audio, string contentType, string language, CancellationToken ct = default) =>
            Task.FromResult("recognized answer");
    }
    private sealed class TestCurrentUser(Guid id) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid Id => id;
        public string? Role => Roles.Student;
        public bool IsInRole(string requested) => string.Equals(requested, Roles.Student, StringComparison.Ordinal);
    }
}
