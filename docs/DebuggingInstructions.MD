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

That means normal local debugging does not require running `ChoboServer init` every time. On first startup, ChoboServer creates the SQLite database under `data-dev`, creates the admin user, and stores the deterministic access token. On later startups, it reuses the same database and AES key.

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
dotnet run --project .\ChoboCli\ChoboCli.csproj -- clusters add --name debug-cluster --mode Cluster --node localhost:18111,localhost:18112,localhost:18121,localhost:18122,localhost:18131,localhost:18132 --username default --backup-restore-maxdop 3
```

Add MinIO as the backup target:

```powershell
dotnet run --project .\ChoboCli\ChoboCli.csproj -- targets add-s3 --name debug-minio --endpoint http://minio:9000 --bucket data-bucket --access-key chobo-access-key --secret-key chobo-secret-key --force-path-style
```

Use `http://minio:9000` for the Chobo backup target because ClickHouse performs the backup and restore S3 calls from inside the Docker network. Use `http://localhost:9000` only for host-side tools that talk directly to MinIO.

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
