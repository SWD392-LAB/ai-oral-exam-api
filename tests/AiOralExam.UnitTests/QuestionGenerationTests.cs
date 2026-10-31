using AiOralExam.Modules.AccessConfig.Application.QuestionGeneration;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.Modules.AccessConfig.Application.Materials;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

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

        var result = await new QuestionGenerator(
            db,
            new TestCurrentUser(lecturerId, Roles.Lecturer),
            null!,
            new DeterministicQuestionDraftContentGenerator(),
            Options.Create(new QuestionGenerationProviderOptions()))
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
            Assert.Contains(draft.Citations[0].Quote, draft.Content);
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
            new QuestionGenerator(db, new TestCurrentUser(lecturerId, Roles.Lecturer), null!,
                new DeterministicQuestionDraftContentGenerator(), Options.Create(new QuestionGenerationProviderOptions()))
                .GenerateAsync(courseId, new QuestionGenerationRequest(
                    "RAG", BloomLevel.Remember, 1, [], Guid.NewGuid())));

        Assert.Equal("insufficient_source", error.Code);
    }

    [Fact]
    public async Task Generator_rejects_a_quote_that_is_not_in_the_selected_source()
    {
        var courseId = Guid.NewGuid();
        var lecturerId = Guid.NewGuid();
        await using var db = CreateDb();
        db.Courses.Add(new Course
        {
            Id = courseId,
            Code = "SWD392",
            Name = "Software Design",
            Lecturers = [new CourseLecturer { CourseId = courseId, LecturerId = lecturerId }]
        });
        await db.SaveChangesAsync();
        var model = new StubDraftContentGenerator(
            [new QuestionDraftSuggestion("Question based on evidence", "excerpt-1", "A fabricated claim.")]);
        var generator = new QuestionGenerator(db, new TestCurrentUser(lecturerId, Roles.Lecturer), null!,
            model, Options.Create(new QuestionGenerationProviderOptions()));

        var error = await Assert.ThrowsAsync<AppException>(() => generator.GenerateAsync(courseId,
            new QuestionGenerationRequest("retrieval", BloomLevel.Understand, 1,
                ["Retrieval selects course-scoped evidence."], Guid.NewGuid())));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, error.StatusCode);
        Assert.Equal("question_generation_invalid_output", error.Code);
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

    private sealed class StubDraftContentGenerator(IReadOnlyList<QuestionDraftSuggestion> result) : IQuestionDraftContentGenerator
    {
        public string ProviderName => "Test";
        public Task<IReadOnlyList<QuestionDraftSuggestion>> GenerateAsync(
            QuestionGenerationRequest request,
            IReadOnlyList<QuestionSourceEvidence> evidence,
            CancellationToken ct) => Task.FromResult(result);
    }
}
