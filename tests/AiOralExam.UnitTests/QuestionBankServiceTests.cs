using AiOralExam.Modules.AccessConfig.Application.QuestionBank;
using AiOralExam.Modules.AccessConfig.Application.QuestionGeneration;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;

namespace AiOralExam.UnitTests;

public sealed class QuestionBankServiceTests
{
    [Fact]
    public async Task Reviewed_items_cannot_change_status_without_a_new_draft()
    {
        var (db, courseId, lecturerId) = await CreateCourseDb();
        await using (db)
        {
            var service = new QuestionBankService(db, new TestCurrentUser(lecturerId));
            var approvedDraft = await service.CreateAsync(courseId,
                new CreateQuestionBankItemRequest("RAG", BloomLevel.Understand, "Explain retrieval."), default);
            var approved = await service.ApproveAsync(courseId, approvedDraft.Id, default);
            Assert.Equal("Approved", approved.Status);

            var approveAgain = await Assert.ThrowsAsync<ConflictException>(
                () => service.ApproveAsync(courseId, approvedDraft.Id, default));
            var reverseApproval = await Assert.ThrowsAsync<ConflictException>(
                () => service.RejectAsync(courseId, approvedDraft.Id, default));
            Assert.Equal("question_bank_transition_invalid", approveAgain.Code);
            Assert.Equal("question_bank_transition_invalid", reverseApproval.Code);

            var rejectedDraft = await service.CreateAsync(courseId,
                new CreateQuestionBankItemRequest("RAG", BloomLevel.Understand, "Explain grounding."), default);
            var rejected = await service.RejectAsync(courseId, rejectedDraft.Id, default);
            Assert.Equal("Rejected", rejected.Status);
            var reverseRejection = await Assert.ThrowsAsync<ConflictException>(
                () => service.ApproveAsync(courseId, rejectedDraft.Id, default));
            Assert.Equal("question_bank_transition_invalid", reverseRejection.Code);
        }
    }

    [Fact]
    public async Task Import_rejects_case_and_whitespace_normalized_duplicates_in_one_batch()
    {
        var (db, courseId, lecturerId) = await CreateCourseDb();
        await using (db)
        {
            var service = new QuestionBankService(db, new TestCurrentUser(lecturerId));
            var error = await Assert.ThrowsAsync<ValidationException>(() => service.ImportAsync(courseId,
                new ImportQuestionBankRequest(
                [
                    new ImportQuestionBankItem("RAG", BloomLevel.Remember, "What is retrieval augmented generation?"),
                    new ImportQuestionBankItem("RAG", BloomLevel.Remember, " \twhat   IS retrieval augmented generation?\n ")
                ]), default));

            Assert.Contains("items", error.Details!.Keys);
            Assert.Empty(await db.QuestionBankItems.ToListAsync());
        }
    }

    [Fact]
    public async Task Import_rejects_content_that_already_exists_in_the_course()
    {
        var (db, courseId, lecturerId) = await CreateCourseDb();
        await using (db)
        {
            var service = new QuestionBankService(db, new TestCurrentUser(lecturerId));
            await service.CreateAsync(courseId,
                new CreateQuestionBankItemRequest("RAG", BloomLevel.Understand, "Explain retrieval."), default);

            var error = await Assert.ThrowsAsync<ConflictException>(() => service.ImportAsync(courseId,
                new ImportQuestionBankRequest(
                    [new ImportQuestionBankItem("RAG", BloomLevel.Understand, "\texplain  retrieval.\n")]), default));

            Assert.Equal("question_bank_duplicate", error.Code);
            Assert.Single(await db.QuestionBankItems.ToListAsync());
        }
    }

    private static async Task<(AccessConfigDbContext Db, Guid CourseId, Guid LecturerId)> CreateCourseDb()
    {
        var lecturerId = Guid.NewGuid();
        var courseId = Guid.NewGuid();
        var db = new AccessConfigDbContext(new DbContextOptionsBuilder<AccessConfigDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        db.Courses.Add(new Course
        {
            Id = courseId,
            Code = "SWD392",
            Name = "Software Design",
            Lecturers = [new CourseLecturer { CourseId = courseId, LecturerId = lecturerId }]
        });
        await db.SaveChangesAsync();
        return (db, courseId, lecturerId);
    }

    private sealed class TestCurrentUser(Guid id) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid Id => id;
        public string? Role => Roles.Lecturer;
        public bool IsInRole(string requested) => string.Equals(requested, Roles.Lecturer, StringComparison.Ordinal);
    }
}
