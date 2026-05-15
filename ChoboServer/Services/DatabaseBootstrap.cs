using Chobo.Contracts;
using ChoboServer.Data;
using ChoboServer.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChoboServer.Services;

public interface IDatabaseBootstrap
{
    Task EnsureDatabaseObjectsAsync(CancellationToken cancellationToken = default);
    Task EnsureSchemaStateAsync(CancellationToken cancellationToken = default);
    Task TryInitializeFromOptionsAsync(CancellationToken cancellationToken = default);
    Task BootstrapFirstStartupAsync(CancellationToken cancellationToken = default);
    Task<bool> InitializeAsync(string adminUser, string accessToken, CancellationToken cancellationToken = default);
}

public sealed class DatabaseBootstrap(
    ChoboDbContext db,
    ISchemaUpgradeService schemaUpgrade,
    ITokenService tokenService,
    IOptions<ChoboInitOptions> initOptions,
    IOptions<ChoboStorageOptions> storageOptions,
    Serilog.ILogger logger) : IDatabaseBootstrap
{
    private readonly Serilog.ILogger _logger = logger.ForContext<DatabaseBootstrap>();

    public async Task EnsureDatabaseObjectsAsync(CancellationToken cancellationToken = default)
    {
        await db.Database.MigrateAsync(cancellationToken);
    }

    public async Task EnsureSchemaStateAsync(CancellationToken cancellationToken = default)
    {
        if (!await db.SchemaStates.AnyAsync(cancellationToken))
        {
            db.SchemaStates.Add(new SchemaStateEntity
            {
                SchemaVersion = ChoboApi.SchemaVersion,
                AppliedMigrationId = "000000000001_Baseline",
                AppliedAt = DateTimeOffset.UtcNow,
                ProductVersion = ChoboApi.ProductVersion
            });
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var schema = await db.SchemaStates.SingleAsync(cancellationToken);
        await schemaUpgrade.UpgradeAsync(schema, cancellationToken);
    }

    public async Task TryInitializeFromOptionsAsync(CancellationToken cancellationToken = default)
    {
        var options = initOptions.Value;
        var user = options.AdminUser;
        if (string.IsNullOrWhiteSpace(user))
        {
            _logger.Information("Skipping initial admin creation because Chobo:Init:AdminUser is not configured.");
            return;
        }

        var token = options.AccessToken ?? TokenService.GenerateToken();
        _logger.Information("Initial admin configuration detected for user {AdminUser}; access token configured: {HasAccessToken}.", user, options.AccessToken is not null);

        await InitializeAsync(user, token, cancellationToken);
    }

    public async Task BootstrapFirstStartupAsync(CancellationToken cancellationToken = default)
    {
        var options = initOptions.Value;
        var adminUser = string.IsNullOrWhiteSpace(options.AdminUser) ? "admin" : options.AdminUser;
        var token = options.AccessToken ?? TokenService.GenerateToken();

        if (await InitializeAsync(adminUser, token, cancellationToken))
        {
            Console.WriteLine(token);
            var dataDirectory = ChoboPaths.GetDataDirectory(storageOptions.Value.DataDirectory);
            Directory.CreateDirectory(dataDirectory);
            await File.WriteAllTextAsync(Path.Combine(dataDirectory, "_initialized"), DateTimeOffset.UtcNow.ToString("O"), cancellationToken);
        }
    }

    public async Task<bool> InitializeAsync(string adminUser, string accessToken, CancellationToken cancellationToken = default)
    {
        await EnsureDatabaseObjectsAsync(cancellationToken);
        await EnsureSchemaStateAsync(cancellationToken);
        if (await db.Users.AnyAsync(cancellationToken))
        {
            _logger.Information("Skipping initial admin creation because users already exist.");
            return false;
        }

        var user = new UserEntity { UserName = adminUser };
        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken);

        db.AccessTokens.Add(tokenService.CreateToken(user.Id, "initial", accessToken));
        db.AuditEntries.Add(new AuditEntryEntity
        {
            ActorName = "system",
            Action = "initialize",
            EntityType = AuditEntityTypes.ToStorageValue(AuditEntityType.Server),
            EntityId = user.Id.ToString(),
            Details = "{}"
        });
        await db.SaveChangesAsync(cancellationToken);
        _logger.Information("Initialized Chobo admin user {AdminUser} ({UserId}) and initial access token.", adminUser, user.Id);
        return true;
    }
}
