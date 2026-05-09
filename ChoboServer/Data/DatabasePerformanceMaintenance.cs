using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ChoboServer.Data;

public static class DatabasePerformanceMaintenance
{
    public static async Task EnsureAsync(ChoboDbContext db)
    {
        await EnsureAccessTokenLookupColumnAsync(db);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS IX_ApplicationLogEntries_Timestamp ON ApplicationLogEntries (Timestamp);
            CREATE INDEX IF NOT EXISTS IX_ApplicationLogEntries_Level_Timestamp ON ApplicationLogEntries (Level, Timestamp);
            CREATE INDEX IF NOT EXISTS IX_AuditEntries_Timestamp ON AuditEntries (Timestamp);
            CREATE INDEX IF NOT EXISTS IX_AuditEntries_ActorUserId_Timestamp ON AuditEntries (ActorUserId, Timestamp);
            CREATE INDEX IF NOT EXISTS IX_AuditEntries_EntityType_Timestamp ON AuditEntries (EntityType, Timestamp);
            CREATE INDEX IF NOT EXISTS IX_AccessTokens_TokenLookupHash ON AccessTokens (TokenLookupHash);
            CREATE INDEX IF NOT EXISTS IX_AccessTokens_IsActive_TokenLookupHash ON AccessTokens (IsActive, TokenLookupHash);
            CREATE INDEX IF NOT EXISTS IX_AccessTokens_UserId_IsActive ON AccessTokens (UserId, IsActive);
            CREATE INDEX IF NOT EXISTS IX_ClickHouseClusters_IsDeleted_Name ON ClickHouseClusters (IsDeleted, Name);
            CREATE INDEX IF NOT EXISTS IX_ClickHouseAccessNodes_ClusterId ON ClickHouseAccessNodes (ClusterId);
            CREATE INDEX IF NOT EXISTS IX_BackupTargets_IsDeleted_Name ON BackupTargets (IsDeleted, Name);
            CREATE INDEX IF NOT EXISTS IX_BackupPolicies_IsDeleted_Name ON BackupPolicies (IsDeleted, Name);
            CREATE INDEX IF NOT EXISTS IX_BackupSchedules_IsDeleted_Name ON BackupSchedules (IsDeleted, Name);
            CREATE INDEX IF NOT EXISTS IX_BackupSchedules_PolicyId_IsDeleted ON BackupSchedules (PolicyId, IsDeleted);
            """);
    }

    private static async Task EnsureAccessTokenLookupColumnAsync(ChoboDbContext db)
    {
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        if (shouldClose)
        {
            await connection.OpenAsync();
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA table_info(AccessTokens);";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (string.Equals(reader.GetString(1), "TokenLookupHash", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }
        finally
        {
            if (shouldClose)
            {
                connection.Close();
            }
        }

        await db.Database.ExecuteSqlRawAsync("ALTER TABLE AccessTokens ADD COLUMN TokenLookupHash TEXT NOT NULL DEFAULT '';");
    }
}

