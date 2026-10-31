using AiOralExam.Modules.AccessConfig.Application.Materials;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.IO.Compression;
using System.Text;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

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
            Options.Create(new MaterialOptions { ChunkSize = 200 }),
            NullLogger<MaterialService>.Instance);

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

    [Fact]
    public async Task Pptx_ingest_preserves_slide_numbers_in_citations()
    {
        var (db, courseId, lecturerId) = await CreateCourseDb();
        var storage = new MemoryMaterialStorage();
        var service = new MaterialService(db, storage, new TestCurrentUser(lecturerId),
            Options.Create(new MaterialOptions { ChunkSize = 200 }), NullLogger<MaterialService>.Instance);
        await using var input = new MemoryStream(CreatePptx("Plate tectonics moves continental plates.", "Erosion changes the shape of landscapes."));

        var uploaded = await service.UploadAsync(courseId, "geology.pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation", input, input.Length, default);
        await service.IngestAsync(courseId, uploaded.Id, default);
        var slideOne = Assert.Single(await service.RetrieveAsync(courseId, [uploaded.Id], "tectonics", default));
        var slideTwo = Assert.Single(await service.RetrieveAsync(courseId, [uploaded.Id], "erosion", default));

        Assert.Contains("geology.pptx#slide-1#chunk-1", slideOne.Citation);
        Assert.Contains("geology.pptx#slide-2#chunk-1", slideTwo.Citation);
    }

    [Fact]
    public async Task Pdf_ingest_extracts_text_and_preserves_page_numbers_in_citations()
    {
        var (db, courseId, lecturerId) = await CreateCourseDb();
        var storage = new MemoryMaterialStorage();
        var service = new MaterialService(db, storage, new TestCurrentUser(lecturerId),
            Options.Create(new MaterialOptions { ChunkSize = 200 }), NullLogger<MaterialService>.Instance);
        await using var input = new MemoryStream(CreatePdf("Mantle convection moves tectonic plates.", "Erosion reshapes the surface."));

        var uploaded = await service.UploadAsync(courseId, "geology.pdf", "application/pdf", input, input.Length, default);
        await service.IngestAsync(courseId, uploaded.Id, default);
        var firstPage = Assert.Single(await service.RetrieveAsync(courseId, [uploaded.Id], "convection", default));
        var secondPage = Assert.Single(await service.RetrieveAsync(courseId, [uploaded.Id], "erosion", default));

        Assert.Contains("geology.pdf#page-1#chunk-1", firstPage.Citation);
        Assert.Contains("geology.pdf#page-2#chunk-1", secondPage.Citation);
        Assert.Contains("Mantle convection", firstPage.Text);
    }

    [Fact]
    public async Task Upload_deletes_private_file_when_database_persistence_fails()
    {
        var lecturerId = Guid.NewGuid();
        var courseId = Guid.NewGuid();
        var interceptor = new FailMaterialSaveInterceptor();
        var options = new DbContextOptionsBuilder<AccessConfigDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .AddInterceptors(interceptor)
            .Options;
        await using (var seedDb = new AccessConfigDbContext(options))
        {
            seedDb.Courses.Add(new Course
            {
                Id = courseId,
                Code = "SWD392",
                Name = "Software Design",
                Lecturers = [new CourseLecturer { CourseId = courseId, LecturerId = lecturerId }]
            });
            await seedDb.SaveChangesAsync();
        }

        interceptor.FailMaterialWrites = true;
        await using var db = new AccessConfigDbContext(options);
        var storage = new MemoryMaterialStorage();
        var service = new MaterialService(db, storage, new TestCurrentUser(lecturerId),
            Options.Create(new MaterialOptions()), NullLogger<MaterialService>.Instance);
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes("Private course material."));

        await Assert.ThrowsAsync<DbUpdateException>(() => service.UploadAsync(
            courseId, "lecture.txt", "text/plain", input, input.Length, default));

        Assert.Equal(0, storage.Count);
    }

    [Fact]
    public async Task Pptx_ingest_rejects_a_slide_that_exceeds_the_decompressed_size_limit()
    {
        var (db, courseId, lecturerId) = await CreateCourseDb();
        var storage = new MemoryMaterialStorage();
        var service = new MaterialService(db, storage, new TestCurrentUser(lecturerId),
            Options.Create(new MaterialOptions()), NullLogger<MaterialService>.Instance);
        await using var input = new MemoryStream(CreatePptx(new string('x', 1_100_000)));

        var uploaded = await service.UploadAsync(courseId, "large.pptx", "application/octet-stream", input, input.Length, default);
        var error = await Assert.ThrowsAsync<AiOralExam.SharedKernel.Errors.ConflictException>(
            () => service.IngestAsync(courseId, uploaded.Id, default));

        Assert.Equal("material_ingest_failed", error.Code);
        Assert.Equal("Failed", Assert.Single(await service.ListAsync(courseId, default)).Status);
    }

    [Fact]
    public async Task Upload_rejects_a_pdf_without_a_pdf_signature()
    {
        var (db, courseId, lecturerId) = await CreateCourseDb();
        var storage = new MemoryMaterialStorage();
        var service = new MaterialService(db, storage, new TestCurrentUser(lecturerId),
            Options.Create(new MaterialOptions()), NullLogger<MaterialService>.Instance);
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes("not a PDF document"));

        var error = await Assert.ThrowsAsync<AiOralExam.SharedKernel.Errors.ValidationException>(
            () => service.UploadAsync(courseId, "lecture.pdf", "application/pdf", input, input.Length, default));

        Assert.Contains("file", error.Details!.Keys);
        Assert.Equal(0, storage.Count);
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

    private static byte[] CreatePptx(params string[] slideTexts)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteZipEntry(archive, "[Content_Types].xml", "<Types/>");
            WriteZipEntry(archive, "ppt/presentation.xml", "<p:presentation xmlns:p=\"urn:p\"/>");
            for (var index = 0; index < slideTexts.Length; index++)
                WriteZipEntry(archive, $"ppt/slides/slide{index + 1}.xml",
                    $"<p:sld xmlns:p=\"urn:p\" xmlns:a=\"urn:a\"><a:t>{slideTexts[index]}</a:t></p:sld>");
        }
        return stream.ToArray();
    }

    private static void WriteZipEntry(ZipArchive archive, string name, string value)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(value);
    }

    private static byte[] CreatePdf(params string[] pageTexts)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pageTexts)
            builder.AddPage(PageSize.A4).AddText(text, 12, new PdfPoint(25, 700), font);
        return builder.Build();
    }

    private sealed class MemoryMaterialStorage : IMaterialStorage
    {
        private readonly Dictionary<string, byte[]> _files = [];
        public int Count => _files.Count;

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

        public Task DeleteAsync(string key, CancellationToken ct)
        {
            _files.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class FailMaterialSaveInterceptor : SaveChangesInterceptor
    {
        public bool FailMaterialWrites { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (FailMaterialWrites && eventData.Context?.ChangeTracker.Entries<CourseMaterial>()
                    .Any(entry => entry.State == EntityState.Added) == true)
                return ValueTask.FromException<InterceptionResult<int>>(
                    new DbUpdateException("Simulated course-material persistence failure."));
            return ValueTask.FromResult(result);
        }
    }

    private sealed class TestCurrentUser(Guid id) : ICurrentUser
    {
        public bool IsAuthenticated => true;
        public Guid Id => id;
        public string? Role => Roles.Lecturer;
        public bool IsInRole(string requested) => requested.Split(',').Contains(Roles.Lecturer, StringComparer.Ordinal);
    }
}
