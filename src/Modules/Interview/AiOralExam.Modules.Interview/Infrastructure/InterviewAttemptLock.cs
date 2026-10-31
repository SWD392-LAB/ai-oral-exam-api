using Microsoft.EntityFrameworkCore;

namespace AiOralExam.Modules.Interview.Infrastructure;

internal static class InterviewAttemptLock
{
    public static async Task<T> ExecuteAsync<T>(
        InterviewDbContext db,
        Guid attemptId,
        Func<Task<T>> operation,
        CancellationToken ct)
    {
        if (db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT id FROM interview_attempts WHERE id = {attemptId} FOR UPDATE", ct);
            var result = await operation();
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return result;
        }

        var inMemoryResult = await operation();
        await db.SaveChangesAsync(ct);
        return inMemoryResult;
    }
}
