using System.Net;
using System.Net.Http.Json;
using AiOralExam.IntegrationTests.Infrastructure;
using AiOralExam.Modules.AccessConfig.Application.ExamSessions;
using AiOralExam.Modules.AccessConfig.Application.Participants;
using AiOralExam.Modules.AccessConfig.Application.Rubrics;
using AiOralExam.Modules.AccessConfig.Application.RubricTemplates;
using AiOralExam.Modules.AccessConfig.Application.RubricVersions;
using AiOralExam.Modules.AccessConfig.Contracts;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.Modules.Interview.Application;
using AiOralExam.Modules.Interview.Infrastructure;
using AiOralExam.Modules.Reporting.Application;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using InterviewParticipant = AiOralExam.Modules.Interview.Domain.Participant;
using InterviewParticipantStatus = AiOralExam.Modules.Interview.Domain.ParticipantStatus;

namespace AiOralExam.IntegrationTests.Http;

public sealed class SharedVersionWorkflowHttpTests
{
    private static readonly Guid LecturerId = Guid.Parse("d115d5a8-63f7-49f3-9492-222222222271");
    private static readonly Guid StudentId = Guid.Parse("d115d5a8-63f7-49f3-9492-222222222272");
    private static readonly string StudentCode = "SE180271";

    [Fact]
    public async Task Shared_version_workflow_reaches_interview_scoring_and_reporting()
    {
        using var factory = new ApiTestFactory().AuthenticateAs(LecturerId, Roles.Administrator);
        var seeded = await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var templateResponse = await client.PostAsJsonAsync("/api/rubric-templates", new
        {
            name = "End-to-end rubric"
        });
        Assert.Equal(HttpStatusCode.Created, templateResponse.StatusCode);
        var template = await templateResponse.Content.ReadFromJsonAsync<RubricTemplateDto>();
        Assert.NotNull(template);

        using var versionResponse = await client.PostAsJsonAsync(
            $"/api/rubric-templates/{template.Id}/versions", new
            {
                criteria = new[]
                {
                    new { orderNo = 2, description = "Clarity", maxPoints = 3m },
                    new { orderNo = 1, description = "Accuracy", maxPoints = 5m }
                }
            });
        Assert.Equal(HttpStatusCode.Created, versionResponse.StatusCode);
        var version = await versionResponse.Content.ReadFromJsonAsync<RubricVersionDetailDto>();
        Assert.NotNull(version);
        Assert.True(version.IsSealed);
        Assert.Equal(8m, version.MaxScore);
        Assert.Equal(new[] { 1, 2 }, version.Criteria.Select(x => x.OrderNo));
        var criterionIds = version.Criteria.Select(x => x.Id).ToArray();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AccessConfigDbContext>();
            var storedVersion = await db.RubricVersions.SingleAsync(x => x.Id == version.Id);
            storedVersion.MaxScore = version.MaxScore;
            await db.SaveChangesAsync();
        }

        using var activationResponse = await client.PostAsync(
            $"/api/rubric-templates/{template.Id}/versions/{version.Id}/activate", null);
        Assert.Equal(HttpStatusCode.OK, activationResponse.StatusCode);
        version = await activationResponse.Content.ReadFromJsonAsync<RubricVersionDetailDto>();
        Assert.NotNull(version);
        Assert.True(version.IsActive);

        var start = DateTime.UtcNow.AddMinutes(-2);
        var end = DateTime.UtcNow.AddHours(1);
        using var sessionResponse = await client.PostAsJsonAsync("/api/exam-sessions", new
        {
            courseId = seeded.CourseId,
            title = "Shared rubric viva",
            startTime = start,
            endTime = end,
            timeLimitPerAnswerSeconds = 120,
            maxFollowUps = 0,
            rubricMode = "SharedVersion",
            rubricVersionId = version.Id
        });
        Assert.Equal(HttpStatusCode.Created, sessionResponse.StatusCode);
        var session = await sessionResponse.Content.ReadFromJsonAsync<ExamSessionCommandDto>();
        Assert.NotNull(session);
        Assert.Equal("SharedVersion", session.RubricMode);
        Assert.Equal(version.Id, session.RubricVersionId);

        using var missingQuestionsPublish = await client.PostAsync($"/api/exam-sessions/{session.Id}/publish", null);
        Assert.Equal(HttpStatusCode.BadRequest, missingQuestionsPublish.StatusCode);

