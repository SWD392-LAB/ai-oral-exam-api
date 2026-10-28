using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiOralExam.IntegrationTests.Infrastructure;
using AiOralExam.Modules.AccessConfig.Contracts;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.Modules.Interview.Application;
using AiOralExam.Modules.Interview.Contracts;
using AiOralExam.Modules.Interview.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using AccessDomain = AiOralExam.Modules.AccessConfig.Domain;
using InterviewDomain = AiOralExam.Modules.Interview.Domain;

namespace AiOralExam.IntegrationTests.Http;

public sealed class ScoreReviewHttpContractTests
{
    private static readonly Guid LecturerId = Guid.Parse("4d3b69f1-0d6e-4e1b-b89c-2146e355e101");
    private static readonly Guid StudentId = Guid.Parse("4d3b69f1-0d6e-4e1b-b89c-2146e355e102");
    private static readonly Guid CourseId = Guid.Parse("4d3b69f1-0d6e-4e1b-b89c-2146e355e103");
    private static readonly Guid ExamSessionId = Guid.Parse("4d3b69f1-0d6e-4e1b-b89c-2146e355e104");
    private static readonly Guid QuestionId = Guid.Parse("4d3b69f1-0d6e-4e1b-b89c-2146e355e105");
    private static readonly Guid RubricId = Guid.Parse("4d3b69f1-0d6e-4e1b-b89c-2146e355e106");
    private static readonly Guid LecturerRoleId = Guid.Parse("4d3b69f1-0d6e-4e1b-b89c-2146e355e107");
    private static readonly Guid ParticipantId = Guid.Parse("4d3b69f1-0d6e-4e1b-b89c-2146e355e108");
    private static readonly Guid AttemptId = Guid.Parse("4d3b69f1-0d6e-4e1b-b89c-2146e355e109");
    private static readonly DateTime StartedAt = new(2025, 1, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public async Task Get_review_returns_attempt_result_contract_for_assigned_lecturer()
    {
        using var factory = NewLecturerFactory();
        await SeedReviewScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/interview-attempts/{AttemptId}/review");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<AttemptResult>(JsonOptions);
        Assert.NotNull(result);
        Assert.Equal(AttemptId, result.AttemptId);
        Assert.Equal(ExamSessionId, result.ExamSessionId);
        Assert.Equal(StudentId, result.StudentId);
        Assert.Equal(AttemptStatus.PendingReview, result.Status);
        Assert.Null(result.Review);
        Assert.Empty(result.Responses);
    }

    [Fact]
    public async Task Put_score_review_returns_approved_review_contract()
    {
        using var factory = NewLecturerFactory();
        await SeedReviewScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.PutAsJsonAsync(
            $"/api/interview-attempts/{AttemptId}/score-review",
            new { finalScore = 85.25m, comment = "Reviewed and confirmed." });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ScoreReviewResult>(JsonOptions);
        Assert.NotNull(result);
        Assert.Equal(LecturerId, result.ReviewerId);
        Assert.Equal(85.25m, result.FinalScore);
        Assert.Equal("Reviewed and confirmed.", result.Comment);
        Assert.Equal(ReviewStatus.Approved, result.Status);
        Assert.NotNull(result.ReviewedAt);
    }

    [Fact]
    public async Task Draft_and_confirm_endpoints_preserve_pending_then_approved_states()
    {
        using var factory = NewLecturerFactory();
        await SeedReviewScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var draftResponse = await client.PutAsJsonAsync(
            $"/api/interview-attempts/{AttemptId}/score-review/draft",
            new { finalScore = 85.25m, comment = "Draft comment." });

        Assert.Equal(HttpStatusCode.OK, draftResponse.StatusCode);
        var draft = await draftResponse.Content.ReadFromJsonAsync<ScoreReviewResult>(JsonOptions);
        Assert.NotNull(draft);
        Assert.Equal(ReviewStatus.Pending, draft.Status);
        Assert.Null(draft.ReviewedAt);

        using var detailsResponse = await client.GetAsync($"/api/interview-attempts/{AttemptId}/review");
        var details = await detailsResponse.Content.ReadFromJsonAsync<AttemptResult>(JsonOptions);
        Assert.NotNull(details);
        Assert.Equal(draft, details.Review);
        Assert.Equal(AttemptStatus.PendingReview, details.Status);

        using var confirmResponse = await client.PostAsync(
            $"/api/interview-attempts/{AttemptId}/score-review/confirm", content: null);
        Assert.Equal(HttpStatusCode.OK, confirmResponse.StatusCode);
        var confirmed = await confirmResponse.Content.ReadFromJsonAsync<ScoreReviewResult>(JsonOptions);
        Assert.NotNull(confirmed);
        Assert.Equal(ReviewStatus.Approved, confirmed.Status);
        Assert.NotNull(confirmed.ReviewedAt);
    }

    [Fact]
    public async Task Lecturer_can_list_submitted_attempts_for_an_assigned_session()
    {
        using var factory = NewLecturerFactory();
        await SeedReviewScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/exam-sessions/{ExamSessionId}/score-reviews");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var attempts = await response.Content.ReadFromJsonAsync<AttemptResult[]>(JsonOptions);
        Assert.NotNull(attempts);
        Assert.Single(attempts);
        Assert.Equal(AttemptId, attempts[0].AttemptId);
        Assert.Equal(AttemptStatus.PendingReview, attempts[0].Status);
    }

    [Fact]
    public async Task Put_score_review_is_forbidden_for_student()
    {
        using var factory = new ApiTestFactory().AuthenticateAs(StudentId, Roles.Student);
        using var client = factory.CreateClient();

        using var response = await client.PutAsJsonAsync(
            $"/api/interview-attempts/{AttemptId}/score-review",
            new { finalScore = 85.25m, comment = "Reviewed and confirmed." });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Put_score_review_persists_review_data()
    {
        using var factory = NewLecturerFactory();
        await SeedReviewScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.PutAsJsonAsync(
            $"/api/interview-attempts/{AttemptId}/score-review",
            new { finalScore = 85.25m, comment = "Reviewed and confirmed." });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InterviewDbContext>();
        var review = await db.ScoreReviews.SingleAsync(x => x.AttemptId == AttemptId);

        Assert.Equal(AttemptId, review.AttemptId);
        Assert.Equal(LecturerId, review.ReviewerId);
        Assert.Equal(85.25m, review.FinalScore);
        Assert.Equal("Reviewed and confirmed.", review.Comment);
        Assert.Equal(ReviewStatus.Approved, review.Status);
        Assert.NotNull(review.ReviewedAt);
    }

    [Fact]
    public async Task Student_cannot_access_score_review_endpoint()
    {
        using var factory = new ApiTestFactory().AuthenticateAs(StudentId, Roles.Student);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"/api/interview-attempts/{AttemptId}/review");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Score_above_session_maximum_returns_shared_validation_error_envelope()
    {
        using var factory = NewLecturerFactory();
        await SeedReviewScenarioAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.PutAsJsonAsync(
            $"/api/interview-attempts/{AttemptId}/score-review",
            new { finalScore = 100.01m, comment = "Out of range." });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.NotNull(error);
        Assert.Equal("validation_failed", error.Code);
        Assert.NotNull(error.Details);
        Assert.Contains(nameof(SubmitScoreReviewRequest.FinalScore), error.Details.Keys);
    }

    private static ApiTestFactory NewLecturerFactory() =>
        new ApiTestFactory().AuthenticateAs(LecturerId, Roles.Lecturer);

    private static async Task SeedReviewScenarioAsync(ApiTestFactory factory)
    {
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AccessConfigDbContext>();
            var role = new AccessDomain.Role { Id = LecturerRoleId, Name = Roles.Lecturer };
            var lecturer = new AccessDomain.User
            {
                Id = LecturerId,
                RoleId = LecturerRoleId,
                Role = role,
                FullName = "Test Lecturer",
                Email = "reviewer@example.test",
                Username = "test.reviewer",
                LecturerCode = "L-100",
                IsActive = true,
                CreatedAt = StartedAt,
            };
            var course = new AccessDomain.Course
            {
                Id = CourseId,
                Code = "TST101",
                Name = "Test Course",
            };
            var examSession = new AccessDomain.ExamSession
            {
                Id = ExamSessionId,
                CourseId = CourseId,
                Course = course,
                CreatedBy = LecturerId,
                Title = "Test Viva",
                StartTime = StartedAt,
                EndTime = StartedAt.AddHours(1),
                TimeLimitPerAnswerSeconds = 60,
                MaxFollowUps = 1,
                RubricMode = AccessDomain.RubricMode.Legacy,
                Status = ExamSessionStatus.Published,
                CreatedAt = StartedAt,
            };
            var rubric = new AccessDomain.Rubric
            {
                Id = RubricId,
                QuestionId = QuestionId,
                MaxScore = 100m,
            };
            var question = new AccessDomain.Question
            {
                Id = QuestionId,
                ExamSessionId = ExamSessionId,
                OrderNo = 1,
                Content = "Test question",
                Rubric = rubric,
            };
            var assignment = new AccessDomain.CourseLecturer
            {
                CourseId = CourseId,
                Course = course,
                LecturerId = LecturerId,
                Lecturer = lecturer,
                AssignedAt = StartedAt,
            };
            course.Lecturers.Add(assignment);
            course.ExamSessions.Add(examSession);
            examSession.Questions.Add(question);
            lecturer.CourseAssignments.Add(assignment);

            db.Roles.Add(role);
            db.Courses.Add(course);
            await db.SaveChangesAsync();
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InterviewDbContext>();
            var participant = new InterviewDomain.Participant
            {
                Id = ParticipantId,
                ExamSessionId = ExamSessionId,
                StudentId = StudentId,
                JoinedAt = StartedAt,
                Status = InterviewDomain.ParticipantStatus.Completed,
            };
            var attempt = new InterviewDomain.InterviewAttempt
            {
                Id = AttemptId,
                ParticipantId = ParticipantId,
                Participant = participant,
                StartedAt = StartedAt,
            };
            attempt.Complete(StartedAt.AddMinutes(20));
            participant.Attempts.Add(attempt);
            db.Participants.Add(participant);
            await db.SaveChangesAsync();
        }
    }
}
