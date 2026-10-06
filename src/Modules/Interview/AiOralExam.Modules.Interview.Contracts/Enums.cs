namespace AiOralExam.Modules.Interview.Contracts;

// Khop CHECK constraint trong database/01_schema.sql

/// <summary>Trang thai luot thi (interview_attempts.status).</summary>
public enum AttemptStatus
{
    /// <summary>Dang thi.</summary>
    InProgress,
    /// <summary>Thi xong, AI da goi y diem, cho giang vien duyet.</summary>
    PendingReview,
    /// <summary>Giang vien da chot diem - sinh vien moi xem duoc bao cao.</summary>
    Finalized
}

public enum TurnType
{
    /// <summary>Cau hoi chinh do giang vien soan.</summary>
    Main,
    /// <summary>Cau hoi xoay do AI sinh ra.</summary>
    FollowUp
}

public enum ReviewStatus
{
    Pending,
    Approved
}
