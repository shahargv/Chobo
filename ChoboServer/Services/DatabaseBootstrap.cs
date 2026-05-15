using ChoboServer.Data;
using ChoboServer.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChoboServer.Services;

public static class DatabaseBootstrap
{
    public static async Task EnsureDatabaseObjectsAsync(ChoboDbContext db)
    {
        await db.Database.MigrateAsync();
    }

    public static async Task EnsureSchemaStateAsync(ChoboDbContext db)
    {
        if (!await db.SchemaStates.AnyAsync())
        {
            db.SchemaStates.Add(new SchemaStateEntity
            {
                SchemaVersion = Chobo.Contracts.ChoboApi.SchemaVersion,
                AppliedMigrationId = "20260509160000_InitialCreate",
                AppliedAt = DateTimeOffset.UtcNow,
                ProductVersion = Chobo.Contracts.ChoboApi.ProductVersion
            });
            await db.SaveChangesAsync();
            return;
        }

        var schema = await db.SchemaStates.SingleAsync();
        await SchemaUpgradeService.UpgradeAsync(db, schema);
    }

    public static async Task TryInitializeFromOptionsAsync(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<ChoboInitOptions>>().Value;
        var logger = services.GetService<Serilog.ILogger>()?.ForContext(typeof(DatabaseBootstrap));
        var user = options.AdminUser;
        if (string.IsNullOrWhiteSpace(user))
        {
            logger?.Information("Skipping initial admin creation because Chobo:Init:AdminUser is not configured.");
            return;
        }

        var token = options.AccessToken ?? TokenService.GenerateToken();
        logger?.Information("Initial admin configuration detected for user {AdminUser}; access token configured: {HasAccessToken}.", user, options.AccessToken is not null);

        await InitializeAsync(services, user, token);
    }

    public static async Task BootstrapFirstStartupAsync(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<ChoboInitOptions>>().Value;
        var storage = services.GetRequiredService<IOptions<ChoboStorageOptions>>().Value;
        var adminUser = string.IsNullOrWhiteSpace(options.AdminUser) ? "admin" : options.AdminUser;
        var token = options.AccessToken ?? TokenService.GenerateToken();

        if (await InitializeAsync(services, adminUser, token))
        {
            Console.WriteLine(token);
            var dataDirectory = ChoboPaths.GetDataDirectory(storage.DataDirectory);
            Directory.CreateDirectory(dataDirectory);
            await File.WriteAllTextAsync(Path.Combine(dataDirectory, "_initialized"), DateTimeOffset.UtcNow.ToString("O"));
        }
    }

    public static async Task<bool> InitializeAsync(IServiceProvider services, string adminUser, string accessToken)
    {
        var db = services.GetRequiredService<ChoboDbContext>();
        var logger = services.GetService<Serilog.ILogger>()?.ForContext(typeof(DatabaseBootstrap));
        await EnsureDatabaseObjectsAsync(db);
        await EnsureSchemaStateAsync(db);
        if (await db.Users.AnyAsync())
        {
            logger?.Information("Skipping initial admin creation because users already exist.");
            return false;
        }

        var user = new UserEntity { UserName = adminUser };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var tokenService = services.GetRequiredService<TokenService>();
        db.AccessTokens.Add(tokenService.CreateToken(user.Id, "initial", accessToken));
        db.AuditEntries.Add(new AuditEntryEntity
        {
            ActorName = "system",
            Action = "initialize",
            EntityType = AuditEntityTypes.ToStorageValue(AuditEntityType.Server),
            EntityId = user.Id.ToString(),
            Details = "{}"
        });
        await db.SaveChangesAsync();
        logger?.Information("Initialized Chobo admin user {AdminUser} ({UserId}) and initial access token.", adminUser, user.Id);
        return true;
    }
}
