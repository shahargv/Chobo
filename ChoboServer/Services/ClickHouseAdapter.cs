using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text;
using System.Text.Json;
using ChoboServer.Data;
using ClickHouse.Driver.ADO;

namespace ChoboServer.Services;

public sealed record ClickHouseTableInfo(string Database, string Table, string Engine, string CreateTableSql, string ColumnsJson, string SchemaHash);

public sealed record ClickHouseOperationResult(string OperationId, string Status);

public sealed record ClickHouseOperationStatus(bool Exists, string? Status, string? Error);

public sealed record ClickHouseDiscoveredOperation(string OperationId, string Status, string? Error);

public sealed record ClickHouseNodeEndpoint(string Host, int Port, bool UseTls);

public sealed record ClickHouseShardReplicaInfo(int ShardNumber, string? ShardName, int ReplicaNumber, string Host, int Port, bool UseTls, int ErrorsCount)
{
    public ClickHouseNodeEndpoint Endpoint => new(Host, Port, UseTls);
}

public interface IClickHouseAdapter
{
    Task<IReadOnlyList<ClickHouseTableInfo>> GetTablesAsync(ClickHouseClusterEntity cluster, CancellationToken cancellationToken);
    Task<IReadOnlyList<ClickHouseTableInfo>> GetTablesAsync(ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> GetClusterNamesAsync(ClickHouseClusterEntity cluster, CancellationToken cancellationToken);
    Task<ClickHouseTableInfo?> GetTableAsync(ClickHouseClusterEntity cluster, string database, string table, CancellationToken cancellationToken);
    Task<ClickHouseTableInfo?> GetTableAsync(ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, string database, string table, CancellationToken cancellationToken);
    Task ExecuteAsync(ClickHouseClusterEntity cluster, string sql, CancellationToken cancellationToken);
    Task ExecuteAsync(ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, string sql, CancellationToken cancellationToken);
    Task<IReadOnlyList<ClickHouseShardReplicaInfo>> GetTopologyAsync(ClickHouseClusterEntity cluster, CancellationToken cancellationToken);
    Task<ClickHouseOperationResult> StartBackupAsync(ClickHouseClusterEntity cluster, BackupTargetEntity target, BackupTableEntity table, string? baseBackupPath, IReadOnlyDictionary<string, JsonElement> settings, CancellationToken cancellationToken);
    Task<ClickHouseOperationResult> StartBackupShardAsync(ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, BackupTargetEntity target, BackupTableEntity table, BackupTableShardEntity shard, string? baseBackupPath, IReadOnlyDictionary<string, JsonElement> settings, CancellationToken cancellationToken);
    Task<ClickHouseOperationResult> StartRestoreAsync(ClickHouseClusterEntity cluster, BackupTargetEntity target, RestoreTableEntity table, BackupTableEntity backupTable, IReadOnlyDictionary<string, JsonElement> settings, CancellationToken cancellationToken);
    Task<ClickHouseOperationResult> StartRestoreShardAsync(ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, BackupTargetEntity target, RestoreTableShardEntity shard, BackupTableEntity backupTable, BackupTableShardEntity backupShard, bool allowNonEmptyTables, IReadOnlyDictionary<string, JsonElement> settings, CancellationToken cancellationToken);
    Task<ClickHouseOperationStatus> GetOperationStatusAsync(ClickHouseClusterEntity cluster, string operationId, CancellationToken cancellationToken);
    Task<ClickHouseOperationStatus> GetOperationStatusAsync(ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, string operationId, CancellationToken cancellationToken);
    Task<ClickHouseDiscoveredOperation?> FindLatestBackupOperationForPathAsync(ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, BackupTargetEntity target, string storagePath, CancellationToken cancellationToken);
    Task KillBackupRestoreOperationAsync(ClickHouseClusterEntity cluster, string operationId, CancellationToken cancellationToken);
    Task KillBackupRestoreOperationAsync(ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, string operationId, CancellationToken cancellationToken);
}

public sealed class ClickHouseAdapter(ICredentialProtector protector, IEndpointRewriteService endpointRewrites, IBackupStorageProviderRegistry storageProviders, Serilog.ILogger logger) : IClickHouseAdapter
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan OperationSubmitTimeout = TimeSpan.FromMinutes(2);
    private readonly Serilog.ILogger _logger = logger.ForContext<ClickHouseAdapter>();

