using AiOralExam.Modules.AccessConfig.Application.QuestionBank;
using AiOralExam.Modules.AccessConfig.Application.QuestionGeneration;
using AiOralExam.Modules.AccessConfig.Application.AuditLogs;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AiOralExam.UnitTests;

public sealed class QuestionBankServiceTests
{
    [Fact]
    public async Task Create_starts_as_draft_with_an_immutable_initial_revision_and_audit_entry()
    {
        await using var db = CreateDbContext();
        var data = await SeedAsync(db);
        var service = new QuestionBankService(db, new TestCurrentUser(data.Lecturer.Id));

        var item = await service.CreateAsync(data.Course.Id,
            new CreateQuestionBankItemRequest("  Topic  ", BloomLevel.Understand, "  Question  "),
            CancellationToken.None);

        Assert.Equal("Draft", item.Status);
        Assert.Equal("Topic", item.Topic);
        Assert.Equal("Question", item.Content);
        var revision = Assert.Single(item.Revisions);
        Assert.Equal(1, revision.RevisionNo);
        Assert.Equal("Question", revision.Content);
        Assert.Equal(AuditActions.Create, Assert.Single(db.AuditLogs).Action);
    }

    [Fact]
    public async Task Import_persists_every_valid_row_as_draft_with_its_revision_and_audit()
    {
        await using var db = CreateDbContext();
        var data = await SeedAsync(db);
        var service = new QuestionBankService(db, new TestCurrentUser(data.Lecturer.Id));

        var imported = await service.ImportAsync(data.Course.Id,
            new ImportQuestionBankRequest([
                new ImportQuestionBankItem("Topic A", BloomLevel.Remember, "Question A"),
                new ImportQuestionBankItem("Topic B", BloomLevel.Analyze, "Question B")
            ]), CancellationToken.None);

        Assert.Equal(2, imported.Count);
        Assert.All(imported, item =>
        {
            Assert.Equal("Draft", item.Status);
            Assert.Equal("Import", item.Origin);
            Assert.Equal(1, Assert.Single(item.Revisions).RevisionNo);
        });
        Assert.Equal(2, db.QuestionBankItems.Count());
        Assert.Equal(2, db.QuestionBankRevisions.Count());
        Assert.Equal(2, db.AuditLogs.Count());
    }

    [Fact]
    public async Task Import_invalid_rows_report_field_paths_without_partial_persistence()
    {
        await using var db = CreateDbContext();
        var data = await SeedAsync(db);
        var service = new QuestionBankService(db, new TestCurrentUser(data.Lecturer.Id));

        var error = await Assert.ThrowsAsync<ValidationException>(() => service.ImportAsync(data.Course.Id,
            new ImportQuestionBankRequest([
                new ImportQuestionBankItem(" ", BloomLevel.Remember, " "),
                new ImportQuestionBankItem("Topic", (BloomLevel)99, "Question")
            ]), CancellationToken.None));

        Assert.Contains("items[0].content", error.Details!.Keys);
        Assert.Contains("items[0].topic", error.Details.Keys);
        Assert.Contains("items[1].bloom", error.Details.Keys);
        Assert.Empty(db.QuestionBankItems);
        Assert.Empty(db.QuestionBankRevisions);
        Assert.Empty(db.AuditLogs);
    }

    [Fact]
    public async Task Import_rejects_case_insensitive_duplicates_with_row_errors()
    {
        await using var db = CreateDbContext();
        var data = await SeedAsync(db);
        var service = new QuestionBankService(db, new TestCurrentUser(data.Lecturer.Id));

        var error = await Assert.ThrowsAsync<ValidationException>(() => service.ImportAsync(data.Course.Id,
            new ImportQuestionBankRequest([
                new ImportQuestionBankItem("Topic", BloomLevel.Apply, "  Same question "),
                new ImportQuestionBankItem("Topic", BloomLevel.Apply, "same QUESTION")
            ]), CancellationToken.None));

        Assert.Contains("items[0].content", error.Details!.Keys);
        Assert.Contains("items[1].content", error.Details.Keys);
        Assert.Empty(db.QuestionBankItems);
        Assert.Empty(db.AuditLogs);
    }

