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

public sealed record ClickHouseNodeEndpoint(string Host, int Port, bool UseTls);

public sealed record ClickHouseShardReplicaInfo(int ShardNumber, string? ShardName, int ReplicaNumber, string Host, int Port, bool UseTls, int ErrorsCount)
{
    public ClickHouseNodeEndpoint Endpoint => new(Host, Port, UseTls);
}

public interface IClickHouseAdapter
{
    Task<IReadOnlyList<ClickHouseTableInfo>> GetTablesAsync(ClickHouseClusterEntity cluster, CancellationToken cancellationToken);
    Task<ClickHouseTableInfo?> GetTableAsync(ClickHouseClusterEntity cluster, string database, string table, CancellationToken cancellationToken);
    Task<ClickHouseTableInfo?> GetTableAsync(ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, string database, string table, CancellationToken cancellationToken);
    Task ExecuteAsync(ClickHouseClusterEntity cluster, string sql, CancellationToken cancellationToken);
    Task ExecuteAsync(ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, string sql, CancellationToken cancellationToken);
    Task<IReadOnlyList<ClickHouseShardReplicaInfo>> GetTopologyAsync(ClickHouseClusterEntity cluster, CancellationToken cancellationToken);
    Task<ClickHouseOperationResult> StartBackupAsync(ClickHouseClusterEntity cluster, BackupTargetEntity target, BackupTableEntity table, CancellationToken cancellationToken);
    Task<ClickHouseOperationResult> StartBackupShardAsync(ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, BackupTargetEntity target, BackupTableEntity table, BackupTableShardEntity shard, CancellationToken cancellationToken);
    Task<ClickHouseOperationResult> StartRestoreAsync(ClickHouseClusterEntity cluster, BackupTargetEntity target, RestoreTableEntity table, BackupTableEntity backupTable, CancellationToken cancellationToken);
    Task<ClickHouseOperationResult> StartRestoreShardAsync(ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, BackupTargetEntity target, RestoreTableShardEntity shard, BackupTableEntity backupTable, BackupTableShardEntity backupShard, CancellationToken cancellationToken);
    Task<ClickHouseOperationStatus> GetOperationStatusAsync(ClickHouseClusterEntity cluster, string operationId, CancellationToken cancellationToken);
    Task<ClickHouseOperationStatus> GetOperationStatusAsync(ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, string operationId, CancellationToken cancellationToken);
}

