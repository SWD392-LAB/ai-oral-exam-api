using AiOralExam.Modules.AccessConfig.Application.Rubrics;

namespace AiOralExam.Modules.AccessConfig.Application.RubricVersions;

public sealed record CreateRubricVersionRequest(IReadOnlyList<CreateRubricCriterionRequest>? Criteria);

public sealed record CreateRubricCriterionRequest(int OrderNo, string Description, decimal MaxPoints);

public sealed record RubricVersionDto(
    Guid Id,
    Guid TemplateId,
    Guid? CourseId,
    int VersionNo,
    decimal MaxScore,
    bool IsSealed,
    bool IsActive,
    Guid CreatedBy,
    DateTime CreatedAt);

public sealed record RubricVersionDetailDto(
    Guid Id,
    Guid TemplateId,
    Guid? CourseId,
    int VersionNo,
    decimal MaxScore,
    bool IsSealed,
    bool IsActive,
    Guid CreatedBy,
    DateTime CreatedAt,
    IReadOnlyList<RubricCriterionDto> Criteria);
