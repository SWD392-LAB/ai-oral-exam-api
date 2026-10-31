using AiOralExam.Modules.AccessConfig.Application.Materials;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace AiOralExam.Modules.AccessConfig.Application.QuestionGeneration;

/// <summary>Resolves course-scoped evidence, delegates draft composition, and verifies citations.</summary>
internal sealed class QuestionGenerator(
    AccessConfigDbContext db,
    ICurrentUser currentUser,
    IMaterialService materials,
    IQuestionDraftContentGenerator draftContent,
    IOptions<QuestionGenerationProviderOptions> providerOptions) : IQuestionGenerator
{
    public async Task<QuestionGenerationResult> GenerateAsync(
        Guid courseId,
        QuestionGenerationRequest request,
        CancellationToken ct = default)
    {
        if (request.PlatformRubricVersionId == Guid.Empty)
            throw new ValidationException(new Dictionary<string, string[]> { ["platformRubricVersionId"] = ["A platform rubric version is required."] });
        var requestErrors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Topic)) requestErrors["topic"] = ["A topic is required."];
        if (request.Count is < 1 or > 20) requestErrors["count"] = ["Count must be between 1 and 20."];
        if (!Enum.IsDefined(request.Bloom)) requestErrors["bloom"] = ["Bloom must be Remember, Understand, Apply, or Analyze."];
        if (requestErrors.Count > 0) throw new ValidationException(requestErrors);

        var course = await db.Courses.AsNoTracking()
            .Include(x => x.Lecturers)
            .FirstOrDefaultAsync(x => x.Id == courseId, ct)
            ?? throw new NotFoundException("course_not_found", "Course not found.");
        if (!currentUser.IsInRole(Roles.Administrator) && course.Lecturers.All(x => x.LecturerId != currentUser.Id))
            throw new ForbiddenException("You are not assigned to this course.");

        var evidence = (request.SourceExcerpts ?? [])
            .Select((text, index) => new QuestionSourceEvidence(
                $"excerpt-{index + 1}", index + 1, $"excerpt-{index + 1}", text?.Trim() ?? string.Empty))
            .Where(x => !string.IsNullOrWhiteSpace(x.Text))
            .Concat((request.SourceMaterialIds is { Count: > 0 }
                    ? await materials.RetrieveAsync(courseId, request.SourceMaterialIds, request.Topic, ct)
                    : [])
                .Select(x => new QuestionSourceEvidence(
                    $"material:{x.MaterialId:N}:{x.ChunkNo}", x.ChunkNo, x.Citation, x.Text)))
            .ToList();

        if (evidence.Count == 0)
            throw new ConflictException("insufficient_source", "No usable source excerpt was supplied.");

        IReadOnlyList<QuestionDraftSuggestion> suggestions;
        try
        {
            suggestions = await draftContent.GenerateAsync(request, evidence, ct);
        }
        catch (AppException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new AppException(StatusCodes.Status503ServiceUnavailable, "question_generation_failed", "AI question generation failed. The source material is unchanged; retry the request.");
        }

        var evidenceById = evidence.ToDictionary(x => x.SourceId, StringComparer.Ordinal);
        var seenQuestions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (suggestions.Count != request.Count)
            throw GenerationOutputError();

        var drafts = new List<GeneratedQuestionDraft>(request.Count);
        for (var index = 0; index < suggestions.Count; index++)
        {
            var suggestion = suggestions[index];
            if (string.IsNullOrWhiteSpace(suggestion.Content) || suggestion.Content.Length > 2000
                || string.IsNullOrWhiteSpace(suggestion.SourceId) || string.IsNullOrWhiteSpace(suggestion.Quote)
                || !evidenceById.TryGetValue(suggestion.SourceId, out var source)
                || !ContainsVerbatim(source.Text, suggestion.Quote))
                throw GenerationOutputError();

            var normalizedQuestion = string.Join(' ', suggestion.Content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (!seenQuestions.Add(normalizedQuestion)) throw GenerationOutputError();

            drafts.Add(new GeneratedQuestionDraft(
                Guid.NewGuid(), index + 1, normalizedQuestion, request.Bloom, "Draft", request.PlatformRubricVersionId,
                [new QuestionCitation(source.Citation, source.SourceIndex, suggestion.Quote.Trim())]));
        }

        var provider = providerOptions.Value.UsesGemini ? "Gemini" : draftContent.ProviderName;
        return new QuestionGenerationResult(courseId, provider, "Draft", drafts);
    }

    private static bool ContainsVerbatim(string source, string quote)
    {
        static string Normalize(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return Normalize(source).Contains(Normalize(quote), StringComparison.OrdinalIgnoreCase);
    }

    private static AppException GenerationOutputError() =>
        new(StatusCodes.Status503ServiceUnavailable, "question_generation_invalid_output", "The question generator returned invalid content or unverifiable citations. Retry the request.");
}
