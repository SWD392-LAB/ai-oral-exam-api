using AiOralExam.Modules.AccessConfig.Domain;
using AiOralExam.Modules.AccessConfig.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AiOralExam.UnitTests;

public sealed class RubricMappingTests
{
    [Fact]
    public void Rubric_entities_map_to_schema_tables_and_mode_is_stored_as_string()
    {
        using var db = CreateContext();
        var model = db.Model;

        Assert.Equal("rubric_templates", model.FindEntityType(typeof(RubricTemplate))!.GetTableName());
        Assert.Equal("rubric_versions", model.FindEntityType(typeof(RubricVersion))!.GetTableName());

        var mode = model.FindEntityType(typeof(ExamSession))!.FindProperty(nameof(ExamSession.RubricMode))!;
        Assert.Equal(typeof(string), mode.GetTypeMapping().Converter!.ProviderClrType);
        Assert.Equal(RubricMode.SharedVersion, mode.GetDefaultValue());
        Assert.Equal(ValueGenerated.Never, mode.ValueGenerated);
        Assert.Equal(RubricMode.SharedVersion, new ExamSession().RubricMode);
    }

    [Fact]
    public void Rubric_parent_relationships_are_optional_and_use_schema_delete_behaviors()
    {
        using var db = CreateContext();
        var criterion = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(RubricCriterion))!;
        var legacyRubricFk = criterion.GetForeignKeys().Single(x => x.PrincipalEntityType.ClrType == typeof(Rubric));
        var versionFk = criterion.GetForeignKeys().Single(x => x.PrincipalEntityType.ClrType == typeof(RubricVersion));

        Assert.True(legacyRubricFk.Properties.Single().IsNullable);
        Assert.True(versionFk.Properties.Single().IsNullable);
        Assert.Equal(DeleteBehavior.Cascade, legacyRubricFk.DeleteBehavior);
        Assert.Equal(DeleteBehavior.Restrict, versionFk.DeleteBehavior);
        Assert.Equal("rubric_criteria", criterion.GetTableName());

        var checks = criterion.GetCheckConstraints().ToArray();
        Assert.Equal("(rubric_id IS NOT NULL) <> (rubric_version_id IS NOT NULL)",
            checks.Single(x => x.Name == "ck_rubric_criteria_exactly_one_parent").Sql);
        Assert.Equal("order_no > 0", checks.Single(x => x.Name == "rubric_criteria_order_no_check").Sql);
        Assert.Equal("max_points > 0", checks.Single(x => x.Name == "rubric_criteria_max_points_check").Sql);

        var versionOrderIndex = criterion.GetIndexes().Single(x =>
            x.GetDatabaseName() == "ux_rubric_criteria_version_order");
        Assert.True(versionOrderIndex.IsUnique);
        Assert.Equal("rubric_version_id IS NOT NULL", versionOrderIndex.GetFilter());
    }

    [Fact]
    public void Platform_version_identity_is_course_independent_and_has_one_global_active_version()
    {
        using var db = CreateContext();
        var version = db.Model.FindEntityType(typeof(RubricVersion))!;
        var session = db.Model.FindEntityType(typeof(ExamSession))!;
        var templateFk = version.GetForeignKeys().Single(x => x.PrincipalEntityType.ClrType == typeof(RubricTemplate));
        var sessionVersionFk = session.GetForeignKeys().Single(x => x.PrincipalEntityType.ClrType == typeof(RubricVersion));

        Assert.Equal(new[] { nameof(RubricVersion.TemplateId) },
            templateFk.Properties.Select(x => x.Name));
        Assert.Equal(new[] { nameof(RubricTemplate.Id) },
            templateFk.PrincipalKey.Properties.Select(x => x.Name));
        Assert.Equal(new[] { nameof(ExamSession.RubricVersionId) },
            sessionVersionFk.Properties.Select(x => x.Name));
        Assert.Equal(new[] { nameof(RubricVersion.Id) },
            sessionVersionFk.PrincipalKey.Properties.Select(x => x.Name));
        Assert.Equal(DeleteBehavior.Restrict, sessionVersionFk.DeleteBehavior);
        Assert.True(version.FindProperty(nameof(RubricVersion.CourseId))!.IsNullable);
        Assert.True(db.Model.FindEntityType(typeof(RubricTemplate))!
            .FindProperty(nameof(RubricTemplate.CourseId))!.IsNullable);
        Assert.Contains(version.GetKeys(), x => x != version.FindPrimaryKey() &&
            x.Properties.Select(p => p.Name).SequenceEqual(
                [nameof(RubricVersion.TemplateId), nameof(RubricVersion.VersionNo)]));
        var activeIndex = version.GetIndexes().Single(x => x.GetDatabaseName() == "ux_rubric_versions_single_active");
        Assert.True(activeIndex.IsUnique);
        Assert.Equal(new[] { nameof(RubricVersion.IsActive) }, activeIndex.Properties.Select(x => x.Name));
        Assert.Equal("is_active", activeIndex.GetFilter());
    }

    private static AccessConfigDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AccessConfigDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
}