        using var questionsResponse = await client.PostAsJsonAsync(
            $"/api/exam-sessions/{session.Id}/questions/import", new
            {
                questions = new[]
                {
                    new { orderNo = 2, content = "Explain the second design decision." },
                    new { orderNo = 1, content = "Explain the first design decision." }
                }
            });
        Assert.Equal(HttpStatusCode.Created, questionsResponse.StatusCode);

        using var missingParticipantsPublish = await client.PostAsync($"/api/exam-sessions/{session.Id}/publish", null);
        Assert.Equal(HttpStatusCode.BadRequest, missingParticipantsPublish.StatusCode);

        using var participantResponse = await client.PostAsJsonAsync(
            $"/api/exam-sessions/{session.Id}/participants/import", new
            {
                studentCodes = new[] { StudentCode }
            });
        Assert.Equal(HttpStatusCode.OK, participantResponse.StatusCode);
        var imported = await participantResponse.Content.ReadFromJsonAsync<ImportStudentsResult>();
        Assert.NotNull(imported);
        Assert.Equal(1, imported.Imported);
        var participant = Assert.Single(imported.Participants);
        Assert.Equal(StudentId, participant.StudentId);

        using (var scope = factory.Services.CreateAsyncScope())
        {
            var interviewDb = scope.ServiceProvider.GetRequiredService<InterviewDbContext>();
            interviewDb.Participants.Add(new InterviewParticipant
            {
                Id = participant.Id,
                ExamSessionId = session.Id,
                StudentId = StudentId,
                JoinedAt = participant.JoinedAt,
                Status = InterviewParticipantStatus.Registered
            });
            await interviewDb.SaveChangesAsync();
        }

        using var publishResponse = await client.PostAsync($"/api/exam-sessions/{session.Id}/publish", null);
        Assert.Equal(HttpStatusCode.OK, publishResponse.StatusCode);
        var published = await publishResponse.Content.ReadFromJsonAsync<ExamSessionCommandDto>();
        Assert.NotNull(published);
        Assert.Equal("Published", published.Status);
        Assert.Equal(2, published.QuestionCount);
        Assert.Equal(1, published.ParticipantCount);

        factory.AuthenticateAs(StudentId, Roles.Student);
        using var attemptResponse = await client.PostAsJsonAsync("/api/interview-attempts", new
        {
            examSessionId = session.Id
        });
        Assert.Equal(HttpStatusCode.Created, attemptResponse.StatusCode);
        var attempt = await attemptResponse.Content.ReadFromJsonAsync<AttemptStateDto>();
        Assert.NotNull(attempt);
        Assert.Equal(2, attempt.TotalQuestions);
        Assert.Equal(1, attempt.CurrentTurn?.QuestionOrderNo);

        var answer = string.Join(' ', Enumerable.Repeat("explanation", 60));
        using var missingTurnIdResponse = await client.PostAsJsonAsync(
            $"/api/interview-attempts/{attempt.AttemptId}/responses", new { answerText = answer });
        Assert.Equal(HttpStatusCode.BadRequest, missingTurnIdResponse.StatusCode);

        using var firstAnswerResponse = await client.PostAsJsonAsync(
            $"/api/interview-attempts/{attempt.AttemptId}/responses", new
            {
                answerText = answer,
                turnId = attempt.CurrentTurn!.TurnId
            });
        Assert.Equal(HttpStatusCode.OK, firstAnswerResponse.StatusCode);
        var firstAnswer = await firstAnswerResponse.Content.ReadFromJsonAsync<SubmitAnswerResponse>();
        Assert.NotNull(firstAnswer);
        Assert.Equal(SubmitOutcome.NextQuestion, firstAnswer.Outcome);
        Assert.Equal(2, firstAnswer.Attempt.CurrentTurn?.QuestionOrderNo);

        using var secondAnswerResponse = await client.PostAsJsonAsync(
            $"/api/interview-attempts/{attempt.AttemptId}/responses", new
            {
                answerText = answer,
                turnId = firstAnswer.Attempt.CurrentTurn!.TurnId
            });
        Assert.Equal(HttpStatusCode.OK, secondAnswerResponse.StatusCode);
        var secondAnswer = await secondAnswerResponse.Content.ReadFromJsonAsync<SubmitAnswerResponse>();
        Assert.NotNull(secondAnswer);
        Assert.Equal(SubmitOutcome.Completed, secondAnswer.Outcome);
        Assert.Equal("PendingReview", secondAnswer.Attempt.Status);

