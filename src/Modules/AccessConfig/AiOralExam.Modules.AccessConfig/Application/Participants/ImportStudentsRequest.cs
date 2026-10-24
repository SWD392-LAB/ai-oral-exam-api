using System.ComponentModel.DataAnnotations;

namespace AiOralExam.Modules.AccessConfig.Application.Participants;

public sealed record ImportStudentsRequest(
    [Required] IReadOnlyList<string> StudentCodes);
