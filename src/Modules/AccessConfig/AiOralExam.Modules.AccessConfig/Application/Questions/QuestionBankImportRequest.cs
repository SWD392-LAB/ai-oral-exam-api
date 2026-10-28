namespace AiOralExam.Modules.AccessConfig.Application.Questions;

/// <summary>Draft import contract for a question-bank JSON batch.</summary>
public sealed record QuestionBankImportRequest(IReadOnlyList<QuestionBankImportItemRequest?>? Items);

/// <summary>One row in a question-bank JSON batch.</summary>
public sealed record QuestionBankImportItemRequest(string? Content, string? Topic, string? BloomLevel);
