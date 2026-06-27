namespace Chobo.Contracts;

public enum RuntimeSettingValueType
{
    String,
    Boolean,
    Integer,
    TimeSpan,
    DateTimeOffset,
    Json
}

public enum RuntimeSettingApplyMode
{
    Live,
    RestartRequired
}

public sealed record RuntimeSettingDto(
    string Key,
    string Section,
    string Name,
    RuntimeSettingValueType ValueType,
    RuntimeSettingApplyMode ApplyMode,
    bool IsNullable,
    bool IsReadOnly,
    bool HasOverlayValue,
    bool IsExternallyOverridden,
    string? EffectiveValue,
    string? OverlayValue,
    string? DefaultValue,
    string? Warning);

public sealed record RuntimeSettingsListDto(IReadOnlyList<RuntimeSettingDto> Items);

public sealed record UpdateRuntimeSettingRequest(string? Value);

public sealed record RuntimeSettingUpdateResult(RuntimeSettingDto Setting, bool EffectiveValueUnchanged, bool RestartRequired);

public sealed record RuntimeSettingsReloadResult(IReadOnlyList<RuntimeSettingDto> Items, int LiveCount, int RestartRequiredCount);