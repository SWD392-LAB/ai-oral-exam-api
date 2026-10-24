using System.ComponentModel.DataAnnotations;

namespace AiOralExam.Modules.Interview.Application;

public sealed record StartAttemptRequest([Required] Guid ExamSessionId);
