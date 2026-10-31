using System.ComponentModel.DataAnnotations;
using AiOralExam.Modules.AccessConfig.Application.QuestionGeneration;

namespace AiOralExam.Modules.AccessConfig.Application.QuestionBank;

/// <summary>Creates a manual question-bank item as a Draft.</summary>
public sealed record CreateQuestionBankItemRequest(
    [Required, MaxLength(200)] string Topic,
    BloomLevel Bloom,
    [Required, MaxLength(10000)] string Content);

/// <summary>Replaces the current Draft content and creates a new immutable revision.</summary>
public sealed record UpdateQuestionBankItemRequest(
    [Required, MaxLength(200)] string Topic,
    BloomLevel Bloom,
    [Required, MaxLength(10000)] string Content);

/// <summary>One question row in an atomic bank import.</summary>
public sealed record ImportQuestionBankItem(
    [Required, MaxLength(200)] string Topic,
    BloomLevel Bloom,
    [Required, MaxLength(10000)] string Content);

/// <summary>All rows to import as Drafts for one course.</summary>
public sealed record ImportQuestionBankRequest(
    [Required, MinLength(1)] IReadOnlyList<ImportQuestionBankItem> Items);

/// <summary>Approved bank item IDs to attach to a Draft session, in desired question order.</summary>
public sealed record AttachQuestionBankRequest(
    [Required, MinLength(1)] IReadOnlyList<Guid> ItemIds);

/// <summary>An immutable content revision of a question-bank item.</summary>
public sealed record QuestionBankRevisionDto(
    Guid Id,
    int RevisionNo,
    string Content,
    string? ProvenanceJson,
    DateTime CreatedAt);

/// <summary>A course-scoped question-bank item and its revision history.</summary>
public sealed record QuestionBankItemDto(
    Guid Id,
    Guid CourseId,
    string Topic,
    string Bloom,
    string Content,
    string Origin,
    string Status,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<QuestionBankRevisionDto> Revisions);

public interface IQuestionBankService
{
    Task<IReadOnlyList<QuestionBankItemDto>> ListAsync(Guid courseId, QuestionBankStatusFilter? status, CancellationToken ct);
    Task<QuestionBankItemDto> GetAsync(Guid courseId, Guid itemId, CancellationToken ct);
    Task<QuestionBankItemDto> CreateAsync(Guid courseId, CreateQuestionBankItemRequest request, CancellationToken ct);
    Task<IReadOnlyList<QuestionBankItemDto>> ImportAsync(Guid courseId, ImportQuestionBankRequest request, CancellationToken ct);
    Task<QuestionBankItemDto> UpdateDraftAsync(Guid courseId, Guid itemId, UpdateQuestionBankItemRequest request, CancellationToken ct);
    Task<QuestionBankItemDto> ApproveAsync(Guid courseId, Guid itemId, CancellationToken ct);
    Task<QuestionBankItemDto> RejectAsync(Guid courseId, Guid itemId, CancellationToken ct);
    Task<IReadOnlyList<QuestionBankItemDto>> CreateAiDraftsAsync(Guid courseId, string topic, IReadOnlyList<GeneratedQuestionDraft> drafts, IReadOnlyList<Guid>? sourceMaterialIds, CancellationToken ct);
    Task<IReadOnlyList<Guid>> AttachApprovedAsync(Guid sessionId, AttachQuestionBankRequest request, CancellationToken ct);
}

public enum QuestionBankStatusFilter
{
    Draft,
    Approved,
    Rejected,
    Archived
}
