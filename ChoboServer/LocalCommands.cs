using ChoboServer.Options;
using ChoboServer.Services;
using Microsoft.Extensions.Options;

namespace ChoboServer;

public static class LocalCommands
{
    public static async Task InitializeAsync(string[] args)
    {
        var config = ChoboConfiguration.BuildLocalCommandConfiguration(args);
        var services = new ServiceCollection().AddChoboServer(config).BuildServiceProvider();
        var init = services.GetRequiredService<IOptions<ChoboInitOptions>>().Value;
        var user = init.AdminUser ?? "admin";
        var token = init.AccessToken ?? TokenService.GenerateToken();
        await DatabaseBootstrap.InitializeAsync(services, user, token);
        Console.WriteLine(token);
    }
}