    [Fact]
    public async Task Import_rejects_content_already_in_the_course()
    {
        await using var db = CreateDbContext();
        var data = await SeedAsync(db);
        var service = new QuestionBankService(db, new TestCurrentUser(data.Lecturer.Id));
        await service.CreateAsync(data.Course.Id,
            new CreateQuestionBankItemRequest("Topic", BloomLevel.Apply, "Same question"),
            CancellationToken.None);

        var error = await Assert.ThrowsAsync<ConflictException>(() => service.ImportAsync(data.Course.Id,
            new ImportQuestionBankRequest([
                new ImportQuestionBankItem("Topic", BloomLevel.Apply, " same QUESTION ")
            ]), CancellationToken.None));

        Assert.Equal("question_bank_duplicate", error.Code);
        Assert.Single(db.QuestionBankItems);
        Assert.Single(db.AuditLogs);
    }

    [Fact]
    public async Task Draft_update_creates_a_new_revision_and_approved_items_cannot_be_edited()
    {
        await using var db = CreateDbContext();
        var data = await SeedAsync(db);
        var service = new QuestionBankService(db, new TestCurrentUser(data.Lecturer.Id));
        var created = await service.CreateAsync(data.Course.Id,
            new CreateQuestionBankItemRequest("Topic", BloomLevel.Apply, "Revision one"),
            CancellationToken.None);

        var updated = await service.UpdateDraftAsync(data.Course.Id, created.Id,
            new UpdateQuestionBankItemRequest("Updated topic", BloomLevel.Analyze, "Revision two"),
            CancellationToken.None);

        Assert.Equal("Revision two", updated.Content);
        Assert.Equal(new[] { 2, 1 }, updated.Revisions.Select(revision => revision.RevisionNo));
        Assert.Equal(new[] { "Revision two", "Revision one" }, updated.Revisions.Select(revision => revision.Content));

        await service.ApproveAsync(data.Course.Id, created.Id, CancellationToken.None);
        var error = await Assert.ThrowsAsync<ConflictException>(() => service.UpdateDraftAsync(data.Course.Id, created.Id,
            new UpdateQuestionBankItemRequest("Changed", BloomLevel.Remember, "Not allowed"),
            CancellationToken.None));
        Assert.Equal("question_bank_locked", error.Code);
    }

    [Fact]
    public async Task Approval_and_rejection_are_draft_only_and_retries_do_not_add_audit_rows()
    {
        await using var db = CreateDbContext();
        var data = await SeedAsync(db);
        var service = new QuestionBankService(db, new TestCurrentUser(data.Lecturer.Id));
        var approved = await service.CreateAsync(data.Course.Id,
            new CreateQuestionBankItemRequest("Topic", BloomLevel.Apply, "Approved"),
            CancellationToken.None);
        var rejected = await service.CreateAsync(data.Course.Id,
            new CreateQuestionBankItemRequest("Topic", BloomLevel.Apply, "Rejected"),
            CancellationToken.None);

        Assert.Equal("Approved", (await service.ApproveAsync(data.Course.Id, approved.Id, CancellationToken.None)).Status);
        await service.ApproveAsync(data.Course.Id, approved.Id, CancellationToken.None);
        Assert.Equal("Rejected", (await service.RejectAsync(data.Course.Id, rejected.Id, CancellationToken.None)).Status);
        var error = await Assert.ThrowsAsync<ConflictException>(
            () => service.ApproveAsync(data.Course.Id, rejected.Id, CancellationToken.None));

        Assert.Equal("question_bank_status_conflict", error.Code);
        Assert.Equal(4, db.AuditLogs.Count());
    }

    [Fact]
    public async Task Attach_uses_the_approved_revision_snapshot_and_preserves_order()
    {
        await using var db = CreateDbContext();
        var data = await SeedAsync(db, includeExistingQuestion: true);
        var service = new QuestionBankService(db, new TestCurrentUser(data.Lecturer.Id));
        var first = await service.CreateAsync(data.Course.Id,
            new CreateQuestionBankItemRequest("Topic", BloomLevel.Apply, "First from bank"),
            CancellationToken.None);
        var second = await service.CreateAsync(data.Course.Id,
            new CreateQuestionBankItemRequest("Topic", BloomLevel.Analyze, "Second from bank"),
            CancellationToken.None);
        await service.ApproveAsync(data.Course.Id, first.Id, CancellationToken.None);
        await service.ApproveAsync(data.Course.Id, second.Id, CancellationToken.None);

        var attached = await service.AttachApprovedAsync(data.Session.Id,
            new AttachQuestionBankRequest([second.Id, first.Id]), CancellationToken.None);

        Assert.Equal(new[] { second.Id, first.Id }, attached);
        var questions = await db.Questions.Where(question => question.ExamSessionId == data.Session.Id)
            .OrderBy(question => question.OrderNo).ToListAsync();
        Assert.Equal(new[] { 3, 4, 5 }, questions.Select(question => question.OrderNo));
        Assert.Equal("Existing question", questions[0].Content);
        Assert.Equal("Second from bank", questions[1].Content);
        Assert.Equal("First from bank", questions[2].Content);
        Assert.NotNull(questions[1].SourceBankRevisionId);
        Assert.Equal(second.Revisions[0].Id, questions[1].SourceBankRevisionId);
        Assert.Equal(6, db.AuditLogs.Count());
    }

