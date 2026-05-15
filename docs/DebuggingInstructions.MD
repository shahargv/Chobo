# Local Debugging Instructions

This setup is for debugging ChoboServer and ChoboCli on the host while the external services run in Docker. It creates:

- MinIO with deterministic test credentials.
- One ClickHouse Keeper.
- One 3-shard, 2-replica ClickHouse cluster named `chobo_debug_cluster`.
- One standalone ClickHouse server.

## Start The Docker Services

From the repository root:

```powershell
docker compose -f .\resources\debug\docker-compose.yml up -d
```

Check status:

```powershell
docker compose -f .\resources\debug\docker-compose.yml ps
```

Stop the services and keep data:

```powershell
docker compose -f .\resources\debug\docker-compose.yml down
```

Stop the services and reset Docker volumes:

```powershell
docker compose -f .\resources\debug\docker-compose.yml down -v
```

## Exposed Ports

| Service | Container name | Host HTTP | Host native | Notes |
| --- | --- | ---: | ---: | --- |
| MinIO API | `chobo-debug-minio` | `9000` | n/a | S3-compatible endpoint from the host. |
| MinIO console | `chobo-debug-minio` | `9001` | n/a | Browser UI. |
| ClickHouse Keeper | `chobo-debug-clickhouse-keeper` | n/a | `9181`, `9234` | Keeper client and Raft ports. |
| Single ClickHouse | `chobo-debug-clickhouse-single` | `18100` | `19100` | Standalone node. |
| Cluster shard 1 replica 1 | `chobo-debug-clickhouse-cluster-s1-r1` | `18111` | `19111` | First cluster access node. |
| Cluster shard 1 replica 2 | `chobo-debug-clickhouse-cluster-s1-r2` | `18112` | `19112` |  |
| Cluster shard 2 replica 1 | `chobo-debug-clickhouse-cluster-s2-r1` | `18121` | `19121` |  |
| Cluster shard 2 replica 2 | `chobo-debug-clickhouse-cluster-s2-r2` | `18122` | `19122` |  |
| Cluster shard 3 replica 1 | `chobo-debug-clickhouse-cluster-s3-r1` | `18131` | `19131` |  |
| Cluster shard 3 replica 2 | `chobo-debug-clickhouse-cluster-s3-r2` | `18132` | `19132` |  |

MinIO credentials and bucket:

```text
Access key: chobo-access-key
Secret key: chobo-secret-key
Bucket: data-bucket
```

The MinIO browser console is available at `http://localhost:9001`.

## Connect To ClickHouse

HTTP examples:

```powershell
Invoke-RestMethod -Method Post -Uri http://localhost:18100/ -Body "SELECT 1"
Invoke-RestMethod -Method Post -Uri http://localhost:18111/ -Body "SELECT cluster, shard_num, replica_num, host_name FROM system.clusters WHERE cluster = 'chobo_debug_cluster'"
```

Native protocol examples:

```powershell
docker run --rm -it --network chobo-debug clickhouse/clickhouse-server:25.4 clickhouse-client --host clickhouse-single
docker run --rm -it --network chobo-debug clickhouse/clickhouse-server:25.4 clickhouse-client --host clickhouse-cluster-s1-r1
```

To create a replicated table for cluster testing:

```sql
CREATE DATABASE IF NOT EXISTS debug ON CLUSTER chobo_debug_cluster;

CREATE TABLE debug.events ON CLUSTER chobo_debug_cluster
(
    id UInt64,
    message String
)
ENGINE = ReplicatedMergeTree('/clickhouse/tables/{shard}/debug/events', '{replica}')
ORDER BY id;
```

## Run ChoboServer For Debugging

Use the `debug-local` launch profile in Visual Studio, Rider, or VS Code. It sets `ASPNETCORE_ENVIRONMENT=Development`, pins the verified debug data directory, and serves ChoboServer on:

```text
http://localhost:5202
```

The development settings already include deterministic startup configuration:

```text
Chobo:DataDirectory = data-dev
Chobo:EncryptionKeyBase64 = MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=
Chobo:Init:AdminUser = admin
Chobo:Init:AccessToken = dev-static-access-token
```

On first startup, ChoboServer creates the SQLite database under `data-dev`, creates the admin user, stores the deterministic access token, and writes the `_initialized` marker. On later startups, it reuses the same database and AES key files.

The repository-relative `data-dev` default keeps the debug database local to the workspace without depending on machine-specific user profile paths.

Command-line equivalent:

```powershell
dotnet run --project .\ChoboServer\ChoboServer.csproj --launch-profile debug-local
```

If you need an explicit environment-based run instead of the launch profile, use `resources\debug\chobo-server.env` as the source of truth for the stable AES key and startup token:

```powershell
Get-Content .\resources\debug\chobo-server.env | ForEach-Object {
    if ($_ -and -not $_.StartsWith("#")) {
        $name, $value = $_.Split("=", 2)
        [Environment]::SetEnvironmentVariable($name, $value, "Process")
    }
}
dotnet run --project .\ChoboServer\ChoboServer.csproj
```

## Configure ChoboCli

Authenticate the CLI profile once:

```powershell
dotnet run --project .\ChoboCli\ChoboCli.csproj -- server auth --server-url http://localhost:5202 --access-token dev-static-access-token
```

Add the standalone ClickHouse node:

```powershell
dotnet run --project .\ChoboCli\ChoboCli.csproj -- clusters add --name debug-single --mode SingleInstance --node localhost:18100 --username default
```

