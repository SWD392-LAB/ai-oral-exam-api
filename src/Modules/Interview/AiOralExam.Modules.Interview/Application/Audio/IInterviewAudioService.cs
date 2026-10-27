using AiOralExam.Modules.Interview.Application;

namespace AiOralExam.Modules.Interview.Application.Audio;

public interface IInterviewAudioService
{
    Task<SubmitAnswerResponse> SubmitAudioAsync(
        Guid attemptId,
        Guid turnId,
        Stream audio,
        string contentType,
        long? declaredLength,
        CancellationToken ct = default);

    Task<StoredAudio> GetQuestionAudioAsync(Guid attemptId, Guid turnId, CancellationToken ct = default);
    Task<StoredAudio> GetAnswerAudioAsync(Guid attemptId, Guid turnId, CancellationToken ct = default);
}
