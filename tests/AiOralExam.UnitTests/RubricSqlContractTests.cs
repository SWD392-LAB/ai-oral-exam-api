using System.Text.RegularExpressions;

using System.Text.RegularExpressions;

namespace AiOralExam.UnitTests;

public sealed class RubricSqlContractTests
{
    [Fact]
    public void Fresh_schema_and_seed_define_shared_defaults_and_correct_platform_criteria()
    {
        var schema = ReadSql("01_schema.sql");
        var seed = ReadSql("02_seed.sql");

        Assert.Contains("rubric_mode                    VARCHAR(20)  NOT NULL DEFAULT 'SharedVersion'", schema);
        Assert.Contains("'Published', 'Legacy'", seed);
        Assert.Contains("('aaaaaaaa-0000-0000-0000-000000000003', NULL, 'aaaaaaaa-0000-0000-0000-000000000002', 1", seed);
        Assert.Contains("('aaaaaaaa-0000-0000-0000-000000000004', NULL, 'aaaaaaaa-0000-0000-0000-000000000002', 2", seed);
        Assert.Contains("WHERE id = 'aaaaaaaa-0000-0000-0000-000000000002' AND NOT is_sealed", seed);
        Assert.Matches(@"AND NOT EXISTS\s*\(\s*SELECT 1 FROM rubric_versions active\s*WHERE active\.is_active", seed);
    }

    [Fact]
    public void Fresh_schema_and_forward_migrations_require_published_shared_pins_and_nonnull_active_state()
    {
        var schema = ReadSql("01_schema.sql");
        var versionMigration = ReadSql("06_rubric_versions.sql");
        var platformMigration = ReadSql("08_f1_platform_rubrics.sql");

        Assert.Contains("(rubric_mode = 'SharedVersion' AND (rubric_version_id IS NOT NULL OR status = 'Draft'))", schema);
        Assert.Contains("(rubric_mode = 'SharedVersion' AND (rubric_version_id IS NOT NULL OR status = 'Draft'))", versionMigration);
        Assert.Contains("is_active   BOOLEAN       NOT NULL DEFAULT FALSE", schema);
        Assert.Contains("ADD COLUMN IF NOT EXISTS is_active BOOLEAN NOT NULL DEFAULT FALSE", platformMigration);
    }

    [Fact]
    public void Fresh_schema_and_forward_fix_allow_only_sealed_platform_activation()
    {
        foreach (var script in new[] { ReadSql("01_schema.sql"), ReadSql("10_f1_rubric_lifecycle.sql") })
        {
            var trigger = ExtractFunction(script, "fn_protect_rubric_version");

            Assert.Contains("to_jsonb(NEW) - ARRAY['is_active']::TEXT[]", trigger);
            Assert.Contains("IF NEW.is_active THEN", trigger);
            Assert.Contains("Only platform-owned rubric versions can be activated.", trigger);
            Assert.Contains("Only sealed rubric versions can be activated.", trigger);
        }
    }

    [Fact]
    public void Forward_migrations_are_transactional_and_f1_migration_has_correct_seed_values()
    {
        foreach (var name in new[]
                 {
                     "06_rubric_versions.sql", "07_f3_turn_processing.sql", "08_f1_platform_rubrics.sql",
                     "09_f3_processing_lease.sql", "10_f1_rubric_lifecycle.sql"
                 })
        {
            var script = ReadSql(name).TrimEnd();
            Assert.Contains("BEGIN;", script);
            Assert.EndsWith("COMMIT;", script);
        }

        var f1Migration = ReadSql("08_f1_platform_rubrics.sql");
        Assert.Contains("('aaaaaaaa-0000-0000-0000-000000000003', NULL, 'aaaaaaaa-0000-0000-0000-000000000002', 1", f1Migration);
        Assert.Contains("('aaaaaaaa-0000-0000-0000-000000000004', NULL, 'aaaaaaaa-0000-0000-0000-000000000002', 2", f1Migration);
        Assert.Contains("NEW.status = 'Published'", ReadSql("10_f1_rubric_lifecycle.sql"));
        Assert.Contains("ALTER TABLE exam_sessions ALTER COLUMN rubric_mode SET DEFAULT 'SharedVersion'", ReadSql("10_f1_rubric_lifecycle.sql"));
    }

    [Fact]
    public void Publish_gate_is_transition_only_and_published_pins_remain_immutable()
    {
        foreach (var script in new[] { ReadSql("01_schema.sql"), ReadSql("10_f1_rubric_lifecycle.sql") })
        {
            var trigger = ExtractFunction(script, "fn_check_exam_session_rubric");

            Assert.Contains("v_initial_publish := OLD.status = 'Draft' AND NEW.status = 'Published';", trigger);
            Assert.Contains("IF NEW.rubric_mode = 'SharedVersion' AND v_initial_publish", trigger);
            Assert.Contains("NEW.rubric_version_id IS DISTINCT FROM OLD.rubric_version_id", trigger);
            Assert.Contains("ELSIF NEW.rubric_version_id IS DISTINCT FROM OLD.rubric_version_id", trigger);
            Assert.Contains("OLD.status <> 'Draft'", trigger);
            Assert.Matches(@"OLD\.status <> 'Draft'\s+AND \(NEW\.rubric_version_id IS DISTINCT FROM OLD\.rubric_version_id OR NEW\.status = 'Draft'\)", trigger);
            Assert.Contains("JOIN rubrics r ON r.question_id = q.id", trigger);
            Assert.Contains("SET search_path FROM CURRENT;", trigger);
            Assert.Contains("pg_advisory_xact_lock(5067184210567320137)", trigger);
            Assert.DoesNotContain("FOR SHARE OF v, t", trigger);
        }

        var activationLock = ExtractFunction(ReadSql("10_f1_rubric_lifecycle.sql"), "fn_lock_rubric_activation");
        Assert.Contains("pg_advisory_xact_lock(5067184210567320137)", activationLock);
        Assert.Contains("SET search_path FROM CURRENT;", activationLock);
        Assert.Contains("BEFORE UPDATE OF is_active ON rubric_versions", ReadSql("10_f1_rubric_lifecycle.sql"));
        Assert.Contains("v_schema IN ('public', 'information_schema')", ReadSql("10_f1_rubric_lifecycle.sql"));
    }

    private static string ReadSql(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "database", name));

    private static string ExtractFunction(string script, string name)
    {
        var match = Regex.Match(script,
            $"CREATE OR REPLACE FUNCTION {Regex.Escape(name)}\\(\\).*?\\$\\$ LANGUAGE plpgsql(?:\\s+SET search_path FROM CURRENT)?;",
            RegexOptions.Singleline);
        Assert.True(match.Success, $"Function {name} was not found.");
        return match.Value;
    }
}