Add the 3-shard, 2-replica cluster:

```powershell
dotnet run --project .\ChoboCli\ChoboCli.csproj -- clusters add --name debug-cluster --mode Cluster --node localhost:18111,localhost:18112,localhost:18121,localhost:18122,localhost:18131,localhost:18132 --username default --backup-restore-maxdop 3 --clickhouse-cluster-name chobo_debug_cluster
```

Add MinIO as the backup target:

```powershell
dotnet run --project .\ChoboCli\ChoboCli.csproj -- targets add-s3 --name debug-minio --endpoint http://minio:9000 --bucket data-bucket --access-key chobo-access-key --secret-key chobo-secret-key --force-path-style
```

Use `http://minio:9000` for the Chobo backup target because ClickHouse performs the backup and restore S3 calls from inside the Docker network. Use `http://localhost:9000` only for host-side tools that talk directly to MinIO.

## Debug Sharded Backup And Restore

Chobo backs up clustered MergeTree tables one shard at a time. It queries `system.clusters`, chooses one representative replica per shard, and runs ClickHouse backup/restore commands manually on the selected nodes. It intentionally does not use ClickHouse `ON CLUSTER` for backup/restore, because Chobo needs per-shard status, retry, and audit records.

Check the topology Chobo will use:

```powershell
Invoke-RestMethod -Method Post -Uri http://localhost:18111/ -Body "SELECT cluster, shard_num, replica_num, host_name, port, errors_count FROM system.clusters WHERE cluster = 'chobo_debug_cluster' ORDER BY shard_num, replica_num"
```

Create a simple local table on every shard and insert different data on two shards:

```powershell
Invoke-RestMethod -Method Post -Uri http://localhost:18111/ -Body @"
DROP DATABASE IF EXISTS debug_backup ON CLUSTER chobo_debug_cluster SYNC;
CREATE DATABASE debug_backup ON CLUSTER chobo_debug_cluster ENGINE = Atomic;
CREATE TABLE debug_backup.events_local ON CLUSTER chobo_debug_cluster
(
    id UInt64,
    shard UInt8,
    message String
)
ENGINE = MergeTree
ORDER BY id;
INSERT INTO debug_backup.events_local VALUES (1, 1, 'shard-one');
"@

Invoke-RestMethod -Method Post -Uri http://localhost:18121/ -Body "INSERT INTO debug_backup.events_local VALUES (2, 2, 'shard-two')"
```

Start and inspect a sharded backup:

```powershell
$cluster = dotnet run --project .\ChoboCli\ChoboCli.csproj -- clusters list | ConvertFrom-Json | Where-Object name -eq 'debug-cluster'
$target = dotnet run --project .\ChoboCli\ChoboCli.csproj -- targets list | ConvertFrom-Json | Where-Object name -eq 'debug-minio'

$selector = @{
  version = 1
  rules = @(@{
    action = 'Include'
    database = @{ kind = 'Exact'; value = 'debug_backup' }
    table = @{ kind = 'Exact'; value = 'events_local' }
  })
} | ConvertTo-Json -Depth 10
$selector | Set-Content .\debug-selector.json

$backup = dotnet run --project .\ChoboCli\ChoboCli.csproj -- backup manual --cluster-id $cluster.id --target-id $target.id --selector-file .\debug-selector.json | ConvertFrom-Json
dotnet run --project .\ChoboCli\ChoboCli.csproj -- backups wait --id $backup.id --timeout-seconds 300 --poll-seconds 2
dotnet run --project .\ChoboCli\ChoboCli.csproj -- backups show --id $backup.id
```

The backup JSON should show one logical table with a `shards` array. Each shard row includes `sourceShardNumber`, selected host/port, S3 path, ClickHouse operation id/status, and any error.

Restore layout examples:

```powershell
# Preserve layout when source and target shard counts match.
dotnet run --project .\ChoboCli\ChoboCli.csproj -- restore initiate --backup-id $backup.id --target-cluster-id $cluster.id --database debug_backup --table events_local --target-database debug_restore --target-table events_local --layout preserve

# Restore only one source shard to the standalone node.
$single = dotnet run --project .\ChoboCli\ChoboCli.csproj -- clusters list | ConvertFrom-Json | Where-Object name -eq 'debug-single'
dotnet run --project .\ChoboCli\ChoboCli.csproj -- restore initiate --backup-id $backup.id --target-cluster-id $single.id --database debug_backup --table events_local --target-database debug_single_restore --target-table shard_one_events --layout single-node --source-shard 1
```

When a restore partially succeeds, `restores show` reports `PartiallySucceeded` on the run and table, and each shard has its own status and error. Audit records to check:

```powershell
dotnet run --project .\ChoboCli\ChoboCli.csproj -- audit show --last 200
```

Look for `shard-failed`, `table-partially-succeeded`, and restore-level `partially-succeeded` entries. The dashboard and metrics also expose active shard counts and per-policy partial/failed backup totals.

## Useful Checks

List configured Chobo resources:

```powershell
dotnet run --project .\ChoboCli\ChoboCli.csproj -- clusters list
dotnet run --project .\ChoboCli\ChoboCli.csproj -- targets list
```

Check ChoboServer health:

```powershell
Invoke-RestMethod http://localhost:5202/health
```

Follow Docker logs:

```powershell
docker compose -f .\resources\debug\docker-compose.yml logs -f
```
