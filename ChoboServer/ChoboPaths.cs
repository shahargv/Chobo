namespace ChoboServer;

public static class ChoboPaths
{
    public static string GetDataDirectory(string? configured) =>
        string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "data"))
            : Path.GetFullPath(configured);
}
