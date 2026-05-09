using System.Text.Json;

namespace ChoboCli.Infrastructure;

public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task SaveAsync(CliProfile profile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ProfilePath)!);
        await File.WriteAllTextAsync(ProfilePath, JsonSerializer.Serialize(profile, JsonOptions));
    }

    public CliProfile? Load() =>
        File.Exists(ProfilePath)
            ? JsonSerializer.Deserialize<CliProfile>(File.ReadAllText(ProfilePath), JsonOptions)
            : null;

    public static string ProfilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".chobo", "config.json");
}

