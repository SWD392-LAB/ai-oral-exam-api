using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Application.Materials;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;

namespace AiOralExam.Modules.AccessConfig.Application.QuestionGeneration;

/// <summary>
/// F1-C demo generator. It intentionally uses only supplied source excerpts,
/// emits citations for those excerpts and never auto-approves or writes rubric.
/// The controller persists the resulting candidates as Draft bank items.
/// </summary>
internal sealed class DeterministicQuestionGenerator(
    AccessConfigDbContext db,
    ICurrentUser currentUser,
    IMaterialService materials) : IQuestionGenerator
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

        var excerptSources = request.SourceExcerpts ?? [];
        var materialSources = request.SourceMaterialIds is { Count: > 0 }
            ? await materials.RetrieveAsync(courseId, request.SourceMaterialIds, request.Topic, ct)
            : [];
        var sources = excerptSources
            .Select((text, index) => (Text: text?.Trim() ?? string.Empty, Index: index, Citation: $"excerpt-{index + 1}"))
            .Where(x => x.Text.Length > 0)
            .Select(x => (x.Text, x.Index, x.Citation))
            .Concat(materialSources.Select(x => (Text: x.Text, Index: x.ChunkNo, Citation: x.Citation)))
            .ToList();
        if (sources.Count == 0)
            throw new ConflictException("insufficient_source", "No usable source excerpt was supplied.");

        var drafts = Enumerable.Range(0, request.Count)
            .Select(index =>
            {
                var source = sources[index % sources.Count];
                var content = BuildQuestion(request.Topic.Trim(), request.Bloom, index + 1);
                return new GeneratedQuestionDraft(
                    Guid.NewGuid(), index + 1, content, request.Bloom, "Draft", request.PlatformRubricVersionId,
                    [new QuestionCitation(source.Citation, source.Index, TrimCitation(source.Text))]);
            })
            .ToList();

        return new QuestionGenerationResult(courseId, "DeterministicDemo", "Draft", drafts);
    }

    private static string BuildQuestion(string topic, BloomLevel bloom, int number) => bloom switch
    {
        BloomLevel.Remember => $"[{number}] Define the key concepts of {topic} and state one important property of each.",
        BloomLevel.Understand => $"[{number}] Explain {topic} in your own words and give a concrete example.",
        BloomLevel.Apply => $"[{number}] Given a practical scenario involving {topic}, describe how you would apply the relevant method step by step.",
        BloomLevel.Analyze => $"[{number}] Analyze {topic}: compare two possible approaches, identify their trade-offs and justify your choice.",
        _ => throw new ArgumentOutOfRangeException(nameof(bloom))
    };

    private static string TrimCitation(string source) => source.Length <= 300 ? source : source[..300] + "…";
}
