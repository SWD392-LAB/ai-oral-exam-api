using AiOralExam.Modules.AccessConfig.Contracts;
using AiOralExam.Modules.AccessConfig.Domain;
using Microsoft.EntityFrameworkCore;

namespace AiOralExam.Modules.AccessConfig.Infrastructure;

/// <summary>
/// DbContext of F7/F1. NO EF migrations: tables are created by database/01_schema.sql.
/// Table/column names are snake_case (UseSnakeCaseNamingConvention in AccessConfigModule).
/// To change the schema: edit the SQL file first, then update the entities + mapping here to match.
/// </summary>
public sealed class AccessConfigDbContext(DbContextOptions<AccessConfigDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<AiServiceConfig> AiServiceConfigs => Set<AiServiceConfig>();
    public DbSet<Course> Courses => Set<Course>();
    public DbSet<QuestionBankItem> QuestionBankItems => Set<QuestionBankItem>();
    public DbSet<QuestionBankRevision> QuestionBankRevisions => Set<QuestionBankRevision>();
    public DbSet<CourseMaterial> CourseMaterials => Set<CourseMaterial>();
    public DbSet<MaterialChunk> MaterialChunks => Set<MaterialChunk>();
    public DbSet<CourseLecturer> CourseLecturers => Set<CourseLecturer>();
    public DbSet<ExamSession> ExamSessions => Set<ExamSession>();
    public DbSet<Question> Questions => Set<Question>();
    public DbSet<Rubric> Rubrics => Set<Rubric>();
    public DbSet<RubricCriterion> RubricCriteria => Set<RubricCriterion>();
    public DbSet<Participant> Participants => Set<Participant>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // Enums are stored as VARCHAR (matching the CHECK constraints)
        builder.Properties<AiServiceType>().HaveConversion<string>();
        builder.Properties<SpeechLanguage>().HaveConversion<string>();
        builder.Properties<ExamSessionStatus>().HaveConversion<string>();
        builder.Properties<ParticipantStatus>().HaveConversion<string>();
        builder.Properties<QuestionBankOrigin>().HaveConversion<string>();
        builder.Properties<QuestionBankStatus>().HaveConversion<string>();
        builder.Properties<CourseMaterialStatus>().HaveConversion<string>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Role>(e =>
        {
            e.ToTable("roles");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(20).IsRequired();
            e.Property(x => x.Description).HasMaxLength(255);
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            e.Property(x => x.FullName).HasMaxLength(150).IsRequired();
            e.Property(x => x.Email).HasMaxLength(255).IsRequired();
            e.Property(x => x.Username).HasMaxLength(50).IsRequired();
            e.Property(x => x.StudentCode).HasMaxLength(20);
            e.Property(x => x.LecturerCode).HasMaxLength(20);
            e.Property(x => x.ClassCode).HasMaxLength(20);
            e.Property(x => x.IsActive).HasDefaultValue(true);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId)
                .OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => x.RoleId).HasDatabaseName("ix_users_role");
            e.HasIndex(x => x.Username).IsUnique();
            e.HasIndex(x => x.StudentCode).IsUnique();
            e.HasIndex(x => x.LecturerCode).IsUnique();
        });

        b.Entity<AuditLog>(e =>
        {
            e.ToTable("audit_logs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Action).HasMaxLength(100).IsRequired();
            e.Property(x => x.TargetEntity).HasMaxLength(100).IsRequired();
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => x.UserId).HasDatabaseName("ix_audit_user");
            e.HasIndex(x => x.CreatedAt).IsDescending().HasDatabaseName("ix_audit_created");
        });

        b.Entity<AiServiceConfig>(e =>
        {
            e.ToTable("ai_service_configs");
            e.HasKey(x => x.Id);
            e.Property(x => x.ServiceType).HasMaxLength(10).IsRequired();
            e.Property(x => x.ProviderName).HasMaxLength(100).IsRequired();
            e.Property(x => x.EndpointUrl).HasMaxLength(500).IsRequired();
            e.Property(x => x.ApiKeyEncrypted).IsRequired();
            e.Property(x => x.Language).HasMaxLength(20).HasDefaultValue(SpeechLanguage.Vietnamese);
            e.Property(x => x.IsActive).HasDefaultValue(true);
            e.Property(x => x.UpdatedAt).HasDefaultValueSql("now()");
            e.HasOne<User>().WithMany().HasForeignKey(x => x.ConfiguredBy)
                .OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => x.ServiceType).IsUnique().HasFilter("is_active")
                .HasDatabaseName("ux_ai_config_active");
        });

        b.Entity<Course>(e =>
        {
            e.ToTable("courses");
            e.HasKey(x => x.Id);
            e.Property(x => x.Code).HasMaxLength(20).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.HasIndex(x => x.Code).IsUnique();
        });

        b.Entity<QuestionBankItem>(e =>
        {
            e.ToTable("question_bank_items");
            e.HasKey(x => x.Id);
            e.Property(x => x.Topic).HasMaxLength(200).IsRequired();
            e.Property(x => x.Bloom).HasMaxLength(30).IsRequired();
            e.Property(x => x.Content).IsRequired();
            e.Property(x => x.Origin).HasMaxLength(20).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.HasOne(x => x.Course).WithMany().HasForeignKey(x => x.CourseId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Revisions).WithOne(x => x.Item).HasForeignKey(x => x.QuestionBankItemId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.CourseId, x.Status });
        });

        b.Entity<QuestionBankRevision>(e =>
        {
            e.ToTable("question_bank_revisions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Content).IsRequired();
            e.Property(x => x.ProvenanceJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.QuestionBankItemId, x.RevisionNo }).IsUnique();
        });

        b.Entity<CourseMaterial>(e =>
        {
            e.ToTable("course_materials");
            e.HasKey(x => x.Id);
            e.Property(x => x.FileName).HasMaxLength(255).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
            e.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
            e.Property(x => x.StorageKey).HasMaxLength(500).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.Property(x => x.IngestError).HasMaxLength(500);
            e.HasOne(x => x.Course).WithMany().HasForeignKey(x => x.CourseId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Chunks).WithOne(x => x.Material).HasForeignKey(x => x.CourseMaterialId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.CourseId, x.Status });
        });

        b.Entity<MaterialChunk>(e =>
        {
            e.ToTable("material_chunks");
            e.HasKey(x => x.Id);
            e.Property(x => x.Citation).HasMaxLength(300).IsRequired();
            e.Property(x => x.Text).IsRequired();
            e.HasIndex(x => new { x.CourseMaterialId, x.ChunkNo }).IsUnique();
        });

        b.Entity<CourseLecturer>(e =>
        {
            e.ToTable("course_lecturers");
            e.HasKey(x => new { x.CourseId, x.LecturerId });
            e.Property(x => x.AssignedAt).HasDefaultValueSql("now()");
            e.HasOne(x => x.Course).WithMany(c => c.Lecturers).HasForeignKey(x => x.CourseId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Lecturer).WithMany(u => u.CourseAssignments).HasForeignKey(x => x.LecturerId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.LecturerId).HasDatabaseName("ix_course_lecturers_lecturer");
        });

        b.Entity<ExamSession>(e =>
        {
            e.ToTable("exam_sessions");
            e.HasKey(x => x.Id);
            e.Ignore(x => x.IsLocked);
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.MaxFollowUps).HasDefaultValue(2);
            e.Property(x => x.Status).HasMaxLength(20).HasDefaultValue(ExamSessionStatus.Draft);
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
            e.HasOne(x => x.Course).WithMany(c => c.ExamSessions).HasForeignKey(x => x.CourseId)
                .OnDelete(DeleteBehavior.NoAction);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedBy)
                .OnDelete(DeleteBehavior.NoAction);
            e.HasMany(x => x.Questions).WithOne().HasForeignKey(q => q.ExamSessionId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.CourseId).HasDatabaseName("ix_exam_sessions_course");
        });

        b.Entity<Question>(e =>
        {
            e.ToTable("questions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Content).IsRequired();
            e.HasOne(x => x.Rubric).WithOne().HasForeignKey<Rubric>(r => r.QuestionId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ExamSessionId, x.OrderNo }).IsUnique();
            e.Property(x => x.SourceBankRevisionId);
        });

        b.Entity<Rubric>(e =>
        {
            e.ToTable("rubrics");
            e.HasKey(x => x.Id);
            e.Property(x => x.MaxScore).HasPrecision(5, 2).HasDefaultValue(0m).ValueGeneratedOnAddOrUpdate();
            e.HasMany(x => x.Criteria).WithOne(c => c.Rubric).HasForeignKey(c => c.RubricId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RubricCriterion>(e =>
        {
            e.ToTable("rubric_criteria");
            e.HasKey(x => x.Id);
            e.Property(x => x.OrderNo).HasColumnName("order_no");
            e.Property(x => x.Description).HasColumnName("description").IsRequired();
            e.Property(x => x.MaxPoints).HasColumnName("max_points").HasPrecision(5, 2);
            e.HasIndex(x => new { x.RubricId, x.OrderNo }).IsUnique();
        });

        b.Entity<Participant>(e =>
        {
            e.ToTable("participants");
            e.HasKey(x => x.Id);
            e.Property(x => x.JoinedAt).HasDefaultValueSql("now()");
            e.Property(x => x.Status).HasMaxLength(20).HasDefaultValue(ParticipantStatus.Registered);
            e.HasOne(x => x.ExamSession).WithMany().HasForeignKey(x => x.ExamSessionId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId)
                .OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => new { x.ExamSessionId, x.StudentId }).IsUnique();
            e.HasIndex(x => x.StudentId).HasDatabaseName("ix_participants_student");
        });

        // Guid ids are generated in code (Guid.NewGuid). ValueGeneratedNever makes EF always INSERT new
        // entities attached to a navigation instead of mistaking them for existing rows to UPDATE.
        foreach (var entity in b.Model.GetEntityTypes())
            foreach (var key in entity.GetKeys())
                foreach (var prop in key.Properties.Where(p => p.ClrType == typeof(Guid)))
                    prop.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
    }
}
