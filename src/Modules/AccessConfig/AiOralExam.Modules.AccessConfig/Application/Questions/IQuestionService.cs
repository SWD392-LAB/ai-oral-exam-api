namespace AiOralExam.Modules.AccessConfig.Application.Questions;

public interface IQuestionService
{
    Task<IReadOnlyList<QuestionDto>> ListAsync(Guid sessionId, CancellationToken ct);
    Task<QuestionDto> GetAsync(Guid sessionId, Guid id, CancellationToken ct);
    Task<QuestionDto> CreateAsync(Guid sessionId, CreateQuestionRequest r, CancellationToken ct);
    Task<IReadOnlyList<QuestionDto>> ImportAsync(Guid sessionId, ImportQuestionsRequest r, CancellationToken ct);
    Task<QuestionDto> UpdateAsync(Guid sessionId, Guid id, UpdateQuestionRequest r, CancellationToken ct);
    Task DeleteAsync(Guid sessionId, Guid id, CancellationToken ct);
}
