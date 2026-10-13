using System.Globalization;
using System.Text;
using AiOralExam.Modules.AccessConfig.Contracts;
using AiOralExam.Modules.Interview.Contracts;
using AiOralExam.Modules.Reporting.Application;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;

namespace AiOralExam.UnitTests;

public class ReportServiceTests
{
    [Fact]
    public async Task Student_pending_report_returns_metadata_without_scores_comments_or_dialogue()
    {
        var data = new ReportData();
        var report = await data.Service.GetAttemptReportAsync(data.Attempt.AttemptId);

        Assert.Equal("PendingReview", report.Status);
        Assert.Equal(data.StudentName, report.StudentName);
        Assert.Null(report.SuggestedTotalScore);
        Assert.Null(report.FinalScore);
        Assert.Null(report.LecturerComment);
        Assert.Empty(report.Questions);
    }

    [Fact]
    public async Task Student_cannot_read_another_students_report()
    {
        var data = new ReportData { Id = Guid.NewGuid() };
        var error = await Assert.ThrowsAsync<NotFoundException>(() =>
            data.Service.GetAttemptReportAsync(data.Attempt.AttemptId));
        Assert.Equal("attempt_not_found", error.Code);
    }

    [Fact]
    public async Task Approved_report_exposes_stored_scores_and_dialogue_in_order()
    {
        var data = new ReportData();
        data.Confirm(7);
        var report = await data.Service.GetAttemptReportAsync(data.Attempt.AttemptId);

        Assert.Equal("Finalized", report.Status);
        Assert.Equal(8m, report.SuggestedTotalScore);
        Assert.Equal(7m, report.FinalScore);
        Assert.Equal("Lecturer comment", report.LecturerComment);
        var question = Assert.Single(report.Questions);
        Assert.Equal("Stored AI feedback", question.AiFeedback);
        Assert.Equal(new[] { 1, 2 }, question.Turns.Select(t => t.TurnNo));
        Assert.Equal("Stored answer", question.Turns[0].AnswerTranscript);
        Assert.Equal("FollowUp", question.Turns[1].Type);
    }

    [Fact]
    public async Task Assigned_lecturer_can_read_pending_details_but_unassigned_lecturer_cannot()
    {
        var data = new ReportData { Role = Roles.Lecturer };
        Assert.Single((await data.Service.GetAttemptReportAsync(data.Attempt.AttemptId)).Questions);
        data.Assigned = false;
        await Assert.ThrowsAsync<ForbiddenException>(() => data.Service.GetAttemptReportAsync(data.Attempt.AttemptId));
        await Assert.ThrowsAsync<ForbiddenException>(() => data.Service.GetSessionStatisticsAsync(data.Session.Id));
        await Assert.ThrowsAsync<ForbiddenException>(() => data.Service.GetSessionResultsAsync(data.Session.Id));
        await Assert.ThrowsAsync<ForbiddenException>(() => data.Service.ExportGradeSheetCsvAsync(data.Session.Id));
    }