    public Task<IReadOnlyList<ClickHouseTableInfo>> GetTablesAsync(ClickHouseClusterEntity cluster, CancellationToken cancellationToken) =>
        GetTablesAsync(ToEndpoint(FirstAccessNode(cluster)), cluster, cancellationToken);

    public async Task<IReadOnlyList<ClickHouseTableInfo>> GetTablesAsync(ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, CancellationToken cancellationToken)
    {
        _logger.Information("Reading ClickHouse inventory for cluster {ClusterId} ({ClusterName}) on {Host}:{Port}.", cluster.Id, cluster.Name, endpoint.Host, endpoint.Port);
        var rows = await QueryAsync(endpoint, cluster, """
            SELECT database, name, engine, create_table_query
            FROM system.tables
            WHERE database NOT IN ('system', 'information_schema', 'INFORMATION_SCHEMA')
            ORDER BY database, name
            """, cancellationToken);

        var result = new List<ClickHouseTableInfo>();
        foreach (var row in rows)
        {
            var database = row[0];
            var table = row[1];
            var engine = row[2];
            var createSql = row[3];
            var columnsJson = await GetColumnsJsonAsync(endpoint, cluster, database, table, cancellationToken);
            var hash = Hash($"{engine}\n{ClickHouseSql.NormalizeCreateTableName(createSql)}\n{columnsJson}");
            result.Add(new ClickHouseTableInfo(database, table, engine, createSql, columnsJson, hash));
        }

        _logger.Information("Read {TableCount} ClickHouse tables for cluster {ClusterId} on {Host}:{Port}.", result.Count, cluster.Id, endpoint.Host, endpoint.Port);
        return result;
    }
    public async Task<IReadOnlyList<string>> GetClusterNamesAsync(ClickHouseClusterEntity cluster, CancellationToken cancellationToken)
    {
        if (cluster.Mode == Chobo.Contracts.ClusterMode.SingleInstance)
        {
            return [];
        }

        var rows = await QueryAsync(cluster, """
            SELECT DISTINCT cluster
            FROM system.clusters
            ORDER BY cluster
            """, cancellationToken);

        return rows.Select(row => row[0]).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
    }

    public async Task<ClickHouseTableInfo?> GetTableAsync(ClickHouseClusterEntity cluster, string database, string table, CancellationToken cancellationToken)
    {
        return await GetTableAsync(ToEndpoint(FirstAccessNode(cluster)), cluster, database, table, cancellationToken);
    }

    public async Task<ClickHouseTableInfo?> GetTableAsync(ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, string database, string table, CancellationToken cancellationToken)
    {
        _logger.Information("Reading ClickHouse table schema for {Database}.{Table} on cluster {ClusterId}.", database, table, cluster.Id);
        var rows = await QueryAsync(endpoint, cluster, $"""
            SELECT database, name, engine, create_table_query
            FROM system.tables
            WHERE database = {ClickHouseSql.Literal(database)} AND name = {ClickHouseSql.Literal(table)}
            LIMIT 1
            """, cancellationToken);
        if (rows.Count == 0)
        {
            return null;
        }

        var row = rows[0];
        var columnsJson = await GetColumnsJsonAsync(endpoint, cluster, database, table, cancellationToken);
        var hash = Hash($"{row[2]}\n{ClickHouseSql.NormalizeCreateTableName(row[3])}\n{columnsJson}");
        return new ClickHouseTableInfo(row[0], row[1], row[2], row[3], columnsJson, hash);
    }

