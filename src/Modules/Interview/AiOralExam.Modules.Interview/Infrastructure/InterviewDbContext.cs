using AiOralExam.Modules.Interview.Contracts;
using AiOralExam.Modules.Interview.Domain;
using Microsoft.EntityFrameworkCore;

namespace AiOralExam.Modules.Interview.Infrastructure;

/// <summary>
/// DbContext cua F3 - chi map bang module nay so huu. Khong dung EF migration
/// (schema tao boi database/01_schema.sql). FK sang users/questions/exam_sessions chi giu Guid.
/// </summary>
public sealed class InterviewDbContext(DbContextOptions<InterviewDbContext> options) : DbContext(options)
{
    public DbSet<Participant> Participants => Set<Participant>();
    public DbSet<InterviewAttempt> InterviewAttempts => Set<InterviewAttempt>();
    public DbSet<QuestionResponse> QuestionResponses => Set<QuestionResponse>();
    public DbSet<InterviewTurn> InterviewTurns => Set<InterviewTurn>();
    public DbSet<AiEvaluation> AiEvaluations => Set<AiEvaluation>();
    public DbSet<ScoreReview> ScoreReviews => Set<ScoreReview>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder.Properties<ParticipantStatus>().HaveConversion<string>();
        builder.Properties<AttemptStatus>().HaveConversion<string>();
        builder.Properties<TurnType>().HaveConversion<string>();
        builder.Properties<ReviewStatus>().HaveConversion<string>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Participant>(e =>
        {
            e.ToTable("participants");
            e.HasKey(x => x.Id);
            e.HasMany(x => x.Attempts).WithOne(a => a.Participant).HasForeignKey(a => a.ParticipantId);
        });

        b.Entity<InterviewAttempt>(e =>
        {
            e.ToTable("interview_attempts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Status);
            e.Ignore(x => x.CurrentResponse);
            e.HasMany(x => x.Responses).WithOne().HasForeignKey(r => r.AttemptId);
            e.HasOne(x => x.Review).WithOne().HasForeignKey<ScoreReview>(r => r.AttemptId);
        });

        b.Entity<QuestionResponse>(e =>
        {
            e.ToTable("question_responses");
            e.HasKey(x => x.Id);
            e.Ignore(x => x.CurrentTurn);
            e.HasMany(x => x.Turns).WithOne().HasForeignKey(t => t.QuestionResponseId);
            e.HasOne(x => x.Evaluation).WithOne().HasForeignKey<AiEvaluation>(v => v.QuestionResponseId);
        });

        b.Entity<InterviewTurn>(e =>
        {
            e.ToTable("interview_turns");
            e.HasKey(x => x.Id);
        });

        b.Entity<AiEvaluation>(e =>
        {
            e.ToTable("ai_evaluations");
            e.HasKey(x => x.Id);
            e.Property(x => x.SuggestedScore).HasPrecision(5, 2);
        });

        b.Entity<ScoreReview>(e =>
        {
            e.ToTable("score_reviews");
            e.HasKey(x => x.Id);
            e.Property(x => x.FinalScore).HasPrecision(5, 2);
        });

        // Id (Guid) do code tu sinh (Guid.NewGuid). ValueGeneratedNever de EF luon INSERT entity moi
        // gan vao navigation thay vi hieu nham la entity cu can UPDATE.
        foreach (var entity in b.Model.GetEntityTypes())
            foreach (var key in entity.GetKeys())
                foreach (var prop in key.Properties.Where(p => p.ClrType == typeof(Guid)))
                    prop.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
    }
}
