namespace AiOralExam.Modules.AccessConfig.Contracts;

/// <summary>Matches the CHECK constraint on exam_sessions.status.</summary>
public enum ExamSessionStatus
{
    Draft,
    Published,
    Closed
}
