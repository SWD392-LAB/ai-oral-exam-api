using AiOralExam.Modules.AccessConfig.Application.AuditLogs;
using AiOralExam.Modules.AccessConfig.Application.Courses;
using AiOralExam.Modules.AccessConfig.Application.Rubrics;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AiOralExam.Modules.AccessConfig.Application.RubricVersions;

internal sealed class RubricVersionService(AccessConfigDbContext db, ICurrentUser currentUser) : IRubricVersionService
{
    private const decimal MaximumScore = 999.99m;
    private const string NpgsqlProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";
    private const string VersionNumberConstraint = "rubric_versions_template_id_version_no_key";
    private const long GlobalActiveRubricLockId = 0x4652413142555249;

    private bool IsAdmin => currentUser.IsInRole(Roles.Administrator);

    public async Task<RubricVersionDetailDto> CreateAsync(
        Guid templateId, CreateRubricVersionRequest request, CancellationToken ct)
    {
        EnsureAdministrator();
        var criteria = Validate(request);
        var template = await LoadTemplateForManagementAsync(templateId, ct);
        if (template.CourseId is not null)
            throw new ConflictException("rubric_template_not_platform_owned",
                "New rubric versions can only be created for platform-owned templates.");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await LockTemplateRowAsync(templateId, ct);

            var latestVersionNo = await db.RubricVersions
                .Where(x => x.TemplateId == templateId)
                .Select(x => (int?)x.VersionNo)
                .MaxAsync(ct) ?? 0;
            if (latestVersionNo == int.MaxValue)
                throw new ConflictException("rubric_version_number_exhausted", "No further rubric version numbers are available.");

            var version = new RubricVersion
            {
                Id = Guid.NewGuid(),
                TemplateId = template.Id,
                Template = template,
                CourseId = template.CourseId,
                VersionNo = latestVersionNo + 1,
                IsSealed = false,
                CreatedBy = currentUser.Id,
                CreatedAt = DateTime.UtcNow
            };
            db.RubricVersions.Add(version);
            CourseService.AddAudit(db, currentUser.Id, AuditActions.Create, AuditEntities.RubricVersion,
                version.Id, $"Rubric version {version.VersionNo} for template {template.Name}");
            await db.SaveChangesAsync(ct);

            var criterionEntities = criteria.Select(item => new RubricCriterion
            {
                Id = Guid.NewGuid(),
                RubricVersionId = version.Id,
                RubricVersion = version,
                OrderNo = item.OrderNo,
                Description = item.Description,
                MaxPoints = item.MaxPoints
            }).ToList();
            db.RubricCriteria.AddRange(criterionEntities);
            await db.SaveChangesAsync(ct);

            await db.Entry(version).ReloadAsync(ct);
            version.IsSealed = true;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return ToDetail(version, criterionEntities, criteria.Sum(x => x.MaxPoints));
        }
        catch (Exception ex) when (IsVersionNumberConflict(ex))
        {
            throw new ConflictException("rubric_version_number_conflict",
                "A rubric version was created concurrently. Retry the request.");
        }
    }

    public async Task<RubricVersionDetailDto> ActivateAsync(
        Guid templateId, Guid versionId, CancellationToken ct)
    {
        EnsureAdministrator();
        var template = await LoadTemplateForManagementAsync(templateId, ct);
        if (template.CourseId is not null)
            throw new ConflictException("rubric_template_not_platform_owned",
                "Only platform-owned rubric templates can activate shared versions.");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockGlobalActiveRubricAsync(ct);
        await LockTemplateRowAsync(templateId, ct);
        var version = await db.RubricVersions.Include(x => x.Criteria)
            .FirstOrDefaultAsync(x => x.TemplateId == templateId && x.Id == versionId, ct)
            ?? throw new NotFoundException("rubric_version_not_found", "Rubric version not found.");
        if (!version.IsSealed)
            throw new ConflictException("rubric_version_not_sealed", "Only sealed rubric versions can be activated.");

        var activeVersions = await db.RubricVersions
            .Where(x => x.IsActive && x.Id != versionId)
            .ToListAsync(ct);
        foreach (var activeVersion in activeVersions)
            activeVersion.IsActive = false;
        await db.SaveChangesAsync(ct);

        version.IsActive = true;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return ToDetail(version, version.Criteria, version.MaxScore);
    }

    private async Task LockGlobalActiveRubricAsync(CancellationToken ct)
    {
        if (db.Database.ProviderName == NpgsqlProvider)
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({GlobalActiveRubricLockId})", ct);
    }

    public async Task<IReadOnlyList<RubricVersionDto>> ListAsync(Guid templateId, CancellationToken ct)
    {
        await LoadTemplateForManagementAsync(templateId, ct);
        return await db.RubricVersions.AsNoTracking()
            .Where(x => x.TemplateId == templateId)
            .OrderBy(x => x.VersionNo)
            .Select(x => new RubricVersionDto(
                x.Id, x.TemplateId, x.CourseId, x.VersionNo, x.MaxScore, x.IsSealed, x.IsActive,
                x.CreatedBy, x.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task<RubricVersionDetailDto> GetAsync(Guid templateId, Guid versionId, CancellationToken ct)
    {
        await LoadTemplateForManagementAsync(templateId, ct);
        var version = await db.RubricVersions.AsNoTracking()
            .Include(x => x.Criteria)
            .FirstOrDefaultAsync(x => x.TemplateId == templateId && x.Id == versionId, ct) ??
            throw new NotFoundException("rubric_version_not_found", "Rubric version not found.");

        return ToDetail(version, version.Criteria.OrderBy(x => x.OrderNo), version.MaxScore);
    }

    private async Task<RubricTemplate> LoadTemplateForManagementAsync(Guid templateId, CancellationToken ct)
    {
        EnsureManagementRole();
        var template = await db.RubricTemplates
            .Include(x => x.Course).ThenInclude(x => x!.Lecturers)
            .FirstOrDefaultAsync(x => x.Id == templateId, ct) ??
            throw new NotFoundException("rubric_template_not_found", "Rubric template not found.");

        if (!IsAdmin && template.Course is not null
            && template.Course.Lecturers.All(x => x.LecturerId != currentUser.Id))
            throw new ForbiddenException("You are not assigned to this course.");

        return template;
    }

    private void EnsureManagementRole()
    {
        if (!currentUser.IsAuthenticated)
            throw new UnauthorizedException("unauthenticated", "You need to sign in.");
        if (!IsAdmin && !currentUser.IsInRole(Roles.Lecturer))
            throw new ForbiddenException("Only lecturers and administrators can read rubric versions.");
    }

    private void EnsureAdministrator()
    {
        if (!currentUser.IsAuthenticated)
            throw new UnauthorizedException("unauthenticated", "You need to sign in.");
        if (!IsAdmin)
            throw new ForbiddenException("Only administrators can manage platform rubric versions.");
    }

    private async Task LockTemplateRowAsync(Guid templateId, CancellationToken ct)
    {
        if (db.Database.ProviderName != NpgsqlProvider)
            return;

        var rows = await db.RubricTemplates
            .FromSqlInterpolated($"SELECT * FROM rubric_templates WHERE id = {templateId} FOR UPDATE")
            .ToListAsync(ct);
        if (rows.Count == 0)
            throw new NotFoundException("rubric_template_not_found", "Rubric template not found.");
    }

    private static IReadOnlyList<CreateRubricCriterionRequest> Validate(CreateRubricVersionRequest request)
    {
        if (request.Criteria is null || request.Criteria.Count == 0)
            throw InvalidCriteria("A rubric version must contain at least one criterion.");
        if (request.Criteria.Any(x => string.IsNullOrWhiteSpace(x.Description)))
            throw InvalidCriteria("Criterion descriptions cannot be empty.");
        if (request.Criteria.Any(x => x.OrderNo <= 0)
            || request.Criteria.Select(x => x.OrderNo).Distinct().Count() != request.Criteria.Count)
            throw InvalidCriteria("Criterion order numbers must be positive and unique.");
        if (request.Criteria.Any(x => x.MaxPoints <= 0 || x.MaxPoints > MaximumScore
            || decimal.Round(x.MaxPoints, 2) != x.MaxPoints))
            throw InvalidCriteria("Criterion max points must be greater than 0, no more than 999.99, and have at most 2 decimal places.");

        var criteria = request.Criteria
            .Select(x => x with { Description = x.Description.Trim() })
            .ToList();
        var total = criteria.Sum(x => x.MaxPoints);
        if (total <= 0 || total > MaximumScore)
            throw InvalidCriteria("The total max points must be between 0.01 and 999.99.");

        return criteria;
    }

    private static ValidationException InvalidCriteria(string message) =>
        new(new Dictionary<string, string[]> { ["criteria"] = [message] });

    private static bool IsVersionNumberConflict(Exception exception) =>
        exception switch
        {
            PostgresException postgresException => IsVersionNumberConflict(postgresException),
            DbUpdateException { InnerException: PostgresException postgresException } =>
                IsVersionNumberConflict(postgresException),
            _ => false
        };

    private static bool IsVersionNumberConflict(PostgresException exception) =>
        exception.SqlState == PostgresErrorCodes.UniqueViolation
        && exception.ConstraintName == VersionNumberConstraint;

    private static RubricVersionDetailDto ToDetail(
        RubricVersion version, IEnumerable<RubricCriterion> criteria, decimal maxScore) => new(
        version.Id,
        version.TemplateId,
        version.CourseId,
        version.VersionNo,
        maxScore,
        version.IsSealed,
        version.IsActive,
        version.CreatedBy,
        version.CreatedAt,
        criteria.OrderBy(x => x.OrderNo)
            .Select(x => new RubricCriterionDto(x.Id, x.OrderNo, x.Description, x.MaxPoints))
            .ToList());
}
