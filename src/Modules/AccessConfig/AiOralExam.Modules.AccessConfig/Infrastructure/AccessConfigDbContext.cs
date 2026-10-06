using AiOralExam.Modules.AccessConfig.Contracts;
using AiOralExam.Modules.AccessConfig.Domain;
using Microsoft.EntityFrameworkCore;

namespace AiOralExam.Modules.AccessConfig.Infrastructure;

/// <summary>
/// DbContext cua F7. KHONG dung EF migration: bang duoc tao boi database/01_schema.sql.
/// Ten bang/cot snake_case (UseSnakeCaseNamingConvention o ModuleRegistration).
/// Neu sua schema: sua file SQL truoc, roi sua entity + mapping o day cho khop.
/// </summary>
public sealed class AccessConfigDbContext(DbContextOptions<AccessConfigDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<PasswordSetupToken> PasswordSetupTokens => Set<PasswordSetupToken>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<AiServiceConfig> AiServiceConfigs => Set<AiServiceConfig>();
    public DbSet<Course> Courses => Set<Course>();
    public DbSet<CourseLecturer> CourseLecturers => Set<CourseLecturer>();
    public DbSet<ExamSession> ExamSessions => Set<ExamSession>();
    public DbSet<Question> Questions => Set<Question>();
    public DbSet<Rubric> Rubrics => Set<Rubric>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // Enum luu dang VARCHAR (khop CHECK constraint)
        builder.Properties<AuthProvider>().HaveConversion<string>();
        builder.Properties<TokenPurpose>().HaveConversion<string>();
        builder.Properties<AiServiceType>().HaveConversion<string>();
        builder.Properties<SpeechLanguage>().HaveConversion<string>();
        builder.Properties<ExamSessionStatus>().HaveConversion<string>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Role>(e =>
        {
            e.ToTable("roles");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
        });

        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Email).IsUnique();
            e.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId);
        });

        b.Entity<PasswordSetupToken>(e =>
        {
            e.ToTable("password_setup_tokens");
            e.HasKey(x => x.Id);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
        });

        b.Entity<AuditLog>(e =>
        {
            e.ToTable("audit_logs");
            e.HasKey(x => x.Id);
        });

        b.Entity<AiServiceConfig>(e =>
        {
            e.ToTable("ai_service_configs");
            e.HasKey(x => x.Id);
        });

        b.Entity<Course>(e =>
        {
            e.ToTable("courses");
            e.HasKey(x => x.Id);
        });

        b.Entity<CourseLecturer>(e =>
        {
            e.ToTable("course_lecturers");
            e.HasKey(x => new { x.CourseId, x.LecturerId });
            e.HasOne(x => x.Course).WithMany(c => c.Lecturers).HasForeignKey(x => x.CourseId);
            e.HasOne(x => x.Lecturer).WithMany(u => u.CourseAssignments).HasForeignKey(x => x.LecturerId);
        });

        b.Entity<ExamSession>(e =>
        {
            e.ToTable("exam_sessions");
            e.HasKey(x => x.Id);
            e.Ignore(x => x.IsLocked);
            e.HasOne(x => x.Course).WithMany(c => c.ExamSessions).HasForeignKey(x => x.CourseId);
            e.HasMany(x => x.Questions).WithOne().HasForeignKey(q => q.ExamSessionId);
        });

        b.Entity<Question>(e =>
        {
            e.ToTable("questions");
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Rubric).WithOne().HasForeignKey<Rubric>(r => r.QuestionId);
        });

        b.Entity<Rubric>(e =>
        {
            e.ToTable("rubrics");
            e.HasKey(x => x.Id);
            e.Property(x => x.MaxScore).HasPrecision(5, 2);
        });

        // Id (Guid) do code tu sinh (Guid.NewGuid). ValueGeneratedNever de EF luon INSERT entity moi
        // gan vao navigation thay vi hieu nham la entity cu can UPDATE.
        foreach (var entity in b.Model.GetEntityTypes())
            foreach (var key in entity.GetKeys())
                foreach (var prop in key.Properties.Where(p => p.ClrType == typeof(Guid)))
                    prop.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
    }
}
