using Chobo.Contracts;
using ChoboServer.Data;

namespace ChoboServer.Services;

public interface ISchemaUpgradeService
{
    Task UpgradeAsync(SchemaStateEntity schema, CancellationToken cancellationToken = default);
}

public sealed class SchemaUpgradeService(ChoboDbContext db) : ISchemaUpgradeService
{
    public async Task UpgradeAsync(SchemaStateEntity schema, CancellationToken cancellationToken = default)
    {
        if (schema.SchemaVersion > ChoboApi.SchemaVersion)
        {
            throw new InvalidOperationException($"Database schema version {schema.SchemaVersion} is newer than server-supported schema version {ChoboApi.SchemaVersion}.");
        }

        // Each arm steps one version so a database several releases behind is carried forward
        // through every intermediate step rather than needing a direct arm per source version.
        if (schema.SchemaVersion == 1 && ChoboApi.SchemaVersion >= 2)
        {
            schema.SchemaVersion = 2;
            schema.AppliedMigrationId = "000000000002_PasswordProtectedBackups";
            schema.AppliedAt = DateTimeOffset.UtcNow;
        }

        if (schema.SchemaVersion == 2 && ChoboApi.SchemaVersion >= 3)
        {
            // Index-only change. The EF migration drops the redundant indexes and creates the
            // consolidated one, so there is no data transformation to perform here.
            schema.SchemaVersion = 3;
            schema.AppliedMigrationId = "000000000003_IndexConsolidation";
            schema.AppliedAt = DateTimeOffset.UtcNow;
        }

        if (schema.SchemaVersion < ChoboApi.SchemaVersion)
        {
            throw new InvalidOperationException($"No upgrade path is registered from schema version {schema.SchemaVersion} to {ChoboApi.SchemaVersion}.");
        }

        schema.ProductVersion = ChoboApi.ProductVersion;
        await db.SaveChangesAsync(cancellationToken);
    }
}
