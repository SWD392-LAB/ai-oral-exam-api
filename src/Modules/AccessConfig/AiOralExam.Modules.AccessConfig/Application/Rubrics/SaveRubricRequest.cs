using System.ComponentModel.DataAnnotations;

namespace AiOralExam.Modules.AccessConfig.Application.Rubrics;

/// <summary>The criteria list replaces the existing list; omitted criterion IDs are removed.</summary>
public sealed record SaveRubricRequest([Required] IReadOnlyList<SaveRubricCriterionRequest> Criteria);
