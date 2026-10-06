namespace AiOralExam.Modules.Interview.Application;

/// <summary>Section "Interview" trong appsettings.</summary>
public sealed class InterviewOptions
{
    public const string SectionName = "Interview";

    /// <summary>So giay du them sau khi het gio (bu tre mang) truoc khi coi la het gio.</summary>
    public int TimeLimitGraceSeconds { get; set; } = 15;

    /// <summary>
    /// M1: sinh vien thay diem AI ngay sau moi cau (gate M1).
    /// M3 tro di nen dat false: sinh vien chi thay diem khi giang vien da chot.
    /// </summary>
    public bool ShowAiScoreToStudent { get; set; } = true;

    /// <summary>Cho phep thi lai khi da co luot thi ket thuc (open point - mac dinh khong).</summary>
    public bool AllowRetake { get; set; }

    public MockOptions Mock { get; set; } = new();

    public sealed class MockOptions
    {
        /// <summary>M1 chua co hoi xoay -> false. Bat len de thu luong FOLLOW_UP voi mock.</summary>
        public bool EnableFollowUps { get; set; }
    }
}