    public async Task ExecuteAsync(ClickHouseClusterEntity cluster, string sql, CancellationToken cancellationToken)
    {
        _logger.Information("Executing ClickHouse command on cluster {ClusterId}: {SqlPreview}", cluster.Id, SqlLogRedactor.Preview(sql));
        await QueryAsync(cluster, sql, cancellationToken);
    }

    public async Task ExecuteAsync(ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, string sql, CancellationToken cancellationToken)
    {
        _logger.Information("Executing ClickHouse command on {Host}:{Port} for cluster {ClusterId}: {SqlPreview}", endpoint.Host, endpoint.Port, cluster.Id, SqlLogRedactor.Preview(sql));
        await QueryAsync(endpoint, cluster, sql, cancellationToken);
    }

    public async Task<IReadOnlyList<ClickHouseShardReplicaInfo>> GetTopologyAsync(ClickHouseClusterEntity cluster, CancellationToken cancellationToken)
    {
        if (cluster.Mode == Chobo.Contracts.ClusterMode.SingleInstance)
        {
            var node = FirstAccessNode(cluster);
            return [new ClickHouseShardReplicaInfo(1, "single", 1, node.Host, node.Port, node.UseTls, 0)];
        }

        var clusterName = cluster.ClickHouseClusterName;
        if (string.IsNullOrWhiteSpace(clusterName))
        {
            var names = await QueryAsync(cluster, """
                SELECT DISTINCT cluster
                FROM system.clusters
                ORDER BY cluster
                """, cancellationToken);
            if (names.Count != 1)
            {
                throw new InvalidOperationException("ClickHouse cluster name is required when system.clusters contains zero or multiple cluster definitions.");
            }

            clusterName = names[0][0];
        }

        var rows = await QueryAsync(cluster, $"""
            SELECT shard_num, replica_num, host_name, port, errors_count
            FROM system.clusters
            WHERE cluster = {ClickHouseSql.Literal(clusterName)}
            ORDER BY shard_num, replica_num, host_name, port
            """, cancellationToken);
        if (rows.Count == 0)
        {
            throw new InvalidOperationException($"ClickHouse cluster '{clusterName}' was not found in system.clusters.");
        }

        var useTls = FirstAccessNode(cluster).UseTls;
        return rows
            .Select(row =>
            {
                var shardNumber = int.Parse(row[0], System.Globalization.CultureInfo.InvariantCulture);
                var replicaNumber = int.Parse(row[1], System.Globalization.CultureInfo.InvariantCulture);
                var reportedEndpoint = new ClickHouseNodeEndpoint(row[2], int.Parse(row[3], System.Globalization.CultureInfo.InvariantCulture), useTls);
                var serverEndpoint = endpointRewrites.RewriteClickHouseEndpointForServer(reportedEndpoint);
                return new ClickHouseShardReplicaInfo(
                    shardNumber,
                    $"shard{shardNumber}",
                    replicaNumber,
                    serverEndpoint.Host,
                    serverEndpoint.Port,
                    serverEndpoint.UseTls,
                    row.Count > 4 && int.TryParse(row[4], out var errors) ? errors : 0);
            })
            .ToList();
    }

    public async Task<ClickHouseOperationResult> StartBackupAsync(ClickHouseClusterEntity cluster, BackupTargetEntity target, BackupTableEntity table, string? baseBackupPath, IReadOnlyDictionary<string, JsonElement> settings, CancellationToken cancellationToken)
    {
        var endpoint = ToEndpoint(FirstAccessNode(cluster));
        var shard = new BackupTableShardEntity { StoragePath = table.StoragePath, SourceShardNumber = 1, ReplicaNumber = 1, Host = endpoint.Host, Port = endpoint.Port, UseTls = endpoint.UseTls };
        return await StartBackupShardAsync(endpoint, cluster, target, table, shard, baseBackupPath, settings, cancellationToken);
    }