    [Fact]
    public async Task Missing_attempt_is_not_found()
    {
        var data = new ReportData();
        await Assert.ThrowsAsync<NotFoundException>(() => data.Service.GetAttemptReportAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Pending_review_does_not_release_a_draft_final_score()
    {
        var data = new ReportData();
        data.Attempt = data.Attempt with
        {
            Review = new(Guid.NewGuid(), 9, "Private draft", ReviewStatus.Pending, null)
        };
        var report = await data.Service.GetAttemptReportAsync(data.Attempt.AttemptId);
        Assert.Null(report.FinalScore);
        Assert.Null(report.LecturerComment);
        Assert.Empty(report.Questions);
    }

    [Fact]
    public async Task Grade_sheet_contains_only_confirmed_final_scores_and_handles_csv_text_safely()
    {
        var data = new ReportData
        {
            Role = Roles.Administrator,
            StudentName = "Nguyễn, \"An\"\r\nSinh viên",
            StudentCode = "  =1+1"
        };
        var (pendingBytes, _) = await data.Service.ExportGradeSheetCsvAsync(data.Session.Id);
        var pending = Encoding.UTF8.GetString(pendingBytes);
        Assert.True(pendingBytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
        Assert.Contains("No,StudentCode,FullName,Status,FinalScore,MaxScore\r\n", pending);
        Assert.Contains("\"'  =1+1\"", pending);
        Assert.Contains("\"Nguyễn, \"\"An\"\"\r\nSinh viên\"", pending);
        Assert.Contains(",PendingReview,,10\r\n", pending);
        Assert.DoesNotContain("AiSuggestedScore", pending);

        data.Confirm(9.5m);
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            var (bytes, fileName) = await data.Service.ExportGradeSheetCsvAsync(data.Session.Id);
            Assert.Contains(",Finalized,9.5,10\r\n", Encoding.UTF8.GetString(bytes));
            Assert.StartsWith("grade-sheet-SWD392-", fileName);
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }
    }

    [Fact]
    public async Task Student_cannot_read_class_results_statistics_or_export()
    {
        var data = new ReportData();
        await Assert.ThrowsAsync<ForbiddenException>(() => data.Service.GetSessionResultsAsync(data.Session.Id));
        await Assert.ThrowsAsync<ForbiddenException>(() => data.Service.GetSessionStatisticsAsync(data.Session.Id));
        await Assert.ThrowsAsync<ForbiddenException>(() => data.Service.ExportGradeSheetCsvAsync(data.Session.Id));
    }

    private sealed class ReportData : IInterviewResultsApi, IExamConfigurationApi, ICurrentUser
    {
        private static readonly Guid StudentId = Guid.NewGuid();
        public Guid Id { get; set; } = StudentId;
        public string? Role { get; set; } = Roles.Student;
        public bool IsAuthenticated => true;
        public bool IsInRole(string role) => Role == role;
        public bool Assigned { get; set; } = true;
        public string StudentName { get; set; } = "Nguyễn An";
        public string? StudentCode { get; set; } = "SE180001";
        public ExamSessionInfo Session { get; } = new(Guid.NewGuid(), Guid.NewGuid(), "SWD392", "Design", "Viva",
            DateTime.UtcNow, DateTime.UtcNow.AddHours(1), 120, 2, ExamSessionStatus.Closed,
            [new(Guid.NewGuid(), 1, "Main question", [], 10)]);
        public AttemptResult Attempt { get; set; }
        public ReportService Service => new(this, this, this);

        public ReportData()
        {
            var now = DateTime.UtcNow;
            Attempt = new(Guid.NewGuid(), Session.Id, StudentId, AttemptStatus.PendingReview, now, now, null,
                [new(Guid.NewGuid(), Session.Questions[0].Id, 1, true, 8, "Stored AI feedback",
                    [new(2, TurnType.FollowUp, "Follow-up question", "Follow-up answer", now, now),
                     new(1, TurnType.Main, "Main question", "Stored answer", now, now)])]);
        }

        public void Confirm(decimal score) => Attempt = Attempt with
        {
            Status = AttemptStatus.Finalized,
            Review = new(Guid.NewGuid(), score, "Lecturer comment", ReviewStatus.Approved, DateTime.UtcNow)
        };

        public Task<AttemptResult?> GetAttemptResultAsync(Guid attemptId, CancellationToken ct = default) =>
            Task.FromResult(attemptId == Attempt.AttemptId ? Attempt : null);
        public Task<IReadOnlyList<AttemptResult>> GetSessionResultsAsync(Guid examSessionId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AttemptResult>>(examSessionId == Session.Id ? [Attempt] : []);
        public Task<ExamSessionInfo?> GetSessionAsync(Guid examSessionId, CancellationToken ct = default) =>
            Task.FromResult(examSessionId == Session.Id ? Session : null);
        public Task<IReadOnlyList<ExamSessionInfo>> GetSessionsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ExamSessionInfo>>(ids.Contains(Session.Id) ? [Session] : []);
        public Task<bool> IsLecturerOfSessionAsync(Guid lecturerId, Guid examSessionId, CancellationToken ct = default) =>
            Task.FromResult(Assigned && examSessionId == Session.Id);
        public Task<IReadOnlyList<UserBasicInfo>> GetUsersAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UserBasicInfo>>(ids.Contains(StudentId)
                ? [new(StudentId, StudentName, "student@example.com", StudentCode)] : []);
        public Task<UserAccessInfo?> GetUserAccessAsync(Guid userId, CancellationToken ct = default) =>
            Task.FromResult<UserAccessInfo?>(null);
    }
}
