using AiOralExam.Modules.AccessConfig.Application;
using AiOralExam.Modules.AccessConfig.Application.ExamSessions;
using AiOralExam.Modules.AccessConfig.Contracts;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.Modules.Interview.Contracts;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;

namespace AiOralExam.UnitTests;

public sealed class ExamConfigurationApiTests
{
    [Fact]
    public async Task Legacy_session_preserves_per_question_rubrics_and_missing_rubric_behavior()
    {
        await using var db = CreateDatabase();
        var (course, session) = NewSession(RubricMode.Legacy);
        var questionWithRubric = NewQuestion(session, 1);
        var rubric = new Rubric { Id = Guid.NewGuid(), QuestionId = questionWithRubric.Id, MaxScore = 4.5m };
        var criterionId = Guid.NewGuid();
        rubric.Criteria.Add(new RubricCriterion
        {
            Id = criterionId,
            RubricId = rubric.Id,
            Rubric = rubric,
            OrderNo = 1,
            Description = "Accuracy",
            MaxPoints = 4.5m
        });
        questionWithRubric.Rubric = rubric;
        NewQuestion(session, 2);
        db.Courses.Add(course);
        db.ExamSessions.Add(session);
        await db.SaveChangesAsync();

        var info = await new ExamConfigurationApi(db).GetSessionAsync(session.Id);

        Assert.NotNull(info);
        Assert.Equal(2, info.Questions.Count);
        Assert.Equal(criterionId, Assert.Single(info.Questions[0].Criteria).Id);
        Assert.Equal(4.5m, info.Questions[0].MaxScore);
        Assert.Empty(info.Questions[1].Criteria);
        Assert.Equal(0m, info.Questions[1].MaxScore);
        Assert.Equal(4.5m, info.TotalMaxScore);
    }

    [Fact]
    public async Task Shared_version_returns_same_stable_ordered_criteria_and_per_question_max_to_consumers()
    {
        await using var db = CreateDatabase();
        var (session, version) = NewSharedSession();
        var course = version.Template.Course;
        session.Questions.Clear();
        NewQuestion(session, 2);
        NewQuestion(session, 1);
        db.Courses.Add(course);
        db.RubricTemplates.Add(version.Template);
        db.RubricVersions.Add(version);
        db.ExamSessions.Add(session);
        await db.SaveChangesAsync();

        var info = await new ExamConfigurationApi(db).GetSessionAsync(session.Id);
        Assert.NotNull(info);
        Assert.Equal(new[] { 1, 2 }, info.Questions.Select(x => x.OrderNo));
        Assert.All(info.Questions, question =>
        {
            Assert.Equal(10m, question.MaxScore);
            Assert.Equal(new[] { 1, 2 }, question.Criteria.Select(x => x.OrderNo));
            Assert.Equal(new[] { version.Criteria.Single(x => x.OrderNo == 1).Id,
                    version.Criteria.Single(x => x.OrderNo == 2).Id },
                question.Criteria.Select(x => x.Id));
        });
        Assert.Equal(20m, info.TotalMaxScore);

        var detail = await new ExamSessionQueries(db, new AdministratorCurrentUser(), new FakeInterviewResultsApi())
            .GetSessionAsync(session.Id);
        Assert.All(detail.Questions, question =>
        {
            Assert.Equal(10m, question.MaxScore);
            Assert.Equal(new[] { 1, 2 }, question.RubricCriteria.Select(x => x.OrderNo));
            Assert.Equal(info.Questions[0].Criteria.Select(x => x.Id), question.RubricCriteria.Select(x => x.Id));
        });
    }

    [Fact]
    public void Shared_version_without_selection_is_invalid()
    {
        var (_, session) = NewSession(RubricMode.SharedVersion);

        AssertInvalid(session);
    }

    [Fact]
    public void Shared_version_must_be_sealed()
    {
        var (session, _) = NewSharedSession(isSealed: false);

        AssertInvalid(session);
    }

    [Fact]
    public void Shared_version_must_have_criteria()
    {
        var (session, _) = NewSharedSession(withCriteria: false);

        AssertInvalid(session);
    }