        using (var scope = factory.Services.CreateAsyncScope())
        {
            var accessDb = scope.ServiceProvider.GetRequiredService<AccessConfigDbContext>();
            var configuration = scope.ServiceProvider.GetRequiredService<IExamConfigurationApi>();
            var effectiveSession = await configuration.GetSessionAsync(session.Id);
            Assert.NotNull(effectiveSession);
            var questions = effectiveSession.Questions.OrderBy(x => x.OrderNo).ToArray();
            Assert.Equal(2, questions.Length);
            Assert.Equal(16m, effectiveSession.TotalMaxScore);
            foreach (var question in questions)
            {
                Assert.Equal(8m, question.MaxScore);
                Assert.Equal(criterionIds, question.Criteria.Select(x => x.Id));
                Assert.Equal(new[] { 1, 2 }, question.Criteria.Select(x => x.OrderNo));
            }

            var interviewDb = scope.ServiceProvider.GetRequiredService<InterviewDbContext>();
            var responses = await interviewDb.QuestionResponses.AsNoTracking()
                .Where(x => x.AttemptId == attempt.AttemptId)
                .ToListAsync();
            Assert.Equal(2, responses.Count);
            Assert.All(responses, response => Assert.Equal(8m, response.AiSuggestedScore));
            var responseIds = responses.Select(x => x.Id).ToArray();
            var scores = await interviewDb.CriterionScores.AsNoTracking()
                .Where(x => responseIds.Contains(x.QuestionResponseId))
                .ToListAsync();
            Assert.Equal(4, scores.Count);
            Assert.Equal(4, scores.Select(x => x.Id).Distinct().Count());
            foreach (var response in responses)
            {
                Assert.Equal(criterionIds.Order(), scores.Where(x => x.QuestionResponseId == response.Id)
                    .Select(x => x.RubricCriterionId).Order());
                Assert.Equal(response.AiSuggestedScore, scores.Where(x => x.QuestionResponseId == response.Id)
                    .Sum(x => x.Points));
            }
        }

        factory.AuthenticateAs(LecturerId, Roles.Lecturer);
        using var resultsResponse = await client.GetAsync($"/api/reports/exam-sessions/{session.Id}/results");
        Assert.Equal(HttpStatusCode.OK, resultsResponse.StatusCode);
        var results = await resultsResponse.Content.ReadFromJsonAsync<List<StudentResultDto>>();
        var result = Assert.Single(results!);
        Assert.Equal(attempt.AttemptId, result.AttemptId);
        Assert.Equal(16m, result.SuggestedTotalScore);
        Assert.Equal(16m, result.MaxTotalScore);

        using var reportResponse = await client.GetAsync($"/api/reports/attempts/{attempt.AttemptId}");
        Assert.Equal(HttpStatusCode.OK, reportResponse.StatusCode);
        var report = await reportResponse.Content.ReadFromJsonAsync<AttemptReportDto>();
        Assert.NotNull(report);
        Assert.Equal(16m, report.MaxTotalScore);
        Assert.Equal(16m, report.SuggestedTotalScore);
        Assert.Equal(new[] { 8m, 8m }, report.Questions.Select(x => x.MaxScore));
        Assert.Equal(new decimal?[] { 8m, 8m }, report.Questions.Select(x => x.SuggestedScore));

