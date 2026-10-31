using AiOralExam.Modules.AccessConfig.Application.Materials;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
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

    [Fact]
    public async Task Pptx_ingest_preserves_slide_numbers_in_citations()
    {
        var (db, courseId, lecturerId) = await CreateCourseDb();
        var storage = new MemoryMaterialStorage();
        var service = new MaterialService(db, storage, new TestCurrentUser(lecturerId),
            Options.Create(new MaterialOptions { ChunkSize = 200 }));
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
            Options.Create(new MaterialOptions { ChunkSize = 200 }));
        await using var input = new MemoryStream(CreatePdf("Mantle convection moves tectonic plates.", "Erosion reshapes the surface."));

        var uploaded = await service.UploadAsync(courseId, "geology.pdf", "application/pdf", input, input.Length, default);
        await service.IngestAsync(courseId, uploaded.Id, default);
        var firstPage = Assert.Single(await service.RetrieveAsync(courseId, [uploaded.Id], "convection", default));
        var secondPage = Assert.Single(await service.RetrieveAsync(courseId, [uploaded.Id], "erosion", default));

        Assert.Contains("geology.pdf#page-1#chunk-1", firstPage.Citation);
        Assert.Contains("geology.pdf#page-2#chunk-1", secondPage.Citation);
        Assert.Contains("Mantle convection", firstPage.Text);
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
            for (var index = 0; index < slideTexts.Length; index++)
            {
                var entry = archive.CreateEntry($"ppt/slides/slide{index + 1}.xml");
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write($"<p:sld xmlns:p=\"urn:p\" xmlns:a=\"urn:a\"><a:t>{slideTexts[index]}</a:t></p:sld>");
            }
        }
        return stream.ToArray();
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