    public async Task<ClickHouseOperationResult> StartBackupShardAsync(ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, BackupTargetEntity target, BackupTableEntity table, BackupTableShardEntity shard, string? baseBackupPath, IReadOnlyDictionary<string, JsonElement> settings, CancellationToken cancellationToken)
    {
        var destination = await storageProviders.Get(target).CreateBackupDestinationAsync(target, shard.StoragePath, baseBackupPath, cancellationToken);
        var password = await protector.DecryptAsync(shard.EncryptedBackupPassword, shard.EncryptedBackupPasswordKeyId, cancellationToken);
        var managed = destination.Settings.ToList();
        if (password is not null)
        {
            managed.Add(("password", ClickHouseSql.Literal(password)));
            if (!string.IsNullOrWhiteSpace(baseBackupPath))
            {
                managed.Add(("use_same_password_for_base_backup", "1"));
            }
        }
        var settingsClause = ClickHouseAdvancedSettings.ToSettingsClause(settings, managed.ToArray());
        var sql = $"BACKUP TABLE {ClickHouseSql.Qualified(table.Database, table.Table)} TO {destination.Expression}{settingsClause} ASYNC";
        _logger.Information("Submitting ClickHouse backup for {Database}.{Table} shard {ShardNumber} on {Host}:{Port} to {StoragePath}.", table.Database, table.Table, shard.SourceShardNumber, endpoint.Host, endpoint.Port, shard.StoragePath);
        return await StartOperationAsync(endpoint, cluster, sql, destination.SensitiveValues.Concat(password is null ? [] : [password]).ToList(), cancellationToken);
    }

    public async Task<ClickHouseOperationResult> StartRestoreAsync(ClickHouseClusterEntity cluster, BackupTargetEntity target, RestoreTableEntity table, BackupTableEntity backupTable, IReadOnlyDictionary<string, JsonElement> settings, CancellationToken cancellationToken)
    {
        var endpoint = ToEndpoint(FirstAccessNode(cluster));
        var backupShard = new BackupTableShardEntity { StoragePath = backupTable.StoragePath, SourceShardNumber = 1 };
        var shard = new RestoreTableShardEntity { RestoreDatabase = table.TargetDatabase, RestoreTableName = table.TargetTable, SourceShardNumber = 1, TargetHost = endpoint.Host, TargetPort = endpoint.Port, TargetUseTls = endpoint.UseTls };
        return await StartRestoreShardAsync(endpoint, cluster, target, shard, backupTable, backupShard, table.Append, settings, cancellationToken);
    }

    public async Task<ClickHouseOperationResult> StartRestoreShardAsync(ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, BackupTargetEntity target, RestoreTableShardEntity shard, BackupTableEntity backupTable, BackupTableShardEntity backupShard, bool allowNonEmptyTables, IReadOnlyDictionary<string, JsonElement> settings, CancellationToken cancellationToken)
    {
        var destination = await storageProviders.Get(target).CreateRestoreDestinationAsync(target, backupShard.StoragePath, cancellationToken);
        var allowDifferentTableDefinition = cluster.Mode == Chobo.Contracts.ClusterMode.SingleInstance && ClickHouseSql.IsReplicatedMergeTreeEngine(backupTable.Engine);
        var password = await protector.DecryptAsync(backupShard.EncryptedBackupPassword, backupShard.EncryptedBackupPasswordKeyId, cancellationToken);
        var sql = ClickHouseRestoreSqlBuilder.Build(
            backupTable,
            shard,
            destination,
            allowNonEmptyTables,
            allowDifferentTableDefinition,
            settings,
            password,
            backupShard.EffectiveBackupType == Chobo.Contracts.BackupType.Incremental);
        _logger.Information("Submitting ClickHouse restore for {SourceDatabase}.{SourceTable} source shard {SourceShard} to {TargetDatabase}.{TargetTable} on {Host}:{Port}.", backupTable.Database, backupTable.Table, backupShard.SourceShardNumber, shard.RestoreDatabase, shard.RestoreTableName, endpoint.Host, endpoint.Port);
        return await StartOperationAsync(endpoint, cluster, sql, destination.SensitiveValues.Concat(password is null ? [] : [password]).ToList(), cancellationToken);
    }