        using var lockedUpdate = await client.PutAsJsonAsync($"/api/exam-sessions/{session.Id}", new
        {
            title = "Changed after publication",
            startTime = start,
            endTime = end,
            timeLimitPerAnswerSeconds = 120,
            rubricMode = "Legacy",
            rubricVersionId = (Guid?)null
        });
        Assert.Equal(HttpStatusCode.Conflict, lockedUpdate.StatusCode);

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var unchangedSession = await verifyScope.ServiceProvider.GetRequiredService<AccessConfigDbContext>()
            .ExamSessions.AsNoTracking().SingleAsync(x => x.Id == session.Id);
        Assert.Equal(RubricMode.SharedVersion, unchangedSession.RubricMode);
        Assert.Equal(version.Id, unchangedSession.RubricVersionId);
    }

    [Fact]
    public async Task Legacy_session_can_still_import_questions_add_question_rubrics_and_publish()
    {
        using var factory = new ApiTestFactory().AuthenticateAs(LecturerId, Roles.Lecturer);
        var seeded = await SeedAsync(factory);
        using var client = factory.CreateClient();
        var start = DateTime.UtcNow.AddMinutes(-1);
        var end = DateTime.UtcNow.AddHours(1);

        using var sessionResponse = await client.PostAsJsonAsync("/api/exam-sessions", new
        {
            courseId = seeded.CourseId,
            title = "Legacy compatibility viva",
            startTime = start,
            endTime = end,
            timeLimitPerAnswerSeconds = 120,
            maxFollowUps = 0
        });
        Assert.Equal(HttpStatusCode.Created, sessionResponse.StatusCode);
        var session = await sessionResponse.Content.ReadFromJsonAsync<ExamSessionCommandDto>();
        Assert.NotNull(session);
        Assert.Equal("Legacy", session.RubricMode);
        Assert.Null(session.RubricVersionId);

        using var questionsResponse = await client.PostAsJsonAsync(
            $"/api/exam-sessions/{session.Id}/questions/import", new
            {
                questions = new[] { new { orderNo = 1, content = "Explain the legacy question." } }
            });
        Assert.Equal(HttpStatusCode.Created, questionsResponse.StatusCode);
        var importedQuestions = await questionsResponse.Content.ReadFromJsonAsync<List<QuestionDto>>();
        Assert.NotNull(importedQuestions);
        var question = Assert.Single(importedQuestions);

        using var rubricResponse = await client.PutAsJsonAsync(
            $"/api/exam-sessions/{session.Id}/questions/{question.Id}/rubric", new SaveRubricRequest(
            [new(null, 1, "Accuracy", 5m)]));
        Assert.Equal(HttpStatusCode.OK, rubricResponse.StatusCode);
        var rubric = await rubricResponse.Content.ReadFromJsonAsync<RubricDto>();
        Assert.NotNull(rubric);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AccessConfigDbContext>();
            var storedRubric = await db.Rubrics.SingleAsync(x => x.Id == rubric.Id);
            storedRubric.MaxScore = rubric.Criteria.Sum(x => x.MaxPoints);
            await db.SaveChangesAsync();
        }

        using var participantResponse = await client.PostAsJsonAsync(
            $"/api/exam-sessions/{session.Id}/participants/import", new { studentCodes = new[] { StudentCode } });
        Assert.Equal(HttpStatusCode.OK, participantResponse.StatusCode);

        using var publishResponse = await client.PostAsync($"/api/exam-sessions/{session.Id}/publish", null);
        Assert.Equal(HttpStatusCode.OK, publishResponse.StatusCode);
        var published = await publishResponse.Content.ReadFromJsonAsync<ExamSessionCommandDto>();
        Assert.NotNull(published);
        Assert.Equal("Published", published.Status);
        Assert.Equal("Legacy", published.RubricMode);
    }

    private static async Task<Seed> SeedAsync(ApiTestFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AccessConfigDbContext>();
        var lecturerRole = new Role { Id = Guid.NewGuid(), Name = Roles.Lecturer };
        var studentRole = new Role { Id = Guid.NewGuid(), Name = Roles.Student };
        var lecturer = new User
        {
            Id = LecturerId,
            RoleId = lecturerRole.Id,
            Role = lecturerRole,
            FullName = "Workflow Lecturer",
            Email = "shared-workflow-lecturer@example.test",
            Username = "shared-workflow-lecturer",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        var student = new User
        {
            Id = StudentId,
            RoleId = studentRole.Id,
            Role = studentRole,
            FullName = "Workflow Student",
            Email = "shared-workflow-student@example.test",
            Username = "shared-workflow-student",
            StudentCode = StudentCode,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        var course = new Course { Id = Guid.NewGuid(), Code = "WF101", Name = "Workflow Course" };
        course.Lecturers.Add(new CourseLecturer
        {
            CourseId = course.Id,
            Course = course,
            LecturerId = lecturer.Id,
            Lecturer = lecturer,
            AssignedAt = DateTime.UtcNow
        });

        db.Roles.AddRange(lecturerRole, studentRole);
        db.Users.AddRange(lecturer, student);
        db.Courses.Add(course);
        await db.SaveChangesAsync();
        return new Seed(course.Id);
    }

    private sealed record Seed(Guid CourseId);
}
