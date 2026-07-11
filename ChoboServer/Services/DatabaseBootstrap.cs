using Chobo.Contracts;
using ChoboServer.Data;
using ChoboServer.Options;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace ChoboServer.Services;

public interface IDatabaseBootstrap
{
    Task EnsureDatabaseObjectsAsync(CancellationToken cancellationToken = default);
    Task EnsureSchemaStateAsync(CancellationToken cancellationToken = default);
    Task TryInitializeFromOptionsAsync(CancellationToken cancellationToken = default);
    Task<InstallStatusDto> GetInstallStatusAsync(CancellationToken cancellationToken = default);
    Task<InstallResponse> InstallAsync(string? adminUser, CancellationToken cancellationToken = default);
    Task<InstallResponse?> InitializeAsync(string adminUser, string accessToken, CancellationToken cancellationToken = default);
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
        var startupTimer = Stopwatch.StartNew();
        _logger.Information("Starting database schema migration checks for supported schema version {SchemaVersion}.", ChoboApi.SchemaVersion);

        var compatibilityTimer = Stopwatch.StartNew();
        _logger.Information("Schema migration step: verifying database compatibility.");
        await VerifySchemaCompatibilityBeforeMigrationAsync(cancellationToken);
        _logger.Information("Schema migration step completed: database compatibility verified in {ElapsedMilliseconds} ms.", compatibilityTimer.ElapsedMilliseconds);

        var appliedMigrations = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var migrations = db.Database.GetMigrations().ToList();
        var pendingMigrations = migrations.Where(migration => !appliedMigrations.Contains(migration)).ToList();
        _logger.Information(
            "Schema migration inventory: {AppliedCount} applied, {PendingCount} pending, {KnownCount} known migrations.",
            appliedMigrations.Count,
            pendingMigrations.Count,
            migrations.Count);

        foreach (var migration in pendingMigrations)
        {
            _logger.Information("Schema migration step pending: {MigrationId}.", migration);
        }

        var migrationTimer = Stopwatch.StartNew();
        try
        {
            _logger.Information("Schema migration step: applying {PendingCount} pending migration(s).", pendingMigrations.Count);
            await db.Database.MigrateAsync(cancellationToken);
            foreach (var migration in pendingMigrations)
            {
                _logger.Information("Schema migration step completed: applied migration {MigrationId}.", migration);
            }
            _logger.Information("Schema migration apply completed in {ElapsedMilliseconds} ms.", migrationTimer.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.Error(
                ex,
                "Schema migration step failed after {ElapsedMilliseconds} ms. Pending migrations: {PendingMigrations}.",
                migrationTimer.ElapsedMilliseconds,
                pendingMigrations);
            throw;
        }

        _logger.Information(
            "Database schema migration checks completed in {ElapsedMilliseconds} ms; applied {AppliedMigrationCount} migration(s).",
            startupTimer.ElapsedMilliseconds,
            pendingMigrations.Count);
    }

    private async Task VerifySchemaCompatibilityBeforeMigrationAsync(CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State == System.Data.ConnectionState.Closed;
        if (shouldClose)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var tableCommand = connection.CreateCommand();
            tableCommand.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'SchemaStates' LIMIT 1;";
            if (await tableCommand.ExecuteScalarAsync(cancellationToken) is null)
            {
                _logger.Information("Schema compatibility check: SchemaStates table does not exist yet; treating database as new.");
                return;
            }

            await using var versionCommand = connection.CreateCommand();
            versionCommand.CommandText = "SELECT SchemaVersion FROM SchemaStates LIMIT 1;";
            var value = await versionCommand.ExecuteScalarAsync(cancellationToken);
            if (value is null || value == DBNull.Value)
            {
                _logger.Warning("Schema compatibility check: SchemaStates exists but contains no schema version.");
                return;
            }

            var schemaVersion = Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
            _logger.Information("Schema compatibility check: database version {DatabaseSchemaVersion}, server-supported version {SupportedSchemaVersion}.", schemaVersion, ChoboApi.SchemaVersion);
            if (schemaVersion > ChoboApi.SchemaVersion)
            {
                throw new InvalidOperationException($"Database schema version {schemaVersion} is newer than server-supported schema version {ChoboApi.SchemaVersion}.");
            }
        }
        catch (SqliteException ex)
        {
            throw new InvalidOperationException("Failed to read Chobo schema state before applying EF migrations.", ex);
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync();
            }
        }
    }

    public async Task EnsureSchemaStateAsync(CancellationToken cancellationToken = default)
    {
        if (!await db.SchemaStates.AnyAsync(cancellationToken))
        {
            db.SchemaStates.Add(new SchemaStateEntity
            {
                SchemaVersion = ChoboApi.SchemaVersion,
                AppliedMigrationId = "000000000002_PasswordProtectedBackups",
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

        var result = await InitializeAsync(user, token, cancellationToken);
        if (result is not null && options.AccessToken is not null)
        {
            Console.WriteLine("Chobo initialized from configured CHOBO_INIT_ADMIN_USER and CHOBO_INIT_ACCESS_TOKEN.");
        }
    }

    public async Task<InstallStatusDto> GetInstallStatusAsync(CancellationToken cancellationToken = default)
    {
        var requiresInstallation = !await db.Users.AnyAsync(cancellationToken);
        var message = requiresInstallation
            ? "Chobo is waiting for first-time installation. Use the web UI or run: ChoboCli install --server-url <url>"
            : "Chobo installation is complete. Sign in with an existing access token.";
        return new InstallStatusDto(requiresInstallation, message);
    }

    public async Task<InstallResponse> InstallAsync(string? adminUser, CancellationToken cancellationToken = default)
    {
        var userName = string.IsNullOrWhiteSpace(adminUser) ? "admin" : adminUser.Trim();
        var token = TokenService.GenerateToken();
        var result = await InitializeAsync(userName, token, cancellationToken);
        if (result is null)
        {
            throw new InvalidOperationException("Chobo installation has already been finalized. Create additional tokens from an authenticated session.");
        }

        return result;
    }

    public async Task<InstallResponse?> InitializeAsync(string adminUser, string accessToken, CancellationToken cancellationToken = default)
    {
        await EnsureDatabaseObjectsAsync(cancellationToken);
        await EnsureSchemaStateAsync(cancellationToken);
        if (await db.Users.AnyAsync(cancellationToken))
        {
            _logger.Information("Skipping initial admin creation because users already exist.");
            return null;
        }

        var user = new UserEntity { UserName = adminUser.Trim() };
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
        await WriteInitializedMarkerAsync(cancellationToken);
        _logger.Information("Initialized Chobo admin user {AdminUser} ({UserId}) and initial access token.", user.UserName, user.Id);
        return new InstallResponse(user.Id, user.UserName, accessToken);
    }

    private async Task WriteInitializedMarkerAsync(CancellationToken cancellationToken)
    {
        var dataDirectory = ChoboPaths.GetDataDirectory(storageOptions.Value.DataDirectory);
        Directory.CreateDirectory(dataDirectory);
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, "_initialized"), DateTimeOffset.UtcNow.ToString("O"), cancellationToken);
    }
}
