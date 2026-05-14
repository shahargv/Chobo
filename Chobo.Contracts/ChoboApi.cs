using System.Reflection;

namespace Chobo.Contracts;

public static class ChoboApi
{
    public const string ProductName = "Chobo";
    public static string ProductVersion { get; } = typeof(ChoboApi).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion ?? "0.0.0-dev";
    public const int ExportVersion = 1;
    public const int SchemaVersion = 8;
    public const int ApiVersion = 1;
    public const string ApiPrefix = "api/v1";
}
