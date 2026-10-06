namespace AiOralExam.Modules.AccessConfig.Contracts;

/// <summary>
/// The only way for other modules to read F7 data
/// (exam sessions, questions, rubrics, users, lecturer assignments).
/// </summary>
public interface IExamConfigurationApi
{
    /// <summary>Exam session with its questions + rubrics, ordered by OrderNo. Null when it does not exist.</summary>
    Task<ExamSessionInfo?> GetSessionAsync(Guid examSessionId, CancellationToken ct = default);

    /// <summary>Several exam sessions at once (for lists). Unknown ids are skipped.</summary>
    Task<IReadOnlyList<ExamSessionInfo>> GetSessionsAsync(IReadOnlyCollection<Guid> examSessionIds, CancellationToken ct = default);

    /// <summary>Whether the lecturer is assigned to the course of this exam session.</summary>
    Task<bool> IsLecturerOfSessionAsync(Guid lecturerId, Guid examSessionId, CancellationToken ct = default);

    Task<IReadOnlyList<UserBasicInfo>> GetUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);
}