    [Fact]
    public void Shared_version_course_and_score_must_match_its_configuration()
    {
        var (crossCourseSession, _) = NewSharedSession(versionCourseId: Guid.NewGuid());
        AssertInvalid(crossCourseSession);

        var (scoreMismatchSession, _) = NewSharedSession(versionMaxScore: 11m);
        AssertInvalid(scoreMismatchSession);
    }

    private static void AssertInvalid(ExamSession session)
    {
        var exception = Assert.Throws<ConflictException>(() => ExamConfigurationApi.ToInfo(session));
        Assert.Equal("exam_session_rubric_invalid", exception.Code);
    }

    private static AccessConfigDbContext CreateDatabase() =>
        new(new DbContextOptionsBuilder<AccessConfigDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static (Course Course, ExamSession Session) NewSession(RubricMode mode)
    {
        var course = new Course { Id = Guid.NewGuid(), Code = "RUB101", Name = "Rubric Course" };
        var session = new ExamSession
        {
            Id = Guid.NewGuid(),
            CourseId = course.Id,
            Course = course,
            CreatedBy = Guid.NewGuid(),
            Title = "Rubric Session",
            StartTime = DateTime.UtcNow,
            EndTime = DateTime.UtcNow.AddHours(1),
            TimeLimitPerAnswerSeconds = 60,
            RubricMode = mode
        };
        return (course, session);
    }

    private static (ExamSession Session, RubricVersion Version) NewSharedSession(
        bool isSealed = true,
        bool withCriteria = true,
        Guid? versionCourseId = null,
        decimal versionMaxScore = 10m)
    {
        var (course, session) = NewSession(RubricMode.SharedVersion);
        var template = new RubricTemplate
        {
            Id = Guid.NewGuid(),
            CourseId = course.Id,
            Course = course,
            Name = "Shared rubric",
            CreatedBy = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow
        };
        var version = new RubricVersion
        {
            Id = Guid.NewGuid(),
            TemplateId = template.Id,
            Template = template,
            CourseId = versionCourseId ?? course.Id,
            VersionNo = 1,
            MaxScore = versionMaxScore,
            IsSealed = isSealed,
            CreatedBy = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow
        };
        if (withCriteria)
        {
            version.Criteria.Add(NewVersionCriterion(version, 2, "Clarity", 4m));
            version.Criteria.Add(NewVersionCriterion(version, 1, "Accuracy", 6m));
        }

        session.RubricVersionId = version.Id;
        session.RubricVersion = version;
        NewQuestion(session, 1);
        return (session, version);
    }

    private static RubricCriterion NewVersionCriterion(
        RubricVersion version, int orderNo, string description, decimal maxPoints) => new()
    {
        Id = Guid.NewGuid(),
        RubricVersionId = version.Id,
        RubricVersion = version,
        OrderNo = orderNo,
        Description = description,
        MaxPoints = maxPoints
    };

    private static Question NewQuestion(ExamSession session, int orderNo)
    {
        var question = new Question
        {
            Id = Guid.NewGuid(),
            ExamSessionId = session.Id,
            OrderNo = orderNo,
            Content = $"Question {orderNo}"
        };
        session.Questions.Add(question);
        return question;
    }

    private sealed class AdministratorCurrentUser : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid Id => Guid.NewGuid();
        public string? Role => Roles.Administrator;
        public bool IsInRole(string role) => role == Roles.Administrator;
    }

    private sealed class FakeInterviewResultsApi : IInterviewResultsApi
    {
        public Task<AttemptResult?> GetAttemptResultAsync(Guid attemptId, CancellationToken ct = default) =>
            Task.FromResult<AttemptResult?>(null);

        public Task<IReadOnlyList<AttemptCounts>> GetAttemptCountsAsync(
            IReadOnlyCollection<Guid> sessionIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AttemptCounts>>([]);

        public Task<IReadOnlyList<AttemptResult>> GetSessionResultsAsync(
            Guid examSessionId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AttemptResult>>([]);
    }
}
