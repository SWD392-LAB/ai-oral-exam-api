using System.Security.Cryptography;
using System.Text;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace AiOralExam.Modules.AccessConfig.Application.QuestionBank;

internal static class QuestionBankContentHash
{
    private const string HashExpression = "encode(sha256(convert_to(upper(btrim(regexp_replace(content, '\\s+', ' ', 'g'))), 'UTF8')), 'hex')";

    public static async Task<string> ComputeAsync(AccessConfigDbContext db, string content, CancellationToken ct)
    {
        var hashes = await ComputeManyAsync(db, [content], ct);
        return hashes[0];
    }

    public static async Task<IReadOnlyList<string>> ComputeManyAsync(
        AccessConfigDbContext db,
        IReadOnlyList<string> contents,
        CancellationToken ct)
    {
        if (contents.Count == 0) return [];
        if (db.Database.ProviderName?.Contains("InMemory", StringComparison.Ordinal) == true)
            return contents.Select(ComputeInMemory).ToArray();

        var connection = db.Database.GetDbConnection();
        var closeConnection = connection.State != System.Data.ConnectionState.Open;
        if (closeConnection) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT ordinal, {HashExpression} AS content_hash FROM unnest(@contents) WITH ORDINALITY AS input(content, ordinal) ORDER BY ordinal";
            if (db.Database.CurrentTransaction is { } transaction)
                command.Transaction = transaction.GetDbTransaction();
            command.Parameters.Add(new NpgsqlParameter<string[]>("contents", contents.ToArray())
            {
                NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text
            });

            var hashes = new List<string>(contents.Count);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) hashes.Add(reader.GetString(1));
            return hashes;
        }
        finally
        {
            if (closeConnection) await connection.CloseAsync();
        }
    }

    private static string ComputeInMemory(string content)
    {
        // The in-memory provider is test-only. Production hashes use the SQL expression above,
        // which is also used by database/07_question_bank.sql for existing rows.
        var normalized = string.Join(' ', content.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }
}
