using System.Globalization;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Chobo.Contracts;
using ChoboServer.Options;
using Microsoft.Extensions.Options;

namespace ChoboServer.Services;

public interface IRuntimeSettingsService
{
    RuntimeSettingsListDto List();
    RuntimeSettingDto Get(string key);
    Task<RuntimeSettingUpdateResult> SetAsync(string key, string? value, CancellationToken cancellationToken = default);
    Task<RuntimeSettingUpdateResult> UnsetAsync(string key, CancellationToken cancellationToken = default);
    RuntimeSettingsReloadResult Reload();
}

public sealed class RuntimeSettingsService(
    IConfiguration configuration,
    IOptionsMonitor<ChoboRuntimeSettingsOptions> settingsOptions,
    IAuditService audit) : IRuntimeSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    private static readonly IReadOnlyDictionary<string, RuntimeSettingApplyMode> ApplyModeOverrides = new Dictionary<string, RuntimeSettingApplyMode>(StringComparer.OrdinalIgnoreCase)
    {
        ["Chobo:BackupRestore:QueueCapacity"] = RuntimeSettingApplyMode.RestartRequired
    };

    private static readonly IReadOnlyList<OptionDescriptor> OptionDescriptors =
    [
        new(typeof(ChoboStorageOptions), "Chobo", RuntimeSettingApplyMode.RestartRequired, "Changing the data directory requires a server restart and does not move existing data."),
        new(typeof(ChoboSecurityOptions), "Chobo", RuntimeSettingApplyMode.RestartRequired, "Changing encryption configuration requires a restart and can affect credential decryption."),
        new(typeof(ChoboInitOptions), "Chobo:Init", RuntimeSettingApplyMode.RestartRequired, "Initial install settings are only used during bootstrap."),
        new(typeof(ChoboDataRetentionOptions), "Chobo:DataRetention", RuntimeSettingApplyMode.Live, null),
        new(typeof(ChoboSqliteSelfBackupOptions), "Chobo:SqliteSelfBackup", RuntimeSettingApplyMode.Live, null),
        new(typeof(ChoboBackupRestoreOptions), "Chobo:BackupRestore", RuntimeSettingApplyMode.Live, null),
        new(typeof(BackupStorageOperationOptions), "Chobo:BackupStorageOperations", RuntimeSettingApplyMode.Live, null),
        new(typeof(RetentionManagementOptions), "Chobo:RetentionManagement", RuntimeSettingApplyMode.Live, null),
        new(typeof(BackupsGarbageCollectorOptions), "Chobo:BackupsGarbageCollector", RuntimeSettingApplyMode.Live, null),
        new(typeof(ChoboWebOptions), "Chobo:Web", RuntimeSettingApplyMode.RestartRequired, "Changing web hosting settings requires a server restart."),
        new(typeof(ChoboEndpointRewriteOptions), "Chobo:EndpointRewrites", RuntimeSettingApplyMode.RestartRequired, "Endpoint rewrites are applied when dependent services are created; restart after changes."),
        new(typeof(ChoboTestHooksOptions), "Chobo:TestHooks", RuntimeSettingApplyMode.RestartRequired, "Test hooks are deployment-sensitive and require a restart."),
        new(typeof(ChoboRuntimeSettingsOptions), "Chobo:Settings", RuntimeSettingApplyMode.RestartRequired, "Settings API exposure policy is deployment-sensitive and requires a restart.")
    ];

    public RuntimeSettingsListDto List() =>
        new(Discover().OrderBy(x => x.Section, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Select(ToDto).ToList());

    public RuntimeSettingDto Get(string key)
    {
        var setting = Discover().FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
        if (setting is null)
        {
            throw new KeyNotFoundException($"Runtime setting '{key}' was not found or is hidden.");
        }

        return ToDto(setting);
    }

    public async Task<RuntimeSettingUpdateResult> SetAsync(string key, string? value, CancellationToken cancellationToken = default)
    {
        var before = Get(key);
        var setting = FindSetting(key);
        var jsonValue = ConvertToJsonValue(setting.Property.PropertyType, value);
        await WriteOverlayValueAsync(setting.Key, jsonValue, cancellationToken);
        ReloadConfiguration();
        var after = Get(key);
        var unchanged = string.Equals(before.EffectiveValue, after.EffectiveValue, StringComparison.Ordinal);
        await audit.RecordAsync("set", AuditEntityType.RuntimeSetting, setting.Key, new
        {
            key = setting.Key,
            previousOverlayValue = before.OverlayValue,
            newOverlayValue = after.OverlayValue,
            previousEffectiveValue = before.EffectiveValue,
            newEffectiveValue = after.EffectiveValue,
            after.ApplyMode,
            effectiveValueUnchanged = unchanged
        });
        return new RuntimeSettingUpdateResult(after, unchanged, after.ApplyMode == RuntimeSettingApplyMode.RestartRequired);
    }

    public async Task<RuntimeSettingUpdateResult> UnsetAsync(string key, CancellationToken cancellationToken = default)
    {
        var before = Get(key);
        var setting = FindSetting(key);
        await RemoveOverlayValueAsync(setting.Key, cancellationToken);
        ReloadConfiguration();
        var after = Get(key);
        var unchanged = string.Equals(before.EffectiveValue, after.EffectiveValue, StringComparison.Ordinal);
        await audit.RecordAsync("unset", AuditEntityType.RuntimeSetting, setting.Key, new
        {
            key = setting.Key,
            previousOverlayValue = before.OverlayValue,
            newOverlayValue = after.OverlayValue,
            previousEffectiveValue = before.EffectiveValue,
            newEffectiveValue = after.EffectiveValue,
            after.ApplyMode,
            effectiveValueUnchanged = unchanged
        });
        return new RuntimeSettingUpdateResult(after, unchanged, after.ApplyMode == RuntimeSettingApplyMode.RestartRequired);
    }

    public RuntimeSettingsReloadResult Reload()
    {
        ReloadConfiguration();
        var items = List().Items;
        return new RuntimeSettingsReloadResult(
            items,
            items.Count(x => x.ApplyMode == RuntimeSettingApplyMode.Live),
            items.Count(x => x.ApplyMode == RuntimeSettingApplyMode.RestartRequired));
    }

    private RuntimeSetting FindSetting(string key) =>
        Discover().FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"Runtime setting '{key}' was not found or is hidden.");

    private IEnumerable<RuntimeSetting> Discover()
    {
        var hidden = new HashSet<string>(settingsOptions.CurrentValue.HiddenKeys ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in OptionDescriptors)
        {
            var effective = configuration.GetSection(descriptor.Section).Get(descriptor.OptionsType) ?? Activator.CreateInstance(descriptor.OptionsType)!;
            var defaults = Activator.CreateInstance(descriptor.OptionsType)!;
            foreach (var property in descriptor.OptionsType.GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(x => x.CanRead && x.CanWrite))
            {
                var key = $"{descriptor.Section}:{property.Name}";
                if (hidden.Contains(key))
                {
                    continue;
                }

                yield return new RuntimeSetting(descriptor, property, key, property.GetValue(effective), property.GetValue(defaults));
            }
        }
    }

    private RuntimeSettingDto ToDto(RuntimeSetting setting)
    {
        var overlay = ReadOverlayValue(setting.Key);
        var effectiveValue = FormatValue(setting.Property.PropertyType, setting.EffectiveValue);
        var overlayValue = overlay is null ? null : FormatJsonOverlayValue(setting.Property.PropertyType, overlay);
        return new RuntimeSettingDto(
            setting.Key,
            setting.Descriptor.Section,
            setting.Property.Name,
            GetValueType(setting.Property.PropertyType),
            GetApplyMode(setting),
            IsNullable(setting.Property.PropertyType),
            false,
            overlay is not null,
            overlay is not null && !string.Equals(overlayValue, effectiveValue, StringComparison.Ordinal),
            effectiveValue,
            overlayValue,
            FormatValue(setting.Property.PropertyType, setting.DefaultValue),
            GetWarning(setting));
    }

    private static RuntimeSettingApplyMode GetApplyMode(RuntimeSetting setting) =>
        ApplyModeOverrides.TryGetValue(setting.Key, out var overrideMode) ? overrideMode : setting.Descriptor.ApplyMode;

    private static string? GetWarning(RuntimeSetting setting) =>
        string.Equals(setting.Key, "Chobo:BackupRestore:QueueCapacity", StringComparison.OrdinalIgnoreCase)
            ? "Queue capacity is applied when the in-memory queue is constructed; restart after changes."
            : setting.Descriptor.Warning;

    private static RuntimeSettingValueType GetValueType(Type type)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;
        if (target == typeof(bool)) return RuntimeSettingValueType.Boolean;
        if (target == typeof(int) || target == typeof(long)) return RuntimeSettingValueType.Integer;
        if (target == typeof(TimeSpan)) return RuntimeSettingValueType.TimeSpan;
        if (target == typeof(DateTimeOffset) || target == typeof(DateTime)) return RuntimeSettingValueType.DateTimeOffset;
        if (target == typeof(string)) return RuntimeSettingValueType.String;
        return RuntimeSettingValueType.Json;
    }

    private static bool IsNullable(Type type) =>
        !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;

    private static JsonNode? ConvertToJsonValue(Type type, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (IsNullable(type)) return null;
            if ((Nullable.GetUnderlyingType(type) ?? type) == typeof(string)) return JsonValue.Create("");
            throw new InvalidOperationException("A value is required for this setting.");
        }

        var target = Nullable.GetUnderlyingType(type) ?? type;
        if (target == typeof(string)) return JsonValue.Create(value);
        if (target == typeof(bool)) return JsonValue.Create(bool.Parse(value));
        if (target == typeof(int)) return JsonValue.Create(int.Parse(value, CultureInfo.InvariantCulture));
        if (target == typeof(long)) return JsonValue.Create(long.Parse(value, CultureInfo.InvariantCulture));
        if (target == typeof(TimeSpan)) return JsonValue.Create(TimeSpan.Parse(value, CultureInfo.InvariantCulture).ToString("c", CultureInfo.InvariantCulture));
        if (target == typeof(DateTimeOffset)) return JsonValue.Create(DateTimeOffset.Parse(value, CultureInfo.InvariantCulture).ToString("O", CultureInfo.InvariantCulture));
        if (target == typeof(DateTime)) return JsonValue.Create(DateTime.Parse(value, CultureInfo.InvariantCulture).ToString("O", CultureInfo.InvariantCulture));

        try
        {
            return JsonNode.Parse(value) ?? throw new InvalidOperationException("JSON value cannot be empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Value must be valid JSON for setting type {target.Name}.", ex);
        }
    }

    private static string? FormatValue(Type type, object? value)
    {
        if (value is null) return null;
        var target = Nullable.GetUnderlyingType(type) ?? type;
        if (target == typeof(TimeSpan)) return ((TimeSpan)value).ToString("c", CultureInfo.InvariantCulture);
        if (target == typeof(DateTimeOffset)) return ((DateTimeOffset)value).ToString("O", CultureInfo.InvariantCulture);
        if (target == typeof(DateTime)) return ((DateTime)value).ToString("O", CultureInfo.InvariantCulture);
        if (target == typeof(bool)) return ((bool)value).ToString().ToLowerInvariant();
        if (target == typeof(string)) return (string)value;
        if (target.IsPrimitive || target == typeof(decimal)) return Convert.ToString(value, CultureInfo.InvariantCulture);
        return JsonSerializer.Serialize(value, JsonOptions);
    }

    private static string? FormatJsonOverlayValue(Type type, JsonNode node)
    {
        if (node.GetValueKind() == JsonValueKind.Null) return null;
        var target = Nullable.GetUnderlyingType(type) ?? type;
        if (target == typeof(string)) return node.GetValue<string>();
        if (target == typeof(bool)) return node.GetValue<bool>().ToString().ToLowerInvariant();
        if (target == typeof(int)) return node.GetValue<int>().ToString(CultureInfo.InvariantCulture);
        if (target == typeof(long)) return node.GetValue<long>().ToString(CultureInfo.InvariantCulture);
        if (target == typeof(TimeSpan)) return TimeSpan.Parse(node.GetValue<string>(), CultureInfo.InvariantCulture).ToString("c", CultureInfo.InvariantCulture);
        if (target == typeof(DateTimeOffset)) return DateTimeOffset.Parse(node.GetValue<string>(), CultureInfo.InvariantCulture).ToString("O", CultureInfo.InvariantCulture);
        if (target == typeof(DateTime)) return DateTime.Parse(node.GetValue<string>(), CultureInfo.InvariantCulture).ToString("O", CultureInfo.InvariantCulture);
        return node.ToJsonString(JsonOptions);
    }

    private JsonNode? ReadOverlayValue(string key)
    {
        var root = ReadOverlayRoot();
        JsonNode? current = root;
        foreach (var part in key.Split(':'))
        {
            if (current is not JsonObject obj || !obj.TryGetPropertyValue(part, out current))
            {
                return null;
            }
        }

        return current?.DeepClone();
    }

    private async Task WriteOverlayValueAsync(string key, JsonNode? value, CancellationToken cancellationToken)
    {
        var root = ReadOverlayRoot();
        var parts = key.Split(':');
        var current = root;
        foreach (var part in parts.Take(parts.Length - 1))
        {
            if (current[part] is not JsonObject child)
            {
                child = [];
                current[part] = child;
            }

            current = child;
        }

        current[parts[^1]] = value?.DeepClone();
        await WriteOverlayRootAsync(root, cancellationToken);
    }

    private async Task RemoveOverlayValueAsync(string key, CancellationToken cancellationToken)
    {
        var root = ReadOverlayRoot();
        var parts = key.Split(':');
        var current = root;
        foreach (var part in parts.Take(parts.Length - 1))
        {
            if (current[part] is not JsonObject child)
            {
                return;
            }

            current = child;
        }

        current.Remove(parts[^1]);
        PruneEmptyObjects(root);
        await WriteOverlayRootAsync(root, cancellationToken);
    }

    private JsonObject ReadOverlayRoot()
    {
        var path = ChoboConfiguration.GetRuntimeSettingsPath(configuration);
        if (!File.Exists(path)) return [];
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool PruneEmptyObjects(JsonObject obj)
    {
        foreach (var key in obj.Select(x => x.Key).ToList())
        {
            if (obj[key] is JsonObject child && PruneEmptyObjects(child))
            {
                obj.Remove(key);
            }
        }

        return obj.Count == 0;
    }

    private async Task WriteOverlayRootAsync(JsonObject root, CancellationToken cancellationToken)
    {
        var path = ChoboConfiguration.GetRuntimeSettingsPath(configuration);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, root.ToJsonString(JsonOptions), cancellationToken);
        if (File.Exists(path)) File.Replace(temp, path, null);
        else File.Move(temp, path);
    }

    private void ReloadConfiguration()
    {
        if (configuration is IConfigurationRoot root)
        {
            root.Reload();
        }
    }

    private sealed record OptionDescriptor(Type OptionsType, string Section, RuntimeSettingApplyMode ApplyMode, string? Warning);
    private sealed record RuntimeSetting(OptionDescriptor Descriptor, PropertyInfo Property, string Key, object? EffectiveValue, object? DefaultValue)
    {
        public string Section => Descriptor.Section;
        public string Name => Property.Name;
    }
}