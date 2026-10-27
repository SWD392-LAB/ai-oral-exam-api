using AiOralExam.Modules.AccessConfig.Application.Materials;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AiOralExam.UnitTests;

public sealed class MaterialServiceTests
{
    [Fact]
    public async Task Upload_ingest_and_retrieve_are_course_scoped_and_citation_labelled()
    {
        var lecturerId = Guid.NewGuid();
        var courseId = Guid.NewGuid();
        await using var db = new AccessConfigDbContext(new DbContextOptionsBuilder<AccessConfigDbContext>()
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

        var storage = new MemoryMaterialStorage();
        var service = new MaterialService(
            db,
            storage,
            new TestCurrentUser(lecturerId),
            Options.Create(new MaterialOptions { ChunkSize = 200 }));

        await using var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            "Retrieval augmented generation selects course-scoped evidence. " +
            "Citations must point to the source material."));
        var uploaded = await service.UploadAsync(courseId, "lecture.txt", "text/plain", input, input.Length, default);
        var ready = await service.IngestAsync(courseId, uploaded.Id, default);
        var sources = await service.RetrieveAsync(courseId, [uploaded.Id], "retrieval", default);

        Assert.Equal("Ready", ready.Status);
        var source = Assert.Single(sources);
        Assert.Equal(uploaded.Id, source.MaterialId);
        Assert.Contains("lecture.txt#chunk-1", source.Citation);
        Assert.Contains("course-scoped", source.Text);
    }

    private sealed class MemoryMaterialStorage : IMaterialStorage
    {
        private readonly Dictionary<string, byte[]> _files = [];

        public async Task<string> PutAsync(Guid courseId, Guid materialId, string fileName, Stream content, CancellationToken ct)
        {
            var key = $"{courseId:N}/{materialId:N}/{fileName}";
            await using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, ct);
            _files[key] = buffer.ToArray();
            return key;
        }

        public Task<Stream?> OpenReadAsync(string key, CancellationToken ct) =>
            Task.FromResult<Stream?>(_files.TryGetValue(key, out var bytes) ? new MemoryStream(bytes, writable: false) : null);
    }

    private sealed class TestCurrentUser(Guid id) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid Id => id;
        public string? Role => Roles.Lecturer;
        public bool IsInRole(string requested) => requested.Split(',').Contains(Roles.Lecturer, StringComparer.Ordinal);
    }
}
