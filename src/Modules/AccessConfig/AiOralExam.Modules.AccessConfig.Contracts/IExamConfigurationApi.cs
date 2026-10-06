namespace AiOralExam.Modules.AccessConfig.Contracts;

/// <summary>
/// Cong giao tiep duy nhat de module khac doc du lieu cua F7
/// (phien thi, cau hoi, rubric, nguoi dung, phan cong giang vien).
/// </summary>
public interface IExamConfigurationApi
{
    /// <summary>Phien thi kem cau hoi + rubric, sap xep theo OrderNo. Null neu khong ton tai.</summary>
    Task<ExamSessionInfo?> GetSessionAsync(Guid examSessionId, CancellationToken ct = default);

    /// <summary>Nhieu phien thi cung luc (dung cho danh sach). Bo qua id khong ton tai.</summary>
    Task<IReadOnlyList<ExamSessionInfo>> GetSessionsAsync(IReadOnlyCollection<Guid> examSessionIds, CancellationToken ct = default);

    /// <summary>Giang vien co duoc phan vao mon cua phien thi nay khong.</summary>
    Task<bool> IsLecturerOfSessionAsync(Guid lecturerId, Guid examSessionId, CancellationToken ct = default);

    Task<IReadOnlyList<UserBasicInfo>> GetUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);
}
