using AiOralExam.Modules.AccessConfig.Application.QuestionGeneration;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.Modules.AccessConfig.Application.Materials;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;

namespace AiOralExam.UnitTests;

public sealed class QuestionGenerationTests
{
    [Fact]
    public async Task Demo_generator_creates_reviewable_drafts_with_source_citations()
    {
        var lecturerId = Guid.NewGuid();
        var courseId = Guid.NewGuid();
        await using var db = CreateDb();
        db.Courses.Add(new Course
        {
            Id = courseId,
            Code = "SWD392",
            Name = "Software Design",
            Lecturers = [new CourseLecturer { CourseId = courseId, LecturerId = lecturerId }]
        });
        await db.SaveChangesAsync();

        var result = await new DeterministicQuestionGenerator(
            db,
            new TestCurrentUser(lecturerId, Roles.Lecturer),
            null!)
            .GenerateAsync(courseId, new QuestionGenerationRequest(
                "RAG", BloomLevel.Analyze, 2,
                ["Retrieval selects relevant chunks.", "The answer must cite its source."],
                Guid.NewGuid()));

        Assert.Equal("Draft", result.Status);
        Assert.Equal(2, result.Drafts.Count);
        Assert.All(result.Drafts, draft =>
        {
            Assert.Equal("Draft", draft.Status);
            Assert.Equal(BloomLevel.Analyze, draft.Bloom);
            Assert.NotEmpty(draft.Citations);
            Assert.Contains("Analyze", draft.Content);
        });
    }

    [Fact]
    public async Task Demo_generator_rejects_missing_source_instead_of_inventing_citations()
    {
        var courseId = Guid.NewGuid();
        await using var db = CreateDb();
        var lecturerId = Guid.NewGuid();
        db.Courses.Add(new Course
        {
            Id = courseId,
            Code = "SWD392",
            Name = "Software Design",
            Lecturers = [new CourseLecturer { CourseId = courseId, LecturerId = lecturerId }]
        });
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<AiOralExam.SharedKernel.Errors.ConflictException>(() =>
            new DeterministicQuestionGenerator(db, new TestCurrentUser(lecturerId, Roles.Lecturer), null!)
                .GenerateAsync(courseId, new QuestionGenerationRequest(
                    "RAG", BloomLevel.Remember, 1, [], Guid.NewGuid())));

        Assert.Equal("insufficient_source", error.Code);
    }

    private static AccessConfigDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AccessConfigDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private sealed class TestCurrentUser(Guid id, string role) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid Id => id;
        public string? Role => role;
        public bool IsInRole(string requested) => string.Equals(role, requested, StringComparison.Ordinal);
    }
}
