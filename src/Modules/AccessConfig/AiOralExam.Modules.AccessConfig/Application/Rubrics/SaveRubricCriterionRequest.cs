using System.ComponentModel.DataAnnotations;

namespace AiOralExam.Modules.AccessConfig.Application.Rubrics;

public sealed record SaveRubricCriterionRequest(
    Guid? Id,
    [Range(1, int.MaxValue)] int OrderNo,
    [Required] string Description,
    [Range(0, 999.99)] decimal MaxPoints);
