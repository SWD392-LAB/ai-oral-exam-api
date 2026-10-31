using System.ComponentModel.DataAnnotations;

namespace AiOralExam.Modules.AccessConfig.Application.RubricTemplates;

public sealed record CreateRubricTemplateRequest(
    [Required, MaxLength(200)] string Name,
    Guid? CourseId = null);

public sealed record UpdateRubricTemplateRequest(
    [Required, MaxLength(200)] string Name);

public sealed record RubricTemplateDto(
    Guid Id,
    Guid? CourseId,
    string? CourseCode,
    string? CourseName,
    string Name,
    Guid CreatedBy,
    DateTime CreatedAt,
    int VersionCount);
