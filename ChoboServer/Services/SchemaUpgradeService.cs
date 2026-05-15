using Chobo.Contracts;
using ChoboServer.Data;

namespace ChoboServer.Services;

public sealed class SchemaUpgradeService(ChoboDbContext db)
{
    public async Task UpgradeAsync(SchemaStateEntity schema, CancellationToken cancellationToken = default)
    {
        if (schema.SchemaVersion > ChoboApi.SchemaVersion)
        {
            throw new InvalidOperationException($"Database schema version {schema.SchemaVersion} is newer than server-supported schema version {ChoboApi.SchemaVersion}.");
        }

        if (schema.SchemaVersion < ChoboApi.SchemaVersion)
        {
            throw new InvalidOperationException($"No upgrade path is registered from schema version {schema.SchemaVersion} to {ChoboApi.SchemaVersion}.");
        }

        schema.ProductVersion = ChoboApi.ProductVersion;
        await db.SaveChangesAsync(cancellationToken);
    }
}
