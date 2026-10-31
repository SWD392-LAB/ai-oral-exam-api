using System.IO.Compression;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using AiOralExam.Modules.AccessConfig.Application.AuditLogs;
using AiOralExam.Modules.AccessConfig.Application.Courses;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace AiOralExam.Modules.AccessConfig.Application.Materials;

internal sealed class MaterialService(
    AccessConfigDbContext db,
    IMaterialStorage storage,
    ICurrentUser currentUser,
    IOptions<MaterialOptions> options) : IMaterialService
{
    private readonly MaterialOptions _options = options.Value;

    public async Task<CourseMaterialDto> UploadAsync(Guid courseId, string fileName, string contentType, Stream content, long? declaredLength, CancellationToken ct)
    {
        var course = await EnsureCourseAccessAsync(courseId, ct);
        var safeName = Path.GetFileName(fileName);
        var extension = Path.GetExtension(safeName).ToLowerInvariant();
        if (extension is not ".pdf" and not ".pptx" and not ".txt" and not ".md")
            throw new ValidationException(new Dictionary<string, string[]> { ["file"] = ["Only PDF, PPTX, TXT, or Markdown files are supported."] });
        if (declaredLength is > 0 && declaredLength > _options.MaxBytes)
            throw TooLarge();

        await using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        if (buffer.Length == 0) throw new ValidationException(new Dictionary<string, string[]> { ["file"] = ["Material file cannot be empty."] });
        if (buffer.Length > _options.MaxBytes) throw TooLarge();

        var material = new CourseMaterial
        {
            Id = Guid.NewGuid(), CourseId = courseId, FileName = safeName,
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            SizeBytes = buffer.Length, Sha256 = Convert.ToHexString(SHA256.HashData(buffer.ToArray())),
            Status = CourseMaterialStatus.Pending, CreatedBy = currentUser.Id, CreatedAt = DateTime.UtcNow
        };
        buffer.Position = 0;
        material.StorageKey = await storage.PutAsync(courseId, material.Id, safeName, buffer, ct);
        db.CourseMaterials.Add(material);
        CourseService.AddAudit(db, currentUser.Id, AuditActions.Create, AuditEntities.CourseMaterial, material.Id, $"Uploaded material for course {course.Code}");
        await db.SaveChangesAsync(ct);
        return ToDto(material);
    }

    public async Task<IReadOnlyList<CourseMaterialDto>> ListAsync(Guid courseId, CancellationToken ct)
    {
        await EnsureCourseAccessAsync(courseId, ct);
        return (await db.CourseMaterials.AsNoTracking().Where(x => x.CourseId == courseId).OrderByDescending(x => x.CreatedAt).ToListAsync(ct)).Select(ToDto).ToList();
    }

    public async Task<CourseMaterialDto> IngestAsync(Guid courseId, Guid materialId, CancellationToken ct)
    {
        await EnsureCourseAccessAsync(courseId, ct);
        var material = await db.CourseMaterials.Include(x => x.Chunks).FirstOrDefaultAsync(x => x.Id == materialId && x.CourseId == courseId, ct)
            ?? throw new NotFoundException("material_not_found", "Course material not found.");
        var stream = await storage.OpenReadAsync(material.StorageKey, ct)
            ?? throw new NotFoundException("material_file_not_found", "Material file is not available in private storage.");

        try
        {
            await using (stream)
            {
                var sections = await ExtractSectionsAsync(material.FileName, stream, ct);
                var nextChunkNo = 1;
                var chunks = sections.SelectMany(section => Chunk(section.Text, _options.ChunkSize)
                    .Select((chunk, index) => new MaterialChunk
                    {
                        Id = Guid.NewGuid(), CourseMaterialId = material.Id, ChunkNo = nextChunkNo++,
                        Citation = BuildCitation(material.FileName, section.Locator, index + 1), Text = chunk
                    })).ToList();
                if (chunks.Count == 0) throw new InvalidOperationException("No readable text was extracted from the material.");
                db.MaterialChunks.RemoveRange(material.Chunks);
                db.MaterialChunks.AddRange(chunks);
                material.Chunks = chunks;
                material.Status = CourseMaterialStatus.Ready;
                material.IngestError = null;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            material.Status = CourseMaterialStatus.Failed;
            material.IngestError = "Material text extraction failed.";
            await db.SaveChangesAsync(ct);
            throw new ConflictException("material_ingest_failed", "The material could not be ingested into searchable chunks.");
        }

        await db.SaveChangesAsync(ct);
        return ToDto(material);
    }

    public async Task<IReadOnlyList<MaterialChunkSource>> RetrieveAsync(Guid courseId, IReadOnlyCollection<Guid> materialIds, string topic, CancellationToken ct)
    {
        await EnsureCourseAccessAsync(courseId, ct);
        if (materialIds.Count == 0) return [];
        var chunks = await db.MaterialChunks.AsNoTracking().Include(x => x.Material)
            .Where(x => materialIds.Contains(x.CourseMaterialId) && x.Material.CourseId == courseId && x.Material.Status == CourseMaterialStatus.Ready)
            .ToListAsync(ct);
        var terms = Tokens(topic);
        return chunks.Select(chunk => new
            {
                Chunk = chunk,
                Score = terms.Count == 0 ? 0 : terms.Count(term => chunk.Text.Contains(term, StringComparison.OrdinalIgnoreCase))
            })
            .Where(x => terms.Count == 0 || x.Score > 0)
            .OrderByDescending(x => x.Score).ThenBy(x => x.Chunk.ChunkNo)
            .Take(8)
            .Select(x => new MaterialChunkSource(x.Chunk.CourseMaterialId, x.Chunk.ChunkNo, x.Chunk.Citation, x.Chunk.Text))
            .ToList();
    }

    private async Task<Course> EnsureCourseAccessAsync(Guid courseId, CancellationToken ct)
    {
        var course = await db.Courses.Include(x => x.Lecturers).FirstOrDefaultAsync(x => x.Id == courseId, ct)
            ?? throw new NotFoundException("course_not_found", "Course not found.");
        if (!currentUser.IsInRole(Roles.Administrator) && course.Lecturers.All(x => x.LecturerId != currentUser.Id))
            throw new ForbiddenException("You are not assigned to this course.");
        return course;
    }

    private static async Task<IReadOnlyList<ExtractedSection>> ExtractSectionsAsync(string fileName, Stream stream, CancellationToken ct)
    {
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".txt" or ".md" => [new ExtractedSection(string.Empty, await new StreamReader(stream, Encoding.UTF8, true, 4096, true).ReadToEndAsync(ct))],
            ".pptx" => ExtractPptxSections(stream),
            ".pdf" => ExtractPdfSections(stream),
            _ => throw new InvalidOperationException("Unsupported material format.")
        };
    }

    private static IReadOnlyList<ExtractedSection> ExtractPptxSections(Stream stream)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var slides = archive.Entries
            .Where(x => x.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase) && x.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .Select((entry, index) => (Entry: entry, SlideNo: ParseSlideNumber(entry.FullName, index + 1)))
            .OrderBy(x => x.SlideNo);
        var sections = new List<ExtractedSection>();
        foreach (var (slide, slideNo) in slides)
        {
            using var reader = new StreamReader(slide.Open(), Encoding.UTF8);
            var xml = XDocument.Parse(reader.ReadToEnd());
            var text = string.Join(' ', xml.Descendants().Where(x => x.Name.LocalName == "t").Select(x => x.Value));
            if (!string.IsNullOrWhiteSpace(text)) sections.Add(new ExtractedSection($"slide-{slideNo}", text));
        }
        return sections;
    }

    private static int ParseSlideNumber(string path, int fallback)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.StartsWith("slide", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(name.AsSpan("slide".Length), NumberStyles.None, CultureInfo.InvariantCulture, out var slideNo)
                ? slideNo
                : fallback;
    }

    private static IReadOnlyList<ExtractedSection> ExtractPdfSections(Stream stream)
    {
        using var document = PdfDocument.Open(stream);
        var sections = new List<ExtractedSection>();
        var pageNo = 0;
        foreach (var page in document.GetPages())
        {
            pageNo++;
            var text = ContentOrderTextExtractor.GetText(page);
            if (!string.IsNullOrWhiteSpace(text)) sections.Add(new ExtractedSection($"page-{pageNo}", text));
        }
        return sections;
    }

    private static string BuildCitation(string fileName, string locator, int sectionChunkNo) =>
        string.IsNullOrEmpty(locator)
            ? $"{fileName}#chunk-{sectionChunkNo}"
            : $"{fileName}#{locator}#chunk-{sectionChunkNo}";

    private static List<string> Chunk(string text, int size)
    {
        var normalized = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(normalized)) return [];
        var result = new List<string>();
        for (var offset = 0; offset < normalized.Length; offset += Math.Max(200, size))
            result.Add(normalized[offset..Math.Min(normalized.Length, offset + Math.Max(200, size))]);
        return result;
    }

    private static HashSet<string> Tokens(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .Select(x => x.Trim('"', '.', ',', ':', ';', '?', '!').ToLowerInvariant())
        .Where(x => x.Length >= 3).ToHashSet();

    private sealed record ExtractedSection(string Locator, string Text);

    private static CourseMaterialDto ToDto(CourseMaterial material) => new(material.Id, material.CourseId, material.FileName, material.ContentType, material.SizeBytes, material.Sha256, material.Status.ToString(), material.IngestError, material.CreatedAt);
    private static ValidationException TooLarge() => new(new Dictionary<string, string[]> { ["file"] = ["Material file is too large."] });
}
