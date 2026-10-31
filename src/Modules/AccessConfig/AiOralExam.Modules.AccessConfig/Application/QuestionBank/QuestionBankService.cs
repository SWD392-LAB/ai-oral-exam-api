using System.Text.Json;
using AiOralExam.Modules.AccessConfig.Application.AuditLogs;
using AiOralExam.Modules.AccessConfig.Application.Courses;
using AiOralExam.Modules.AccessConfig.Application.QuestionGeneration;
using AiOralExam.Modules.AccessConfig.Contracts;
using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using AiOralExam.SharedKernel.Errors;
using AiOralExam.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AiOralExam.Modules.AccessConfig.Application.QuestionBank;

internal sealed class QuestionBankService(
    AccessConfigDbContext db,
    ICurrentUser currentUser) : IQuestionBankService
{
    public async Task<IReadOnlyList<QuestionBankItemDto>> ListAsync(Guid courseId, QuestionBankStatusFilter? status, CancellationToken ct)
    {
        await EnsureCourseAccessAsync(courseId, ct);
        var query = db.QuestionBankItems.AsNoTracking().Include(x => x.Revisions).Where(x => x.CourseId == courseId);
        if (status is not null)
            query = query.Where(x => x.Status == ToStatus(status.Value));
        return (await query.OrderByDescending(x => x.UpdatedAt).ToListAsync(ct)).Select(ToDto).ToList();
    }

    public async Task<QuestionBankItemDto> GetAsync(Guid courseId, Guid itemId, CancellationToken ct)
    {
        await EnsureCourseAccessAsync(courseId, ct);
        return ToDto(await GetItemAsync(courseId, itemId, ct));
    }

    public async Task<QuestionBankItemDto> CreateAsync(Guid courseId, CreateQuestionBankItemRequest request, CancellationToken ct)
    {
        await EnsureCourseAccessAsync(courseId, ct);
        ValidateText(request.Topic, request.Content);
        var contentHash = await QuestionBankContentHash.ComputeAsync(db, request.Content.Trim(), ct);
        await EnsureContentNotUsedAsync(courseId, contentHash, null, ct);
        var item = NewItem(courseId, request.Topic, request.Bloom, request.Content, contentHash, QuestionBankOrigin.Manual, null);
        db.QuestionBankItems.Add(item);
        AddAudit(AuditActions.Create, item.Id, $"Manual question bank item in course {courseId}");
        await SaveQuestionContentChangesAsync(ct);
        return ToDto(item);
    }

    public async Task<IReadOnlyList<QuestionBankItemDto>> ImportAsync(Guid courseId, ImportQuestionBankRequest request, CancellationToken ct)
    {
        await EnsureCourseAccessAsync(courseId, ct);
        var errors = new Dictionary<string, string[]>();
        if (request.Items is null || request.Items.Count == 0)
            errors["items"] = ["At least one item is required."];
        else
        {
            for (var i = 0; i < request.Items.Count; i++)
            {
                var item = request.Items[i];
                if (item is null || string.IsNullOrWhiteSpace(item.Content))
                    errors[$"items[{i}].content"] = ["Content is required."];
                if (item is null || string.IsNullOrWhiteSpace(item.Topic))
                    errors[$"items[{i}].topic"] = ["Topic is required."];
            }
        }
        if (errors.Count > 0) throw new ValidationException(errors);

        var contentHashes = await QuestionBankContentHash.ComputeManyAsync(
            db, request.Items!.Select(x => x.Content.Trim()).ToArray(), ct);
        var duplicates = contentHashes
            .GroupBy(x => x).Where(x => x.Count() > 1).Select(x => x.Key).ToHashSet();
        if (duplicates.Count > 0)
            throw new ValidationException(new Dictionary<string, string[]> { ["items"] = ["Duplicate question content is not allowed in one import batch."] });

        var incomingHashes = contentHashes.ToHashSet(StringComparer.Ordinal);
        var existingHashes = await db.QuestionBankItems.Where(x => x.CourseId == courseId)
            .Select(x => x.ContentHash).ToListAsync(ct);
        if (incomingHashes.Overlaps(existingHashes))
            throw DuplicateQuestion();

        var imported = request.Items!.Select((x, index) => NewItem(
            courseId, x.Topic, x.Bloom, x.Content, contentHashes[index], QuestionBankOrigin.Import, null)).ToList();
        db.QuestionBankItems.AddRange(imported);
        foreach (var item in imported) AddAudit(AuditActions.Create, item.Id, $"Imported question bank item in course {courseId}");
        await SaveQuestionContentChangesAsync(ct);
        return imported.Select(ToDto).ToList();
    }

    public async Task<QuestionBankItemDto> UpdateDraftAsync(Guid courseId, Guid itemId, UpdateQuestionBankItemRequest request, CancellationToken ct)
    {
        await EnsureCourseAccessAsync(courseId, ct);
        ValidateText(request.Topic, request.Content);
        var item = await GetItemAsync(courseId, itemId, ct);
        if (item.Status != QuestionBankStatus.Draft)
            throw new ConflictException("question_bank_locked", "Only Draft bank items can be edited.");
        var contentHash = await QuestionBankContentHash.ComputeAsync(db, request.Content.Trim(), ct);
        await EnsureContentNotUsedAsync(courseId, contentHash, item.Id, ct);
        item.Topic = request.Topic.Trim();
        item.Bloom = request.Bloom.ToString();
        item.Content = request.Content.Trim();
        item.ContentHash = contentHash;
        item.UpdatedAt = DateTime.UtcNow;
        AddRevision(item, null);
        AddAudit(AuditActions.Update, item.Id, "Updated Draft question bank item");
        await SaveQuestionContentChangesAsync(ct);
        return ToDto(item);
    }

    public Task<QuestionBankItemDto> ApproveAsync(Guid courseId, Guid itemId, CancellationToken ct) => ChangeStatusAsync(courseId, itemId, QuestionBankStatus.Approved, ct);
    public Task<QuestionBankItemDto> RejectAsync(Guid courseId, Guid itemId, CancellationToken ct) => ChangeStatusAsync(courseId, itemId, QuestionBankStatus.Rejected, ct);

    public async Task<IReadOnlyList<QuestionBankItemDto>> CreateAiDraftsAsync(Guid courseId, string topic, IReadOnlyList<GeneratedQuestionDraft> drafts, IReadOnlyList<Guid>? sourceMaterialIds, CancellationToken ct)
    {
        await EnsureCourseAccessAsync(courseId, ct);
        if (drafts.Count == 0) return [];
        if (string.IsNullOrWhiteSpace(topic))
            throw new ValidationException(new Dictionary<string, string[]> { ["topic"] = ["A topic is required."] });
        var contentHashes = await QuestionBankContentHash.ComputeManyAsync(
            db, drafts.Select(d => d.Content.Trim()).ToArray(), ct);
        var items = drafts.Select((d, index) => NewItem(
            courseId, topic, d.Bloom, d.Content, contentHashes[index], QuestionBankOrigin.Ai,
            JsonSerializer.Serialize(new { d.PlatformRubricVersionId, d.Citations, SourceMaterialIds = sourceMaterialIds ?? Array.Empty<Guid>() }), d.DraftId)).ToList();
        db.QuestionBankItems.AddRange(items);
        foreach (var item in items) AddAudit(AuditActions.Create, item.Id, "AI generated Draft question bank item");
        await SaveQuestionContentChangesAsync(ct);
        return items.Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<Guid>> AttachApprovedAsync(Guid sessionId, AttachQuestionBankRequest request, CancellationToken ct)
    {
        var session = await db.ExamSessions.Include(x => x.Course).ThenInclude(x => x.Lecturers).Include(x => x.Questions)
            .FirstOrDefaultAsync(x => x.Id == sessionId, ct)
            ?? throw new NotFoundException("exam_session_not_found", "Exam session not found.");
        EnsureCourseAccess(session.Course);
        if (session.Status != ExamSessionStatus.Draft)
            throw new ConflictException("exam_session_locked", "Only Draft sessions can attach question bank items.");
        var ids = request.ItemIds.Distinct().ToArray();
        var items = await db.QuestionBankItems.Include(x => x.Revisions)
            .Where(x => ids.Contains(x.Id) && x.CourseId == session.CourseId && x.Status == QuestionBankStatus.Approved)
            .ToListAsync(ct);
        if (items.Count != ids.Length)
            throw new ConflictException("question_bank_item_not_approved", "Every selected item must be Approved and belong to the session course.");
        var orderedItems = ids.Select(id => items.Single(item => item.Id == id)).ToList();
        var attachedRevisionIds = session.Questions
            .Where(x => x.SourceBankRevisionId.HasValue)
            .Select(x => x.SourceBankRevisionId!.Value)
            .ToHashSet();
        if (orderedItems.Select(x => x.Revisions.OrderByDescending(r => r.RevisionNo).First().Id).Any(attachedRevisionIds.Contains))
            throw new ConflictException("question_bank_item_already_attached", "A selected bank item is already attached to this session.");

        var nextOrder = session.Questions.Count == 0 ? 1 : session.Questions.Max(x => x.OrderNo) + 1;
        foreach (var item in orderedItems)
        {
            var revision = item.Revisions.OrderByDescending(x => x.RevisionNo).First();
            var question = new Question
            {
                Id = Guid.NewGuid(), ExamSessionId = session.Id, OrderNo = nextOrder++, Content = item.Content,
                SourceBankRevisionId = revision.Id
            };
            db.Questions.Add(question);
            AddAudit(AuditActions.Attach, item.Id, $"Attached bank item to exam session {session.Id}");
        }
        await db.SaveChangesAsync(ct);
        return orderedItems.Select(x => x.Id).ToList();
    }

    private async Task<QuestionBankItemDto> ChangeStatusAsync(Guid courseId, Guid itemId, QuestionBankStatus status, CancellationToken ct)
    {
        await EnsureCourseAccessAsync(courseId, ct);
        var item = await GetItemAsync(courseId, itemId, ct);
        if (item.Status != QuestionBankStatus.Draft)
            throw new ConflictException("question_bank_transition_invalid",
                "Only Draft items can be approved or rejected. Create a new Draft to resubmit a rejected item.");
        item.Status = status;
        item.UpdatedAt = DateTime.UtcNow;
        AddAudit(status == QuestionBankStatus.Approved ? AuditActions.Approve : AuditActions.Reject, item.Id, $"Question bank item marked {status}");
        await db.SaveChangesAsync(ct);
        return ToDto(item);
    }

    private async Task<QuestionBankItem> GetItemAsync(Guid courseId, Guid itemId, CancellationToken ct) =>
        await db.QuestionBankItems.Include(x => x.Revisions).FirstOrDefaultAsync(x => x.Id == itemId && x.CourseId == courseId, ct)
        ?? throw new NotFoundException("question_bank_item_not_found", "Question bank item not found.");

    private async Task EnsureCourseAccessAsync(Guid courseId, CancellationToken ct)
    {
        var course = await db.Courses.Include(x => x.Lecturers).FirstOrDefaultAsync(x => x.Id == courseId, ct)
            ?? throw new NotFoundException("course_not_found", "Course not found.");
        EnsureCourseAccess(course);
    }

    private void EnsureCourseAccess(Course course)
    {
        if (!currentUser.IsInRole(Roles.Administrator) && course.Lecturers.All(x => x.LecturerId != currentUser.Id))
            throw new ForbiddenException("You are not assigned to this course.");
    }

    private QuestionBankItem NewItem(Guid courseId, string topic, BloomLevel bloom, string content, string contentHash,
        QuestionBankOrigin origin, string? provenance, Guid? itemId = null)
    {
        var now = DateTime.UtcNow;
        var item = new QuestionBankItem
        {
            Id = itemId ?? Guid.NewGuid(), CourseId = courseId, Topic = topic.Trim(), Bloom = bloom.ToString(), Content = content.Trim(),
            ContentHash = contentHash,
            Origin = origin, Status = QuestionBankStatus.Draft, CreatedBy = currentUser.Id, CreatedAt = now, UpdatedAt = now
        };
        AddRevision(item, provenance);
        return item;
    }

    private void AddRevision(QuestionBankItem item, string? provenance)
    {
        item.Revisions.Add(new QuestionBankRevision
        {
            Id = Guid.NewGuid(), QuestionBankItemId = item.Id,
            RevisionNo = item.Revisions.Count == 0 ? 1 : item.Revisions.Max(x => x.RevisionNo) + 1,
            Content = item.Content, ProvenanceJson = provenance, CreatedBy = currentUser.Id, CreatedAt = DateTime.UtcNow
        });
    }

    private void AddAudit(string action, Guid id, string description) =>
        CourseService.AddAudit(db, currentUser.Id, action, AuditEntities.QuestionBankItem, id, description);

    private async Task SaveQuestionContentChangesAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsQuestionContentDuplicate(ex))
        {
            throw DuplicateQuestion();
        }
    }

    private async Task EnsureContentNotUsedAsync(Guid courseId, string contentHash, Guid? exceptItemId, CancellationToken ct)
    {
        var duplicateExists = await db.QuestionBankItems.AnyAsync(item => item.CourseId == courseId
            && item.ContentHash == contentHash
            && (exceptItemId == null || item.Id != exceptItemId), ct);
        if (duplicateExists) throw DuplicateQuestion();
    }

    private static ConflictException DuplicateQuestion() =>
        new("question_bank_duplicate", "A question with the same content already exists in this course.");

    private static bool IsQuestionContentDuplicate(DbUpdateException exception)
    {
        for (var cause = exception.InnerException; cause is not null; cause = cause.InnerException)
        {
            if (cause is PostgresException
                {
                    SqlState: PostgresErrorCodes.UniqueViolation,
                    ConstraintName: "ux_question_bank_course_content_hash"
                })
                return true;
        }
        return false;
    }

    private static void ValidateText(string topic, string content)
    {
        if (string.IsNullOrWhiteSpace(topic) || string.IsNullOrWhiteSpace(content))
            throw new ValidationException(new Dictionary<string, string[]> { ["content"] = ["Topic and content are required."] });
    }

    private static QuestionBankStatus ToStatus(QuestionBankStatusFilter filter) => (QuestionBankStatus)filter;

    private static QuestionBankItemDto ToDto(QuestionBankItem item) => new(
        item.Id, item.CourseId, item.Topic, item.Bloom, item.Content, item.Origin.ToString(), item.Status.ToString(), item.CreatedAt, item.UpdatedAt,
        item.Revisions.OrderByDescending(x => x.RevisionNo).Select(x => new QuestionBankRevisionDto(x.Id, x.RevisionNo, x.Content, x.ProvenanceJson, x.CreatedAt)).ToList());
}