    public async Task<ClickHouseOperationStatus> GetOperationStatusAsync(ClickHouseClusterEntity cluster, string operationId, CancellationToken cancellationToken)
    {
        _logger.Debug("Polling ClickHouse operation {OperationId} on cluster {ClusterId}.", operationId, cluster.Id);
        var endpoint = ToEndpoint(FirstAccessNode(cluster));
        return await GetOperationStatusAsync(endpoint, cluster, operationId, cancellationToken);
    }

    public async Task<ClickHouseOperationStatus> GetOperationStatusAsync(ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, string operationId, CancellationToken cancellationToken)
    {
        _logger.Debug("Polling ClickHouse operation {OperationId} on {Host}:{Port} for cluster {ClusterId}.", operationId, endpoint.Host, endpoint.Port, cluster.Id);
        var rows = await QueryAsync(endpoint, cluster, $"""
            SELECT status, error
            FROM system.backups
            WHERE id = {ClickHouseSql.Literal(operationId)}
            ORDER BY start_time DESC
            LIMIT 1
            """, cancellationToken);
        if (rows.Count == 0)
        {
            _logger.Warning("ClickHouse operation {OperationId} was not found on {Host}:{Port} for cluster {ClusterId}.", operationId, endpoint.Host, endpoint.Port, cluster.Id);
            return new ClickHouseOperationStatus(false, null, null);
        }

        _logger.Information("ClickHouse operation {OperationId} status is {Status}.", operationId, rows[0][0]);
        return new ClickHouseOperationStatus(true, rows[0][0], rows[0].Count > 1 ? rows[0][1] : null);
    }

    public async Task<ClickHouseDiscoveredOperation?> FindLatestBackupOperationForPathAsync(ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, BackupTargetEntity target, string storagePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(storagePath))
        {
            return null;
        }

        var destination = await storageProviders.Get(target).CreateBackupDestinationAsync(target, storagePath, null, cancellationToken);
        var exactNames = destination.OperationNames
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var fragments = destination.OperationNameFragments
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (exactNames.Count == 0 && fragments.Count == 0)
        {
            return null;
        }

        var predicates = exactNames
            .Select(name => $"name = {ClickHouseSql.Literal(name)}")
            .Concat(fragments.Select(fragment => $"position(name, {ClickHouseSql.Literal(fragment)}) > 0"))
            .ToList();
        var predicate = string.Join(" OR ", predicates);
        _logger.Information("Looking for ClickHouse backup operation on {Host}:{Port} for storage path {StoragePath}.", endpoint.Host, endpoint.Port, storagePath);
        var rows = await QueryAsync(endpoint, cluster, $"""
            SELECT id, status, error
            FROM system.backups
            WHERE {predicate}
            ORDER BY multiIf(status = 'CREATING_BACKUP', 0, status = 'BACKUP_CREATED', 1, positionCaseInsensitive(status, 'FAILED') > 0, 3, 2), start_time DESC
            LIMIT 1
            """, cancellationToken, destination.SensitiveValues);
        if (rows.Count == 0 || rows[0].Count < 2 || string.IsNullOrWhiteSpace(rows[0][0]))
        {
            _logger.Information("No ClickHouse backup operation was found on {Host}:{Port} for storage path {StoragePath}.", endpoint.Host, endpoint.Port, storagePath);
            return null;
        }

