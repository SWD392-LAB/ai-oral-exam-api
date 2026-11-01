using AiOralExam.Modules.AccessConfig.Application;
using AiOralExam.Modules.AccessConfig.Application.ExamSessions;
using AiOralExam.Modules.AccessConfig.Application.RubricVersions;
using AiOralExam.Modules.AccessConfig.Contracts;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AiOralExam.UnitTests;

public sealed class ExamSessionPublishingTests
{
    [Fact]
    public async Task Legacy_session_with_a_valid_rubric_publishes()
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db, RubricMode.Legacy, addLegacyRubric: true);
        var service = new ExamSessionCommands(db, new TestCurrentUser(seed.LecturerId));

        var result = await service.PublishAsync(seed.SessionId, CancellationToken.None);

        Assert.Equal("Published", result.Status);
        Assert.Equal(RubricMode.Legacy, (await db.ExamSessions.SingleAsync()).RubricMode);
        Assert.Equal("Publish", Assert.Single(db.AuditLogs).Action);
    }

    [Fact]
    public async Task Legacy_session_without_a_valid_question_rubric_is_rejected()
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db, RubricMode.Legacy, addLegacyRubric: false);
        var service = new ExamSessionCommands(db, new TestCurrentUser(seed.LecturerId));

        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            service.PublishAsync(seed.SessionId, CancellationToken.None));

        Assert.Contains("rubric", exception.Details!.Keys);
        Assert.Equal(ExamSessionStatus.Draft, (await db.ExamSessions.SingleAsync()).Status);
        Assert.Empty(db.AuditLogs);
    }

    [Fact]
    public async Task Draft_pinned_to_a_rotated_version_can_be_edited_but_cannot_be_published()
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db, RubricMode.SharedVersion);
        await RotateActiveVersionAsync(db, seed.LecturerId);
        var service = new ExamSessionCommands(db, new TestCurrentUser(seed.LecturerId));

        var updated = await service.UpdateAsync(seed.SessionId,
            new UpdateExamSessionRequest("Edited draft", DateTime.UtcNow.AddHours(1),
                DateTime.UtcNow.AddHours(2), 120), CancellationToken.None);
        Assert.Equal("Edited draft", updated.Title);

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            service.PublishAsync(seed.SessionId, CancellationToken.None));

        Assert.Equal("rubric_version_not_active", exception.Code);
        var draft = await db.ExamSessions.SingleAsync(x => x.Id == seed.SessionId);
        Assert.Equal(ExamSessionStatus.Draft, draft.Status);
        Assert.Equal(seed.VersionId, draft.RubricVersionId);
    }

    [Fact]
    public async Task Shared_version_session_over_the_sql_maximum_total_score_is_rejected()
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db, RubricMode.SharedVersion, addSecondQuestion: true,
            versionMaxScore: 600m, criterionMaxPoints: 600m);
        var service = new ExamSessionCommands(db, new TestCurrentUser(seed.LecturerId));

        var exception = await Assert.ThrowsAsync<ValidationException>(() =>
            service.PublishAsync(seed.SessionId, CancellationToken.None));

        Assert.Contains("rubric", exception.Details!.Keys);
        Assert.Equal(ExamSessionStatus.Draft, (await db.ExamSessions.SingleAsync()).Status);
        Assert.Empty(db.AuditLogs);
    }

    [Fact]
    public async Task Shared_version_session_publishes_without_question_specific_rubrics_and_keeps_consumer_contract()
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db, RubricMode.SharedVersion);
        var service = new ExamSessionCommands(db, new TestCurrentUser(seed.LecturerId));

        var result = await service.PublishAsync(seed.SessionId, CancellationToken.None);

        Assert.Equal("Published", result.Status);
        Assert.Empty(db.Rubrics);
        Assert.Equal("Publish", Assert.Single(db.AuditLogs).Action);

        var info = await new ExamConfigurationApi(db).GetSessionAsync(seed.SessionId);
        Assert.NotNull(info);
        Assert.Equal(5m, info.TotalMaxScore);
        var criterion = Assert.Single(Assert.Single(info.Questions).Criteria);
        Assert.Equal(seed.CriterionId, criterion.Id);
        Assert.Equal(5m, info.Questions[0].MaxScore);
    }

    [Fact]
    public async Task Shared_version_session_without_selection_is_rejected()
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db, RubricMode.SharedVersion, selectVersion: false);
        var service = new ExamSessionCommands(db, new TestCurrentUser(seed.LecturerId));

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            service.PublishAsync(seed.SessionId, CancellationToken.None));

        Assert.Equal("exam_session_rubric_invalid", exception.Code);
        Assert.Equal(ExamSessionStatus.Draft, (await db.ExamSessions.SingleAsync()).Status);
        Assert.Empty(db.AuditLogs);
    }

    [Fact]
    public async Task Shared_version_session_with_unsealed_version_is_rejected()
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db, RubricMode.SharedVersion, versionSealed: false);
        var service = new ExamSessionCommands(db, new TestCurrentUser(seed.LecturerId));

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            service.PublishAsync(seed.SessionId, CancellationToken.None));

        Assert.Equal("rubric_version_not_sealed", exception.Code);
        Assert.Equal(ExamSessionStatus.Draft, (await db.ExamSessions.SingleAsync()).Status);
        Assert.Empty(db.AuditLogs);
    }

    [Fact]
    public async Task Shared_version_session_with_course_scoped_version_is_rejected()
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db, RubricMode.SharedVersion, versionBelongsToCourse: true);
        var service = new ExamSessionCommands(db, new TestCurrentUser(seed.LecturerId));

        var exception = await Assert.ThrowsAsync<NotFoundException>(() =>
            service.PublishAsync(seed.SessionId, CancellationToken.None));

        Assert.Equal("rubric_version_not_found", exception.Code);
        Assert.Equal(ExamSessionStatus.Draft, (await db.ExamSessions.SingleAsync()).Status);
        Assert.Empty(db.AuditLogs);
    }

    [Fact]
    public async Task Shared_version_session_with_inactive_version_is_rejected()
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db, RubricMode.SharedVersion, versionActive: false);
        var service = new ExamSessionCommands(db, new TestCurrentUser(seed.LecturerId));

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            service.PublishAsync(seed.SessionId, CancellationToken.None));

        Assert.Equal("rubric_version_not_active", exception.Code);
        Assert.Equal(ExamSessionStatus.Draft, (await db.ExamSessions.SingleAsync()).Status);
        Assert.Empty(db.AuditLogs);
    }

    [Fact]
    public async Task Shared_version_session_with_empty_or_invalid_criteria_is_rejected()
    {
        await using (var emptyDb = CreateDatabase())
        {
            var seed = await SeedAsync(emptyDb, RubricMode.SharedVersion, versionHasCriteria: false);
            var service = new ExamSessionCommands(emptyDb, new TestCurrentUser(seed.LecturerId));

            var exception = await Assert.ThrowsAsync<ConflictException>(() =>
                service.PublishAsync(seed.SessionId, CancellationToken.None));

            Assert.Equal("rubric_version_invalid", exception.Code);
            Assert.Empty(emptyDb.AuditLogs);
        }

        await using (var invalidDb = CreateDatabase())
        {
            var seed = await SeedAsync(invalidDb, RubricMode.SharedVersion, invalidCriterion: true);
            var service = new ExamSessionCommands(invalidDb, new TestCurrentUser(seed.LecturerId));

            var exception = await Assert.ThrowsAsync<ConflictException>(() =>
                service.PublishAsync(seed.SessionId, CancellationToken.None));

            Assert.Equal("rubric_version_invalid", exception.Code);
            Assert.Empty(invalidDb.AuditLogs);
        }
    }

    [Fact]
    public async Task Shared_version_session_with_inconsistent_max_score_is_rejected()
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db, RubricMode.SharedVersion, versionMaxScore: 4m);
        var service = new ExamSessionCommands(db, new TestCurrentUser(seed.LecturerId));

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            service.PublishAsync(seed.SessionId, CancellationToken.None));

        Assert.Equal("rubric_version_invalid", exception.Code);
        Assert.Empty(db.AuditLogs);
    }

    [Fact]
    public async Task Shared_version_session_requires_questions_and_participants()
    {
        await using (var noQuestionsDb = CreateDatabase())
        {
            var seed = await SeedAsync(noQuestionsDb, RubricMode.SharedVersion, addQuestion: false);
            var service = new ExamSessionCommands(noQuestionsDb, new TestCurrentUser(seed.LecturerId));

            var exception = await Assert.ThrowsAsync<ValidationException>(() =>
                service.PublishAsync(seed.SessionId, CancellationToken.None));

            Assert.Contains("questions", exception.Details!.Keys);
            Assert.Empty(noQuestionsDb.AuditLogs);
        }

        await using (var noParticipantsDb = CreateDatabase())
        {
            var seed = await SeedAsync(noParticipantsDb, RubricMode.SharedVersion, addParticipant: false);
            var service = new ExamSessionCommands(noParticipantsDb, new TestCurrentUser(seed.LecturerId));

            var exception = await Assert.ThrowsAsync<ValidationException>(() =>
                service.PublishAsync(seed.SessionId, CancellationToken.None));

            Assert.Contains("participants", exception.Details!.Keys);
            Assert.Empty(noParticipantsDb.AuditLogs);
        }
    }

    [Fact]
    public async Task Authorization_and_published_status_transition_remain_enforced()
    {
        await using (var unauthorizedDb = CreateDatabase())
        {
            var seed = await SeedAsync(unauthorizedDb, RubricMode.SharedVersion, assignedLecturer: false);
            var service = new ExamSessionCommands(unauthorizedDb, new TestCurrentUser(seed.LecturerId));

            await Assert.ThrowsAsync<ForbiddenException>(() =>
                service.PublishAsync(seed.SessionId, CancellationToken.None));
            Assert.Empty(unauthorizedDb.AuditLogs);
        }

        await using (var publishedDb = CreateDatabase())
        {
            var seed = await SeedAsync(publishedDb, RubricMode.SharedVersion, status: ExamSessionStatus.Published);
            var service = new ExamSessionCommands(publishedDb, new TestCurrentUser(seed.LecturerId));

            var exception = await Assert.ThrowsAsync<ConflictException>(() =>
                service.PublishAsync(seed.SessionId, CancellationToken.None));

            Assert.Equal("exam_session_not_draft", exception.Code);
            Assert.Empty(publishedDb.AuditLogs);
        }
    }

    [Fact]
    public async Task Published_session_update_cannot_change_its_rubric_selection()
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db, RubricMode.SharedVersion, status: ExamSessionStatus.Published);
        var service = new ExamSessionCommands(db, new TestCurrentUser(seed.LecturerId));

        await Assert.ThrowsAsync<ConflictException>(() => service.UpdateAsync(seed.SessionId,
            new UpdateExamSessionRequest("Changed", DateTime.UtcNow.AddHours(1), DateTime.UtcNow.AddHours(2), 120),
            CancellationToken.None));

        var unchanged = await db.ExamSessions.SingleAsync();
        Assert.Equal(RubricMode.SharedVersion, unchanged.RubricMode);
        Assert.Equal(seed.VersionId, unchanged.RubricVersionId);
    }

    [Fact]
    public async Task Published_session_keeps_its_pinned_version_when_platform_activation_rotates()
    {
        await using var db = CreateDatabase();
        var seed = await SeedAsync(db, RubricMode.SharedVersion);
        var commands = new ExamSessionCommands(db, new TestCurrentUser(seed.LecturerId));
        await commands.PublishAsync(seed.SessionId, CancellationToken.None);

        var nextVersionId = await RotateActiveVersionAsync(db, seed.LecturerId);
        var closed = await commands.CloseAsync(seed.SessionId, CancellationToken.None);

        var publishedSession = await db.ExamSessions.SingleAsync(x => x.Id == seed.SessionId);
        Assert.Equal("Closed", closed.Status);
        Assert.Equal(ExamSessionStatus.Closed, publishedSession.Status);
        Assert.Equal(seed.VersionId, publishedSession.RubricVersionId);
        Assert.False((await db.RubricVersions.SingleAsync(x => x.Id == seed.VersionId)).IsActive);
        Assert.True((await db.RubricVersions.SingleAsync(x => x.Id == nextVersionId)).IsActive);
    }

    private static async Task<Guid> RotateActiveVersionAsync(AccessConfigDbContext db, Guid creatorId)
    {
        var template = new RubricTemplate
        {
            Id = Guid.NewGuid(),
            Name = "Next platform rubric",
            CreatedBy = creatorId,
            CreatedAt = DateTime.UtcNow
        };
        var version = new RubricVersion
        {
            Id = Guid.NewGuid(),
            TemplateId = template.Id,
            Template = template,
            VersionNo = 1,
            MaxScore = 7m,
            IsSealed = true,
            CreatedBy = creatorId,
            CreatedAt = DateTime.UtcNow
        };
        version.Criteria.Add(new RubricCriterion
        {
            Id = Guid.NewGuid(),
            RubricVersionId = version.Id,
            RubricVersion = version,
            OrderNo = 1,
            Description = "Updated accuracy",
            MaxPoints = 7m
        });
        db.RubricTemplates.Add(template);
        db.RubricVersions.Add(version);
        await db.SaveChangesAsync();

        await new RubricVersionService(db, new TestAdministrator(creatorId))
            .ActivateAsync(template.Id, version.Id, CancellationToken.None);
        return version.Id;
    }

    private static AccessConfigDbContext CreateDatabase() =>
        new(new DbContextOptionsBuilder<AccessConfigDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static async Task<Seed> SeedAsync(
        AccessConfigDbContext db,
        RubricMode mode,
        bool assignedLecturer = true,
        bool addQuestion = true,
        bool addSecondQuestion = false,
        bool addLegacyRubric = false,
        bool addParticipant = true,
        bool selectVersion = true,
        bool versionSealed = true,
        bool versionHasCriteria = true,
        bool invalidCriterion = false,
        bool versionBelongsToCourse = false,
        bool versionActive = true,
        decimal versionMaxScore = 5m,
        decimal criterionMaxPoints = 5m,
        ExamSessionStatus status = ExamSessionStatus.Draft)
    {
        var lecturerRole = new Role { Id = Guid.NewGuid(), Name = Roles.Lecturer };
        var studentRole = new Role { Id = Guid.NewGuid(), Name = Roles.Student };
        var lecturer = NewUser(Guid.NewGuid(), lecturerRole, "lecturer@example.test");
        var student = NewUser(Guid.NewGuid(), studentRole, "student@example.test");
        var course = NewCourse("PUB101");
        if (assignedLecturer)
            course.Lecturers.Add(new CourseLecturer
            {
                CourseId = course.Id,
                Course = course,
                LecturerId = lecturer.Id,
                Lecturer = lecturer,
                AssignedAt = DateTime.UtcNow
            });

        var versionCourse = versionBelongsToCourse ? course : null;
        var template = new RubricTemplate
        {
            Id = Guid.NewGuid(),
            CourseId = versionCourse?.Id,
            Course = versionCourse,
            Name = "Publish rubric",
            CreatedBy = lecturer.Id,
            CreatedAt = DateTime.UtcNow
        };
        var version = new RubricVersion
        {
            Id = Guid.NewGuid(),
            TemplateId = template.Id,
            Template = template,
            CourseId = versionCourse?.Id,
            VersionNo = 1,
            MaxScore = versionMaxScore,
            IsSealed = versionSealed,
            IsActive = versionActive,
            CreatedBy = lecturer.Id,
            CreatedAt = DateTime.UtcNow
        };
        var criterionId = Guid.NewGuid();
        if (versionHasCriteria)
            version.Criteria.Add(new RubricCriterion
            {
                Id = criterionId,
                RubricVersionId = version.Id,
                RubricVersion = version,
                OrderNo = 1,
                Description = invalidCriterion ? " " : "Accuracy",
                MaxPoints = criterionMaxPoints
            });

        var session = new ExamSession
        {
            Id = Guid.NewGuid(),
            CourseId = course.Id,
            Course = course,
            RubricMode = mode,
            RubricVersionId = selectVersion ? version.Id : null,
            RubricVersion = selectVersion && versionBelongsToCourse ? version : null,
            CreatedBy = lecturer.Id,
            Title = "Publish session",
            StartTime = DateTime.UtcNow.AddHours(1),
            EndTime = DateTime.UtcNow.AddHours(2),
            TimeLimitPerAnswerSeconds = 120,
            MaxFollowUps = 2,
            Status = status,
            CreatedAt = DateTime.UtcNow
        };

        if (addQuestion)
        {
            var questionCount = addSecondQuestion ? 2 : 1;
            for (var orderNo = 1; orderNo <= questionCount; orderNo++)
            {
                var question = new Question
                {
                    Id = Guid.NewGuid(),
                    ExamSessionId = session.Id,
                    OrderNo = orderNo,
                    Content = $"Question {orderNo}",
                    Rubric = addLegacyRubric ? NewLegacyRubric() : null
                };
                if (question.Rubric is not null)
                    question.Rubric.QuestionId = question.Id;
                session.Questions.Add(question);
            }
        }

        db.Roles.AddRange(lecturerRole, studentRole);
        db.Users.AddRange(lecturer, student);
        db.Courses.Add(course);
        db.RubricTemplates.Add(template);
        db.RubricVersions.Add(version);
        db.ExamSessions.Add(session);
        if (addParticipant)
            db.Participants.Add(new Participant
            {
                Id = Guid.NewGuid(),
                ExamSessionId = session.Id,
                ExamSession = session,
                StudentId = student.Id,
                Student = student,
                JoinedAt = DateTime.UtcNow
            });
        await db.SaveChangesAsync();

        return new Seed(lecturer.Id, course.Id, session.Id, version.Id, criterionId);
    }

    private static Rubric NewLegacyRubric()
    {
        var rubric = new Rubric { Id = Guid.NewGuid(), MaxScore = 5m };
        rubric.Criteria.Add(new RubricCriterion
        {
            Id = Guid.NewGuid(),
            RubricId = rubric.Id,
            Rubric = rubric,
            OrderNo = 1,
            Description = "Accuracy",
            MaxPoints = 5m
        });
        return rubric;
    }

    private static Course NewCourse(string code) => new()
    {
        Id = Guid.NewGuid(),
        Code = code,
        Name = $"Course {code}"
    };

    private static User NewUser(Guid id, Role role, string email) => new()
    {
        Id = id,
        RoleId = role.Id,
        Role = role,
        FullName = "Test User",
        Email = email,
        Username = id.ToString("N"),
        IsActive = true,
        CreatedAt = DateTime.UtcNow
    };

    private sealed record Seed(Guid LecturerId, Guid CourseId, Guid SessionId, Guid VersionId, Guid CriterionId);

    private sealed class TestCurrentUser(Guid id) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid Id => id;
        public string? Role => Roles.Lecturer;
        public bool IsInRole(string requestedRole) => requestedRole == Roles.Lecturer;
    }

    private sealed class TestAdministrator(Guid id) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid Id => id;
        public string? Role => Roles.Administrator;
        public bool IsInRole(string requestedRole) => requestedRole == Roles.Administrator;
    }
}