public sealed class ClickHouseAdapter(ICredentialProtector protector, Serilog.ILogger logger) : IClickHouseAdapter
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private readonly Serilog.ILogger _logger = logger.ForContext<ClickHouseAdapter>();

    public async Task<IReadOnlyList<ClickHouseTableInfo>> GetTablesAsync(ClickHouseClusterEntity cluster, CancellationToken cancellationToken)
    {
        _logger.Information("Reading ClickHouse inventory for cluster {ClusterId} ({ClusterName}).", cluster.Id, cluster.Name);
        var rows = await QueryAsync(cluster, """
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
            var columnsJson = await GetColumnsJsonAsync(cluster, database, table, cancellationToken);
            var hash = Hash($"{engine}\n{ClickHouseSql.NormalizeCreateTableName(createSql)}\n{columnsJson}");
            result.Add(new ClickHouseTableInfo(database, table, engine, createSql, columnsJson, hash));
        }

        _logger.Information("Read {TableCount} ClickHouse tables for cluster {ClusterId}.", result.Count, cluster.Id);
        return result;
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
        _logger.Information("Executing ClickHouse command on cluster {ClusterId}: {SqlPreview}", cluster.Id, Preview(sql));
        await QueryAsync(cluster, sql, cancellationToken);
    }

    public async Task ExecuteAsync(ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, string sql, CancellationToken cancellationToken)
    {
        _logger.Information("Executing ClickHouse command on {Host}:{Port} for cluster {ClusterId}: {SqlPreview}", endpoint.Host, endpoint.Port, cluster.Id, Preview(sql));
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
            .Select(row => new ClickHouseShardReplicaInfo(
                int.Parse(row[0], System.Globalization.CultureInfo.InvariantCulture),
                $"shard{int.Parse(row[0], System.Globalization.CultureInfo.InvariantCulture)}",
                int.Parse(row[1], System.Globalization.CultureInfo.InvariantCulture),
                row[2],
                int.Parse(row[3], System.Globalization.CultureInfo.InvariantCulture),
                useTls,
                row.Count > 4 && int.TryParse(row[4], out var errors) ? errors : 0))
            .ToList();
    }

    public async Task<ClickHouseOperationResult> StartBackupAsync(ClickHouseClusterEntity cluster, BackupTargetEntity target, BackupTableEntity table, CancellationToken cancellationToken)
    {
        var endpoint = ToEndpoint(FirstAccessNode(cluster));
        var shard = new BackupTableShardEntity { S3Path = table.S3Path, SourceShardNumber = 1, ReplicaNumber = 1, Host = endpoint.Host, Port = endpoint.Port, UseTls = endpoint.UseTls };
        return await StartBackupShardAsync(endpoint, cluster, target, table, shard, cancellationToken);
    }

    public async Task<ClickHouseOperationResult> StartBackupShardAsync(ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, BackupTargetEntity target, BackupTableEntity table, BackupTableShardEntity shard, CancellationToken cancellationToken)
    {
        var s3 = S3Endpoint(target, shard.S3Path);
        var accessKey = await protector.DecryptAsync(target.EncryptedAccessKey, target.EncryptedAccessKeyKeyId, cancellationToken) ?? "";
        var secretKey = await protector.DecryptAsync(target.EncryptedSecretKey, target.EncryptedSecretKeyKeyId, cancellationToken) ?? "";
        var sql = $"BACKUP TABLE {ClickHouseSql.Qualified(table.Database, table.Table)} TO {ClickHouseSql.S3(s3, accessKey, secretKey)} ASYNC";
        _logger.Information("Submitting ClickHouse backup for {Database}.{Table} shard {ShardNumber} on {Host}:{Port} to {S3Path}.", table.Database, table.Table, shard.SourceShardNumber, endpoint.Host, endpoint.Port, shard.S3Path);
        return await StartOperationAsync(endpoint, cluster, sql, cancellationToken);
    }

    public async Task<ClickHouseOperationResult> StartRestoreAsync(ClickHouseClusterEntity cluster, BackupTargetEntity target, RestoreTableEntity table, BackupTableEntity backupTable, CancellationToken cancellationToken)
    {
        var endpoint = ToEndpoint(FirstAccessNode(cluster));
        var backupShard = new BackupTableShardEntity { S3Path = backupTable.S3Path, SourceShardNumber = 1 };
        var shard = new RestoreTableShardEntity { RestoreDatabase = table.TargetDatabase, RestoreTableName = table.TargetTable, SourceShardNumber = 1, TargetHost = endpoint.Host, TargetPort = endpoint.Port, TargetUseTls = endpoint.UseTls };
        return await StartRestoreShardAsync(endpoint, cluster, target, shard, backupTable, backupShard, cancellationToken);
    }

    public async Task<ClickHouseOperationResult> StartRestoreShardAsync(ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, BackupTargetEntity target, RestoreTableShardEntity shard, BackupTableEntity backupTable, BackupTableShardEntity backupShard, CancellationToken cancellationToken)
    {
        var s3 = S3Endpoint(target, backupShard.S3Path);
        var from = ClickHouseSql.Qualified(backupTable.Database, backupTable.Table);
        var to = ClickHouseSql.Qualified(shard.RestoreDatabase, shard.RestoreTableName);
        var accessKey = await protector.DecryptAsync(target.EncryptedAccessKey, target.EncryptedAccessKeyKeyId, cancellationToken) ?? "";
        var secretKey = await protector.DecryptAsync(target.EncryptedSecretKey, target.EncryptedSecretKeyKeyId, cancellationToken) ?? "";
        var sql = $"RESTORE TABLE {from} AS {to} FROM {ClickHouseSql.S3(s3, accessKey, secretKey)} ASYNC";
        _logger.Information("Submitting ClickHouse restore for {SourceDatabase}.{SourceTable} source shard {SourceShard} to {TargetDatabase}.{TargetTable} on {Host}:{Port}.", backupTable.Database, backupTable.Table, backupShard.SourceShardNumber, shard.RestoreDatabase, shard.RestoreTableName, endpoint.Host, endpoint.Port);
        return await StartOperationAsync(endpoint, cluster, sql, cancellationToken);
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

    private async Task<ClickHouseOperationResult> StartOperationAsync(ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, string sql, CancellationToken cancellationToken)
    {
        var rows = await QueryAsync(endpoint, cluster, sql, cancellationToken);
        if (rows.Count == 0 || rows[0].Count < 2)
        {
            throw new InvalidOperationException("ClickHouse did not return an async operation id.");
        }

        var operation = new ClickHouseOperationResult(rows[0][0], rows[0][1]);
        _logger.Information("ClickHouse async operation submitted: {OperationId} status {Status}.", operation.OperationId, operation.Status);
        return operation;
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

    private async Task<List<List<string>>> QueryAsync(ClickHouseNodeEndpoint endpoint, ClickHouseClusterEntity cluster, string sql, CancellationToken cancellationToken)
    {
        var settings = await CreateSettingsAsync(endpoint, cluster, cancellationToken);
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
            _logger.Error(ex, "ClickHouse query failed on {Host}:{Port}: {Message}. SQL: {SqlPreview}", endpoint.Host, settings.Port, ex.Message, Preview(sql));
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

    private static string Preview(string sql)
    {
        var compact = string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 400 ? compact : compact[..400] + "...";
    }

    private static string S3Endpoint(BackupTargetEntity target, string path)
    {
        return S3TargetUrlBuilder.BuildObjectUrl(target, path).ToString();
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
