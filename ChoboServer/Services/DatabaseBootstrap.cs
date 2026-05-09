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
                ProductVersion = Chobo.Contracts.ChoboApi.ServerVersion
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
        var user = options.AdminUser;
        if (string.IsNullOrWhiteSpace(user))
        {
            return;
        }

        var token = options.AccessToken ?? TokenService.GenerateToken();

        await InitializeAsync(services, user, token);
    }

    public static async Task InitializeAsync(IServiceProvider services, string adminUser, string accessToken)
    {
        var db = services.GetRequiredService<ChoboDbContext>();
        await EnsureDatabaseObjectsAsync(db);
        await EnsureSchemaStateAsync(db);
        if (await db.Users.AnyAsync())
        {
            return;
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
            EntityType = "server",
            EntityId = user.Id.ToString(),
            Details = "{}"
        });
        await db.SaveChangesAsync();
    }
}