    [Fact]
    public async Task Attach_rejects_drafts_duplicates_and_repeat_attach_without_partial_writes()
    {
        await using var db = CreateDbContext();
        var data = await SeedAsync(db);
        var service = new QuestionBankService(db, new TestCurrentUser(data.Lecturer.Id));
        var draft = await service.CreateAsync(data.Course.Id,
            new CreateQuestionBankItemRequest("Topic", BloomLevel.Apply, "Draft question"),
            CancellationToken.None);
        var duplicateRequestError = await Assert.ThrowsAsync<ValidationException>(() =>
            service.AttachApprovedAsync(data.Session.Id, new AttachQuestionBankRequest([draft.Id, draft.Id]), CancellationToken.None));
        Assert.Contains("itemIds", duplicateRequestError.Details!.Keys);

        var statusError = await Assert.ThrowsAsync<ConflictException>(() =>
            service.AttachApprovedAsync(data.Session.Id, new AttachQuestionBankRequest([draft.Id]), CancellationToken.None));
        Assert.Equal("question_bank_item_not_approved", statusError.Code);

        await service.ApproveAsync(data.Course.Id, draft.Id, CancellationToken.None);
        await service.AttachApprovedAsync(data.Session.Id, new AttachQuestionBankRequest([draft.Id]), CancellationToken.None);
        var repeatError = await Assert.ThrowsAsync<ConflictException>(() =>
            service.AttachApprovedAsync(data.Session.Id, new AttachQuestionBankRequest([draft.Id]), CancellationToken.None));

        Assert.Equal("question_bank_item_already_attached", repeatError.Code);
        Assert.Single(db.Questions);
    }

    [Fact]
    public async Task Lecturer_cannot_access_an_unassigned_course()
    {
        await using var db = CreateDbContext();
        var data = await SeedAsync(db);
        var service = new QuestionBankService(db, new TestCurrentUser(Guid.NewGuid()));

        var error = await Assert.ThrowsAsync<ForbiddenException>(
            () => service.ListAsync(data.Course.Id, null, CancellationToken.None));

        Assert.Contains("not assigned", error.Message);
        Assert.Empty(db.QuestionBankItems);
    }

    private static AccessConfigDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AccessConfigDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AccessConfigDbContext(options);
    }

    private static async Task<Seed> SeedAsync(AccessConfigDbContext db, bool includeExistingQuestion = false)
    {
        var role = new Role { Id = Guid.NewGuid(), Name = Roles.Lecturer };
        var lecturer = new User
        {
            Id = Guid.NewGuid(),
            RoleId = role.Id,
            Role = role,
            FullName = "Question Bank Lecturer",
            Email = $"{Guid.NewGuid():N}@example.test",
            Username = Guid.NewGuid().ToString("N"),
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        var course = new Course { Id = Guid.NewGuid(), Code = "QB101", Name = "Question Bank Course" };
        course.Lecturers.Add(new CourseLecturer
        {
            CourseId = course.Id,
            Course = course,
            LecturerId = lecturer.Id,
            Lecturer = lecturer
        });
        var session = new ExamSession
        {
            Id = Guid.NewGuid(),
            CourseId = course.Id,
            Course = course,
            CreatedBy = lecturer.Id,
            Title = "Draft session",
            StartTime = DateTime.UtcNow,
            EndTime = DateTime.UtcNow.AddHours(1),
            Status = ExamSessionStatus.Draft
        };
        if (includeExistingQuestion)
        {
            session.Questions.Add(new Question
            {
                Id = Guid.NewGuid(),
                ExamSessionId = session.Id,
                OrderNo = 3,
                Content = "Existing question"
            });
        }

        db.Roles.Add(role);
        db.Users.Add(lecturer);
        db.Courses.Add(course);
        db.ExamSessions.Add(session);
        await db.SaveChangesAsync();
        return new Seed(lecturer, course, session);
    }

    private sealed record Seed(User Lecturer, Course Course, ExamSession Session);

    private sealed class TestCurrentUser(Guid id) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid Id => id;
        public string? Role => Roles.Lecturer;
        public bool IsInRole(string role) => role == Roles.Lecturer;
    }
}
