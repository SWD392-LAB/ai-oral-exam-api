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
        ValidateBloom(request.Bloom, "bloom");
        var item = NewItem(courseId, request.Topic, request.Bloom, request.Content, QuestionBankOrigin.Manual, null);
        db.QuestionBankItems.Add(item);
        AddAudit(AuditActions.Create, item.Id, $"Manual question bank item in course {courseId}");
        await db.SaveChangesAsync(ct);
        return ToDto(item);
    }

    public async Task<IReadOnlyList<QuestionBankItemDto>> ImportAsync(Guid courseId, ImportQuestionBankRequest request, CancellationToken ct)
    {
        await EnsureCourseAccessAsync(courseId, ct);
        if (request?.Items is not { Count: > 0 } items)
            throw new ValidationException(new Dictionary<string, string[]> { ["items"] = ["At least one item is required."] });

        var errors = new Dictionary<string, string[]>();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item is null || string.IsNullOrWhiteSpace(item.Content))
                errors[$"items[{i}].content"] = ["Content is required."];
            else if (item.Content.Length > 10000)
                errors[$"items[{i}].content"] = ["Content must not exceed 10000 characters."];
            if (item is null || string.IsNullOrWhiteSpace(item.Topic))
                errors[$"items[{i}].topic"] = ["Topic is required."];
            else if (item.Topic.Length > 200)
                errors[$"items[{i}].topic"] = ["Topic must not exceed 200 characters."];
            if (item is not null && !Enum.IsDefined(item.Bloom))
                errors[$"items[{i}].bloom"] = ["Bloom must be Remember, Understand, Apply, or Analyze."];
        }
        if (errors.Count > 0) throw new ValidationException(errors);

        var duplicateRows = items.Select((item, index) => (Content: NormalizeContent(item.Content), Index: index))
            .GroupBy(x => x.Content, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group.Select(row => row.Index));
        foreach (var index in duplicateRows)
            errors[$"items[{index}].content"] = ["Question content must be unique within the import batch."];
        if (errors.Count > 0) throw new ValidationException(errors);

        var existing = await db.QuestionBankItems.Where(x => x.CourseId == courseId)
            .Select(x => x.Content).ToListAsync(ct);
        var existingContent = existing.Select(NormalizeContent).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (items.Any(x => existingContent.Contains(NormalizeContent(x.Content))))
            throw new ConflictException("question_bank_duplicate", "A question with the same content already exists in this course.");

        var imported = items.Select(x => NewItem(courseId, x.Topic, x.Bloom, x.Content, QuestionBankOrigin.Import, null)).ToList();
        db.QuestionBankItems.AddRange(imported);
        foreach (var item in imported) AddAudit(AuditActions.Create, item.Id, $"Imported question bank item in course {courseId}");
        await db.SaveChangesAsync(ct);
        return imported.Select(ToDto).ToList();
    }

    public async Task<QuestionBankItemDto> UpdateDraftAsync(Guid courseId, Guid itemId, UpdateQuestionBankItemRequest request, CancellationToken ct)
    {
        await EnsureCourseAccessAsync(courseId, ct);
        ValidateText(request.Topic, request.Content);
        ValidateBloom(request.Bloom, "bloom");
        var item = await GetItemAsync(courseId, itemId, ct);
        if (item.Status != QuestionBankStatus.Draft)
            throw new ConflictException("question_bank_locked", "Only Draft bank items can be edited.");
        item.Topic = request.Topic.Trim();
        item.Bloom = request.Bloom.ToString();
        item.Content = request.Content.Trim();
        item.UpdatedAt = DateTime.UtcNow;
        AddRevision(item, null);
        AddAudit(AuditActions.Update, item.Id, "Updated Draft question bank item");
        await db.SaveChangesAsync(ct);
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
        var errors = new Dictionary<string, string[]>();
        for (var index = 0; index < drafts.Count; index++)
        {
            var draft = drafts[index];
            if (draft.DraftId == Guid.Empty)
                errors[$"drafts[{index}].draftId"] = ["Draft ID must not be empty."];
            if (string.IsNullOrWhiteSpace(draft.Content))
                errors[$"drafts[{index}].content"] = ["Content is required."];
            if (draft.Content?.Length > 10000)
                errors[$"drafts[{index}].content"] = ["Content must not exceed 10000 characters."];
            if (!Enum.IsDefined(draft.Bloom))
                errors[$"drafts[{index}].bloom"] = ["Bloom must be Remember, Understand, Apply, or Analyze."];
            if (!string.Equals(draft.Status, "Draft", StringComparison.Ordinal))
                errors[$"drafts[{index}].status"] = ["Generated questions must be Drafts."];
        }
        foreach (var duplicate in drafts.Select((draft, index) => (draft.DraftId, index))
            .Where(x => x.DraftId != Guid.Empty)
            .GroupBy(x => x.DraftId)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group))
            errors[$"drafts[{duplicate.index}].draftId"] = ["Draft IDs must be unique within the generation result."];
        if (errors.Count > 0)
            throw new ValidationException(errors);
        var items = drafts.Select(d => NewItem(
            courseId, topic, d.Bloom, d.Content, QuestionBankOrigin.Ai,
            JsonSerializer.Serialize(new { d.PlatformRubricVersionId, d.Citations, SourceMaterialIds = sourceMaterialIds ?? Array.Empty<Guid>() }), d.DraftId)).ToList();
        db.QuestionBankItems.AddRange(items);
        foreach (var item in items) AddAudit(AuditActions.Create, item.Id, "AI generated Draft question bank item");
        await db.SaveChangesAsync(ct);
        return items.Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<Guid>> AttachApprovedAsync(Guid sessionId, AttachQuestionBankRequest request, CancellationToken ct)
    {
        if (request?.ItemIds is not { Count: > 0 } itemIds)
            throw new ValidationException(new Dictionary<string, string[]> { ["itemIds"] = ["At least one question bank item is required."] });
        if (itemIds.Distinct().Count() != itemIds.Count)
            throw new ValidationException(new Dictionary<string, string[]> { ["itemIds"] = ["Question bank item IDs must be unique."] });

        var session = await db.ExamSessions.Include(x => x.Course).ThenInclude(x => x.Lecturers).Include(x => x.Questions)
            .FirstOrDefaultAsync(x => x.Id == sessionId, ct)
            ?? throw new NotFoundException("exam_session_not_found", "Exam session not found.");
        EnsureCourseAccess(session.Course);
        if (session.Status != ExamSessionStatus.Draft)
            throw new ConflictException("exam_session_locked", "Only Draft sessions can attach question bank items.");
        var ids = itemIds.ToArray();
        var items = await db.QuestionBankItems.Include(x => x.Revisions)
            .Where(x => ids.Contains(x.Id) && x.CourseId == session.CourseId && x.Status == QuestionBankStatus.Approved)
            .ToListAsync(ct);
        if (items.Count != ids.Length)
            throw new ConflictException("question_bank_item_not_approved", "Every selected item must be Approved and belong to the session course.");
        var orderedItems = ids.Select(id => items.Single(item => item.Id == id)).ToList();
        var revisions = orderedItems.ToDictionary(
            item => item.Id,
            item => item.Revisions.OrderByDescending(revision => revision.RevisionNo).FirstOrDefault());
        if (revisions.Values.Any(revision => revision is null))
            throw new ConflictException("question_bank_revision_missing", "Every Approved bank item must have a revision.");
        var attachedRevisionIds = session.Questions
            .Where(x => x.SourceBankRevisionId.HasValue)
            .Select(x => x.SourceBankRevisionId!.Value)
            .ToHashSet();
        if (revisions.Values.Any(revision => attachedRevisionIds.Contains(revision!.Id)))
            throw new ConflictException("question_bank_item_already_attached", "A selected bank item is already attached to this session.");

        var nextOrder = session.Questions.Count == 0 ? 1 : session.Questions.Max(x => x.OrderNo) + 1;
        foreach (var item in orderedItems)
        {
            var revision = revisions[item.Id]!;
            var question = new Question
            {
                Id = Guid.NewGuid(), ExamSessionId = session.Id, OrderNo = nextOrder++, Content = revision.Content,
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
        if (item.Status == QuestionBankStatus.Archived)
            throw new ConflictException("question_bank_locked", "Archived bank items cannot change status.");
        if (item.Status == status)
            return ToDto(item);
        if (item.Status != QuestionBankStatus.Draft)
            throw new ConflictException("question_bank_status_conflict", "Only Draft bank items can be approved or rejected.");
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

    private QuestionBankItem NewItem(Guid courseId, string topic, BloomLevel bloom, string content, QuestionBankOrigin origin, string? provenance, Guid? itemId = null)
    {
        var now = DateTime.UtcNow;
        var item = new QuestionBankItem
        {
            Id = itemId ?? Guid.NewGuid(), CourseId = courseId, Topic = topic.Trim(), Bloom = bloom.ToString(), Content = content.Trim(),
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

    private static void ValidateText(string topic, string content)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(topic))
            errors["topic"] = ["Topic is required."];
        else if (topic.Length > 200)
            errors["topic"] = ["Topic must not exceed 200 characters."];
        if (string.IsNullOrWhiteSpace(content))
            errors["content"] = ["Content is required."];
        else if (content.Length > 10000)
            errors["content"] = ["Content must not exceed 10000 characters."];
        if (errors.Count > 0)
            throw new ValidationException(errors);
    }

    private static void ValidateBloom(BloomLevel bloom, string key)
    {
        if (!Enum.IsDefined(bloom))
            throw new ValidationException(new Dictionary<string, string[]>
            {
                [key] = ["Bloom must be Remember, Understand, Apply, or Analyze."]
            });
    }

    private static string NormalizeContent(string content) => content.Trim();

    private static QuestionBankStatus ToStatus(QuestionBankStatusFilter filter) => (QuestionBankStatus)filter;

    private static QuestionBankItemDto ToDto(QuestionBankItem item) => new(
        item.Id, item.CourseId, item.Topic, item.Bloom, item.Content, item.Origin.ToString(), item.Status.ToString(), item.CreatedAt, item.UpdatedAt,
        item.Revisions.OrderByDescending(x => x.RevisionNo).Select(x => new QuestionBankRevisionDto(x.Id, x.RevisionNo, x.Content, x.ProvenanceJson, x.CreatedAt)).ToList());
}
