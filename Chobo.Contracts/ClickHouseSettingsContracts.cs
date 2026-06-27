using System.Text.Json;

namespace Chobo.Contracts;

public sealed record ClickHouseSettingSourceDto(string Name, JsonElement Value, string Source);

public sealed record ClickHouseSettingsPreviewDto(
    IReadOnlyDictionary<string, JsonElement> Settings,
    IReadOnlyList<ClickHouseSettingSourceDto> Sources);

public sealed record BackupSettingsPreviewRequest(Guid? ClusterId = null, Guid? PolicyId = null);

public sealed record RestoreSettingsPreviewRequest(Guid BackupId, Guid TargetClusterId);
