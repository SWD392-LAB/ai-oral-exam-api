using AiOralExam.Modules.AccessConfig.Application.AuditLogs;
using AiOralExam.Modules.AccessConfig.Application.Courses;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AiOralExam.Modules.AccessConfig.Application.RubricTemplates;

internal sealed class RubricTemplateService(AccessConfigDbContext db, ICurrentUser currentUser) : IRubricTemplateService
{
    private const string NpgsqlProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";
    private const string VersionTemplateConstraint = "fk_rubric_versions_template_course";

    private bool IsAdmin => currentUser.IsInRole(Roles.Administrator);

    public async Task<RubricTemplateDto> CreateAsync(CreateRubricTemplateRequest request, CancellationToken ct)
    {
        EnsureManagementRole();
        var name = ValidateName(request.Name);
        if (request.CourseId == Guid.Empty)
            throw InvalidRequest("courseId", "A course is required.");

        var course = await LoadCourseForManagementAsync(request.CourseId, ct);
        var template = new RubricTemplate
        {
            Id = Guid.NewGuid(),
            CourseId = course.Id,
            Course = course,
            Name = name,
            CreatedBy = currentUser.Id,
            CreatedAt = DateTime.UtcNow
        };

        db.RubricTemplates.Add(template);
        CourseService.AddAudit(db, currentUser.Id, AuditActions.Create, AuditEntities.RubricTemplate,
            template.Id, $"Rubric template {template.Name} in course {course.Code}");
        await db.SaveChangesAsync(ct);

        return ToDto(template, course, 0);
    }

    public async Task<IReadOnlyList<RubricTemplateDto>> ListAsync(Guid? courseId, CancellationToken ct)
    {
        EnsureManagementRole();
        var query = db.RubricTemplates.AsNoTracking().AsQueryable();
        if (!IsAdmin)
        {
            var userId = currentUser.Id;
            query = query.Where(x => x.Course.Lecturers.Any(a => a.LecturerId == userId));
        }
        if (courseId.HasValue)
            query = query.Where(x => x.CourseId == courseId.Value);

        return await query
            .OrderBy(x => x.Course.Code)
            .ThenBy(x => x.Name)
            .Select(x => new RubricTemplateDto(
                x.Id,
                x.CourseId,
                x.Course.Code,
                x.Course.Name,
                x.Name,
                x.CreatedBy,
                x.CreatedAt,
                x.Versions.Count))
            .ToListAsync(ct);
    }

    public async Task<RubricTemplateDto> GetAsync(Guid id, CancellationToken ct)
    {
        var template = await LoadTemplateForManagementAsync(id, ct);
        var versionCount = await db.RubricVersions.CountAsync(x => x.TemplateId == id, ct);
        return ToDto(template, template.Course, versionCount);
    }

    public async Task<RubricTemplateDto> UpdateAsync(Guid id, UpdateRubricTemplateRequest request, CancellationToken ct)
    {
        var name = ValidateName(request.Name);
        var template = await LoadTemplateForManagementAsync(id, ct);
        template.Name = name;
        CourseService.AddAudit(db, currentUser.Id, AuditActions.Update, AuditEntities.RubricTemplate,
            template.Id, $"Rubric template {template.Name} in course {template.Course.Code}");
        await db.SaveChangesAsync(ct);

        var versionCount = await db.RubricVersions.CountAsync(x => x.TemplateId == id, ct);
        return ToDto(template, template.Course, versionCount);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var template = await LoadTemplateForManagementAsync(id, ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await LockTemplateRowAsync(id, ct);
            if (await db.RubricVersions.AnyAsync(x => x.TemplateId == id, ct))
                throw new ConflictException("rubric_template_has_versions",
                    "A rubric template with versions cannot be deleted.");

            db.RubricTemplates.Remove(template);
            await db.SaveChangesAsync(ct);

            CourseService.AddAudit(db, currentUser.Id, AuditActions.Delete, AuditEntities.RubricTemplate,
                template.Id, $"Rubric template {template.Name} in course {template.Course.Code}");
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (Exception ex) when (IsVersionTemplateConstraintViolation(ex))
        {
            throw new ConflictException("rubric_template_has_versions",
                "A rubric template with versions cannot be deleted.");
        }
    }

    private async Task<Course> LoadCourseForManagementAsync(Guid courseId, CancellationToken ct)
    {
        EnsureManagementRole();
        var course = await db.Courses.Include(x => x.Lecturers)
            .FirstOrDefaultAsync(x => x.Id == courseId, ct) ??
            throw new NotFoundException("course_not_found", "Course not found.");
        EnsureCourseAccess(course);
        return course;
    }

    private async Task<RubricTemplate> LoadTemplateForManagementAsync(Guid id, CancellationToken ct)
    {
        EnsureManagementRole();
        var template = await db.RubricTemplates
            .Include(x => x.Course).ThenInclude(x => x.Lecturers)
            .FirstOrDefaultAsync(x => x.Id == id, ct) ??
            throw new NotFoundException("rubric_template_not_found", "Rubric template not found.");
        EnsureCourseAccess(template.Course);
        return template;
    }

    private async Task LockTemplateRowAsync(Guid id, CancellationToken ct)
    {
        if (db.Database.ProviderName != NpgsqlProvider)
            return;

        var rows = await db.RubricTemplates
            .FromSqlInterpolated($"SELECT * FROM rubric_templates WHERE id = {id} FOR UPDATE")
            .ToListAsync(ct);
        if (rows.Count == 0)
            throw new NotFoundException("rubric_template_not_found", "Rubric template not found.");
    }

    private static bool IsVersionTemplateConstraintViolation(Exception exception) =>
        exception switch
        {
            PostgresException postgresException => IsVersionTemplateConstraintViolation(postgresException),
            DbUpdateException { InnerException: PostgresException postgresException } =>
                IsVersionTemplateConstraintViolation(postgresException),
            _ => false
        };

    private static bool IsVersionTemplateConstraintViolation(PostgresException exception) =>
        exception.SqlState == PostgresErrorCodes.ForeignKeyViolation
        && exception.ConstraintName == VersionTemplateConstraint;

    private void EnsureCourseAccess(Course course)
    {
        EnsureManagementRole();
        if (!IsAdmin && course.Lecturers.All(x => x.LecturerId != currentUser.Id))
            throw new ForbiddenException("You are not assigned to this course.");
    }

    private void EnsureManagementRole()
    {
        if (!currentUser.IsAuthenticated)
            throw new UnauthorizedException("unauthenticated", "You need to sign in.");
        if (!IsAdmin && !currentUser.IsInRole(Roles.Lecturer))
            throw new ForbiddenException("Only lecturers and administrators can manage rubric templates.");
    }

    private static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw InvalidRequest("name", "A template name is required.");

        var normalized = name.Trim();
        if (normalized.Length > 200)
            throw InvalidRequest("name", "Template names cannot exceed 200 characters.");
        return normalized;
    }

    private static ValidationException InvalidRequest(string field, string message) =>
        new(new Dictionary<string, string[]> { [field] = [message] });

    private static RubricTemplateDto ToDto(RubricTemplate template, Course course, int versionCount) => new(
        template.Id,
        template.CourseId,
        course.Code,
        course.Name,
        template.Name,
        template.CreatedBy,
        template.CreatedAt,
        versionCount);
}