        var operation = new ClickHouseDiscoveredOperation(rows[0][0], rows[0][1], rows[0].Count > 2 ? rows[0][2] : null);
        _logger.Information("Recovered ClickHouse backup operation {OperationId} status {Status} for storage path {StoragePath}.", operation.OperationId, operation.Status, storagePath);
        return operation;
    }

    public async Task KillBackupRestoreOperationAsync(ClickHouseClusterEntity cluster, string operationId, CancellationToken cancellationToken)
    {
        var endpoint = ToEndpoint(FirstAccessNode(cluster));
        await KillBackupRestoreOperationAsync(endpoint, cluster, operationId, cancellationToken);
    }

    public async Task KillBackupRestoreOperationAsync(ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, string operationId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            return;
        }

        _logger.Information("Requesting ClickHouse backup/restore cancellation for operation {OperationId} on {Host}:{Port} for cluster {ClusterId}.", operationId, endpoint.Host, endpoint.Port, cluster.Id);
        await QueryAsync(endpoint, cluster, $"""
            KILL QUERY
            WHERE query_id = {ClickHouseSql.Literal(operationId)}
              AND (
                positionCaseInsensitive(query, 'BACKUP TABLE') > 0 OR
                positionCaseInsensitive(query, 'RESTORE TABLE') > 0
              )
            ASYNC
            """, cancellationToken);
    }

    private async Task<ClickHouseOperationResult> StartOperationAsync(ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, string sql, IReadOnlyList<string>? sensitiveValues, CancellationToken cancellationToken)
    {
        var rows = await SubmitAsyncOperationOverHttpAsync(endpoint, cluster, sql, sensitiveValues, cancellationToken);
        if (rows.Count == 0 || rows[0].Count < 2)
        {
            throw new InvalidOperationException("ClickHouse did not return an async operation id.");
        }

        var operation = new ClickHouseOperationResult(rows[0][0], rows[0][1]);
        _logger.Information("ClickHouse async operation submitted: {OperationId} status {Status}.", operation.OperationId, operation.Status);
        return operation;
    }

    private async Task<List<List<string>>> SubmitAsyncOperationOverHttpAsync(ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, string sql, IReadOnlyList<string>? sensitiveValues, CancellationToken cancellationToken)
    {
        var effectiveEndpoint = endpointRewrites.RewriteClickHouseEndpointForServer(endpoint);
        var settings = await CreateSettingsAsync(effectiveEndpoint, cluster, cancellationToken);
        var uri = new UriBuilder
        {
            Scheme = settings.Protocol,
            Host = settings.Host,
            Port = settings.Port,
            Query = "wait_end_of_query=0&default_format=TabSeparated"
        }.Uri;

        using var client = new HttpClient { Timeout = OperationSubmitTimeout };
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(sql, Encoding.UTF8, "text/plain") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.Username}:{settings.Password}")));

        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var firstLine = await reader.ReadLineAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var rest = await reader.ReadToEndAsync(cancellationToken);
                var body = string.Join('\n', new[] { firstLine, rest }.Where(x => !string.IsNullOrWhiteSpace(x)));
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(body) ? $"ClickHouse returned HTTP {(int)response.StatusCode} {response.ReasonPhrase}." : body.Trim());
            }
            if (string.IsNullOrWhiteSpace(firstLine))
            {
                throw new InvalidOperationException("ClickHouse did not return an async operation id.");
            }

            return [firstLine.Split('\t').ToList()];
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "ClickHouse async operation submit failed on {Host}:{Port}: {Message}. SQL: {SqlPreview}", effectiveEndpoint.Host, settings.Port, ex.Message, SqlLogRedactor.Preview(sql, sensitiveValues));
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    private async Task<string> GetColumnsJsonAsync(ClickHouseClusterEntity cluster, string database, string table, CancellationToken cancellationToken)
    {
        return await GetColumnsJsonAsync(ToEndpoint(FirstAccessNode(cluster)), cluster, database, table, cancellationToken);
    }

    private async Task<string> GetColumnsJsonAsync(ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, string database, string table, CancellationToken cancellationToken)
    {
        var rows = await QueryAsync(endpoint, cluster, $"""
            SELECT name, type, default_kind, default_expression
            FROM system.columns
            WHERE database = {ClickHouseSql.Literal(database)} AND table = {ClickHouseSql.Literal(table)}
            ORDER BY position
            """, cancellationToken);
        return JsonSerializer.Serialize(rows.Select(x => new
        {
            name = x.ElementAtOrDefault(0) ?? "",
            type = x.ElementAtOrDefault(1) ?? "",
            defaultKind = x.ElementAtOrDefault(2) ?? "",
            defaultExpression = x.ElementAtOrDefault(3) ?? ""
        }), JsonOptions);
    }

    private async Task<List<List<string>>> QueryAsync(ClickHouseClusterEntity cluster, string sql, CancellationToken cancellationToken)
    {
        return await QueryAsync(ToEndpoint(FirstAccessNode(cluster)), cluster, sql, cancellationToken);
    }

    private async Task<List<List<string>>> QueryAsync(ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, string sql, CancellationToken cancellationToken, IReadOnlyList<string>? sensitiveValues = null)
    {
        var effectiveEndpoint = endpointRewrites.RewriteClickHouseEndpointForServer(endpoint);
        var settings = await CreateSettingsAsync(effectiveEndpoint, cluster, cancellationToken);
        await using var connection = new ClickHouseConnection(settings);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand(sql);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var result = new List<List<string>>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var row = new List<string>(reader.FieldCount);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row.Add(await reader.IsDBNullAsync(i, cancellationToken)
                        ? ""
                        : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture) ?? "");
                }

                result.Add(row);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "ClickHouse query failed on {Host}:{Port}: {Message}. SQL: {SqlPreview}", effectiveEndpoint.Host, settings.Port, ex.Message, SqlLogRedactor.Preview(sql, sensitiveValues));
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    private async Task<ClickHouseClientSettings> CreateSettingsAsync(ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, CancellationToken cancellationToken)
    {
        var user = await protector.DecryptAsync(cluster.EncryptedUserName, cluster.EncryptedUserNameKeyId, cancellationToken);
        var password = await protector.DecryptAsync(cluster.EncryptedPassword, cluster.EncryptedPasswordKeyId, cancellationToken);
        var settings = new ClickHouseClientSettings
        {
            Host = endpoint.Host,
            Port = ToDriverPort(endpoint),
            Protocol = endpoint.UseTls ? "https" : "http",
            Timeout = RequestTimeout,
            Username = string.IsNullOrWhiteSpace(user) ? "default" : user,
            Password = password ?? ""
        };

        return settings;
    }

    private static ushort ToDriverPort(ClickHouseNodeEndpoint endpoint) =>
        (ushort)((endpoint.Port, endpoint.UseTls) switch
        {
            (9000, false) => 8123,
            (9440, true) => 8443,
            _ => endpoint.Port
        });

    private static string Preview(string sql, IReadOnlyList<string>? sensitiveValues = null)
    {
        var redacted = sql;
        foreach (var value in sensitiveValues ?? [])
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            redacted = redacted.Replace(ClickHouseSql.Literal(value), "'***REDACTED***'", StringComparison.Ordinal);
            redacted = redacted.Replace(value, "***REDACTED***", StringComparison.Ordinal);
        }

        var compact = string.Join(' ', redacted.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 400 ? compact : compact[..400] + "...";
    }


    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static ClickHouseAccessNodeEntity FirstAccessNode(ClickHouseClusterEntity cluster)
    {
        if (cluster.AccessNodes.Count == 0)
        {
            throw new InvalidOperationException("Cluster has no access nodes.");
        }

        return cluster.AccessNodes[0];
    }

    private static ClickHouseNodeEndpoint ToEndpoint(ClickHouseAccessNodeEntity node) =>
        new(node.Host, node.Port, node.UseTls);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
