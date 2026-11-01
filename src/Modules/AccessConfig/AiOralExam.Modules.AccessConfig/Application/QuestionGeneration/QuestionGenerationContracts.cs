using System.ComponentModel.DataAnnotations;

namespace AiOralExam.Modules.AccessConfig.Application.QuestionGeneration;

public sealed record QuestionGenerationRequest(
    [Required, MaxLength(200)] string Topic,
    BloomLevel Bloom,
    [Range(1, 20)] int Count,
    IReadOnlyList<string>? SourceExcerpts,
    Guid PlatformRubricVersionId,
    IReadOnlyList<Guid>? SourceMaterialIds = null);

public sealed record QuestionCitation(
    string SourceId,
    int SourceIndex,
    string Quote);

public sealed record GeneratedQuestionDraft(
    Guid DraftId,
    int OrderNo,
    string Content,
    BloomLevel Bloom,
    string Status,
    Guid PlatformRubricVersionId,
    IReadOnlyList<QuestionCitation> Citations);

public sealed record QuestionGenerationResult(
    Guid CourseId,
    string Provider,
    string Status,
    IReadOnlyList<GeneratedQuestionDraft> Drafts);

public interface IQuestionGenerator
{
    Task<QuestionGenerationResult> GenerateAsync(
        Guid courseId,
        QuestionGenerationRequest request,
        CancellationToken ct = default);
}
