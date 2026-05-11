using ChoboServer.Options;
using ChoboServer.Services;
using Microsoft.Extensions.Options;

namespace ChoboServer;

public static class LocalCommands
{
    public static async Task InitializeAsync(string[] args)
    {
        var config = BuildConfig(args);
        var services = new ServiceCollection().AddChoboServer(config).BuildServiceProvider();
        var init = services.GetRequiredService<IOptions<ChoboInitOptions>>().Value;
        var user = init.AdminUser ?? "admin";
        var token = init.AccessToken ?? TokenService.GenerateToken();
        await DatabaseBootstrap.InitializeAsync(services, user, token);
        Console.WriteLine(token);
    }

    private static IConfiguration BuildConfig(string[] args)
    {
        var values = new Dictionary<string, string?>();
        var dataDirectory = GetOption(args, "--data-directory");
        var key = GetOption(args, "--encryption-key-base64");
        var adminUser = GetOption(args, "--admin-user") ?? Environment.GetEnvironmentVariable("CHOBO_INIT_ADMIN_USER");
        var accessToken = GetOption(args, "--access-token") ?? Environment.GetEnvironmentVariable("CHOBO_INIT_ACCESS_TOKEN");
        if (dataDirectory is not null) values["Chobo:DataDirectory"] = dataDirectory;
        if (key is not null) values["Chobo:EncryptionKeyBase64"] = key;
        if (adminUser is not null) values["Chobo:Init:AdminUser"] = adminUser;
        if (accessToken is not null) values["Chobo:Init:AccessToken"] = accessToken;
        return new ConfigurationBuilder().AddInMemoryCollection(values).AddEnvironmentVariables().Build();
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        }
        return null;
    }
}
