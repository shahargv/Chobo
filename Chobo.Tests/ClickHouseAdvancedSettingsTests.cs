using System.Text.Json;
using ChoboServer.Services;

namespace Chobo.Tests;

public sealed class ClickHouseAdvancedSettingsTests
{
    [Fact]
    public void Normalize_rejects_chobo_managed_settings()
    {
        using var backup = JsonDocument.Parse("{\"base_backup\":\"s3://custom\"}");
        using var restore = JsonDocument.Parse("{\"allow_non_empty_tables\":true}");

        Assert.Throws<ArgumentException>(() => ClickHouseAdvancedSettings.Normalize(ToDictionary(backup), ClickHouseAdvancedSettingsKind.Backup));
        Assert.Throws<ArgumentException>(() => ClickHouseAdvancedSettings.Normalize(ToDictionary(restore), ClickHouseAdvancedSettingsKind.Restore));
    }

    [Fact]
    public void MergeWithSources_applies_policy_over_cluster()
    {
        using var cluster = JsonDocument.Parse("{\"max_backup_bandwidth\":100,\"backup_threads\":2}");
        using var policy = JsonDocument.Parse("{\"backup_threads\":8}");

        var preview = ClickHouseAdvancedSettings.MergeWithSources(
            ("cluster", ClickHouseAdvancedSettings.Normalize(ToDictionary(cluster), ClickHouseAdvancedSettingsKind.Backup)),
            ("policy", ClickHouseAdvancedSettings.Normalize(ToDictionary(policy), ClickHouseAdvancedSettingsKind.Backup)));

        Assert.Equal(100, preview.Settings["max_backup_bandwidth"].GetInt32());
        Assert.Equal(8, preview.Settings["backup_threads"].GetInt32());
        Assert.Contains(preview.Sources, source => source.Name == "backup_threads" && source.Source == "policy");
    }

    [Fact]
    public void ToSettingsClause_renders_supported_values_and_appends_chobo_settings_last()
    {
        using var document = JsonDocument.Parse("{\"max_backup_bandwidth\":100,\"use_same_s3_credentials_for_base_backup\":true,\"s3_storage_class\":\"STANDARD_IA\"}");
        var settings = ClickHouseAdvancedSettings.Normalize(ToDictionary(document), ClickHouseAdvancedSettingsKind.Backup);

        var clause = ClickHouseAdvancedSettings.ToSettingsClause(settings, ("base_backup", "S3('bucket/base')"));

        Assert.Equal(" SETTINGS max_backup_bandwidth = 100, s3_storage_class = 'STANDARD_IA', use_same_s3_credentials_for_base_backup = 1, base_backup = S3('bucket/base')", clause);
    }

    [Fact]
    public void Normalize_rejects_invalid_names_duplicate_case_variants_and_unsupported_values()
    {
        using var invalidName = JsonDocument.Parse("{\"bad-name\":1}");
        using var duplicateCase = JsonDocument.Parse("{\"Backup_Threads\":1,\"backup_threads\":2}");
        using var objectValue = JsonDocument.Parse("{\"backup_threads\":{\"nested\":1}}");
        using var arrayValue = JsonDocument.Parse("{\"backup_threads\":[1]}");
        using var nullValue = JsonDocument.Parse("{\"backup_threads\":null}");

        Assert.Throws<ArgumentException>(() => ClickHouseAdvancedSettings.Normalize(ToDictionary(invalidName), ClickHouseAdvancedSettingsKind.Backup));
        Assert.Throws<ArgumentException>(() => ClickHouseAdvancedSettings.Normalize(ToDictionary(duplicateCase), ClickHouseAdvancedSettingsKind.Backup));
        Assert.Throws<ArgumentException>(() => ClickHouseAdvancedSettings.Normalize(ToDictionary(objectValue), ClickHouseAdvancedSettingsKind.Backup));
        Assert.Throws<ArgumentException>(() => ClickHouseAdvancedSettings.Normalize(ToDictionary(arrayValue), ClickHouseAdvancedSettingsKind.Backup));
        Assert.Throws<ArgumentException>(() => ClickHouseAdvancedSettings.Normalize(ToDictionary(nullValue), ClickHouseAdvancedSettingsKind.Backup));
    }
    private static IReadOnlyDictionary<string, JsonElement> ToDictionary(JsonDocument document) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(document.RootElement.GetRawText())!;
}
