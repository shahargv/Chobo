# Production Setup

This guide describes a production-oriented Chobo deployment. For local development with Docker-hosted ClickHouse and MinIO, see [Local debugging instructions](DebuggingInstructions.MD).

## Components

A production deployment has four moving pieces:

- `ChoboServer`: the HTTP API and background worker host.
- `ChoboCli`: the operator CLI.
- SQLite metadata database: stored by ChoboServer under the configured data directory.
- ClickHouse and S3-compatible storage: external systems that Chobo backs up from, restores into, and writes backup objects to.

ChoboServer owns scheduling, retention, audit records, application logs, backup execution, restore execution, and deletion of expired or manually deleted backup objects.

## Requirements

- .NET 10 runtime for framework-dependent binaries, or the self-contained release artifacts built by `scripts/Build-Artifacts.ps1`.
- Network access from ChoboServer to ClickHouse HTTP(S) endpoints. Chobo uses the official `ClickHouse.Driver` ADO.NET package; configured native-default ports are mapped internally from `9000` to `8123`, and from TLS `9440` to `8443`.
- Network access from ClickHouse nodes to the configured S3 endpoint, because ClickHouse itself performs the `BACKUP ... TO S3(...)` and `RESTORE ... FROM S3(...)` calls.
- Network access from ChoboServer to the S3 endpoint for backup deletion during retention, failed-backup cleanup, and manual delete.
- Persistent storage for the Chobo data directory.
- A stable 32-byte encryption key encoded as Base64.

## Build Artifacts

From the repository root:

```powershell
.\scripts\Build-Artifacts.ps1 -Configuration Release
```

The script publishes self-contained binaries under:

```text
.artifacts/build/Release/
```

It creates these binary directories:

- `cli-win-x64`
- `cli-linux-x64`
- `server-win-x64`
- `server-linux-x64`

It also builds local Docker images:

```text
choboserver:local
chobocli:local
```

The server and CLI Dockerfiles build from source inside Docker.

## Initialize Server State

ChoboServer creates and upgrades its SQLite database on startup. On first startup, when `chobo.db` and `_initialized` are both absent from the data directory, it creates the first admin user and access token automatically.

Provide init settings on first server startup when you need deterministic credentials:

```powershell
$env:CHOBO_INIT_ADMIN_USER = "admin"
$env:CHOBO_INIT_ACCESS_TOKEN = "<long-random-token>"
```

When `CHOBO_INIT_ACCESS_TOKEN` is omitted, ChoboServer generates a token and prints it once directly to stdout during first startup. Store that value securely.

## Run ChoboServer

Set production configuration through environment variables or an appsettings file. A minimal Windows example:

```powershell
$env:ASPNETCORE_URLS = "http://0.0.0.0:8080"
$env:CHOBO_DATA_DIRECTORY = "C:\ProgramData\Chobo"
$env:CHOBO_ENCRYPTION_KEY_BASE64 = "<base64-32-byte-key>"
.\ChoboServer.exe
```

A minimal Linux example:

```bash
ASPNETCORE_URLS=http://0.0.0.0:8080 \
CHOBO_DATA_DIRECTORY=/var/lib/chobo \
CHOBO_ENCRYPTION_KEY_BASE64=<base64-32-byte-key> \
./ChoboServer
```

The server exposes:

- `GET /health` without authentication.
- `/api/v1/*` with bearer-token authentication.
- `GET /api/v1/server/version` with bearer-token authentication.
- `/openapi/*` only in the Development environment.

Put ChoboServer behind your normal TLS termination layer. Chobo's API authentication is bearer-token based, but production traffic should still be protected by TLS.

## Authenticate The CLI

Persist the server URL and access token once:

```powershell
ChoboCli server auth --server-url https://chobo.example.com --access-token <token>
```

The CLI profile is stored at:

```text
%USERPROFILE%\.chobo\config.json
```

You can also pass `--server-url` and `--access-token` per command when automation should avoid a persisted profile.

## Configure Resources

Add a ClickHouse source cluster. For a single node:

```powershell
ChoboCli clusters add --name prod-single --mode SingleInstance --host clickhouse-1.example.com --port 9000 --username default --password <password>
```

For a ClickHouse cluster:

```powershell
ChoboCli clusters add --name prod-cluster --mode Cluster --node ch1:9000,ch2:9000,ch3:9000 --username default --password <password> --backup-restore-maxdop 3 --clickhouse-cluster-name prod_cluster
```

For `Cluster` mode, `--clickhouse-cluster-name` should match the value in ClickHouse `system.clusters.cluster`. If it is omitted, Chobo can auto-discover the cluster name only when `system.clusters` contains exactly one cluster definition.

Chobo accepts the usual ClickHouse native-default ports in CLI commands for compatibility. Internally, the server talks to ClickHouse over HTTP(S): `9000` maps to `8123`, and TLS `9440` maps to `8443`. If your deployment exposes custom HTTP(S) ports, pass those ports directly.

Add an S3-compatible target:

```powershell
ChoboCli targets add-s3 --name prod-s3 --endpoint https://s3.example.com --region us-east-1 --bucket chobo-backups --path-prefix prod --access-key <key> --secret-key <secret>
```

Use `--force-path-style` when required by your S3-compatible service.

Credentials are write-only in the API and CLI output.

## Operational Checks

Check health:

```powershell
Invoke-RestMethod https://chobo.example.com/health
```

Check configured resources:

```powershell
ChoboCli clusters list
ChoboCli targets list
ChoboCli policies list
ChoboCli schedules list
```

Check active work and upcoming schedules:

```powershell
ChoboCli dashboard --next-hours 12
ChoboCli metrics show
```

Review audit and application logs:

```powershell
ChoboCli audit show --last 200
ChoboCli logs show --last 500
```


## Import And Export

Data export/import is intended for Chobo metadata portability and disaster recovery. `data export` includes all restorable Chobo metadata except audit entries and application logs: users, access tokens, clusters, backup targets, policies, schedules, schema definitions, backups, backup tables and shards, restores, and restore tables and shards.

Import does not restore audit entries or application logs. The importing server keeps its local audit/log history and writes a new import audit record.

Imported ClickHouse and S3 credentials are intentionally empty. After importing, update cluster credentials and backup target credentials before running connection tests, backups, restores, cleanup, or metadata recovery that needs those resources. The next save encrypts the credentials with the current server key.

Use config export/import only for configuration-only moves. Config import is not a way to preserve existing backup/restore history; use data export/import for full metadata recovery.
## Upgrade Notes

Chobo tracks separate API, export, product/server, and SQLite schema versions. The current API path is `/api/v1`.

On startup, ChoboServer checks the SQLite schema version. It rejects databases newer than the server-supported schema and applies registered schema upgrade steps for older supported databases.

ChoboCli checks `/api/v1/server/version` before normal commands and fails when the server API version does not match the API version the CLI was built for. Upgrade the server and CLI together.

Before upgrading production:

- Back up the Chobo data directory, including `chobo.db`.
- Keep the same `CHOBO_ENCRYPTION_KEY_BASE64`; changing it makes stored credentials unreadable.
- Verify the new server can reach ClickHouse and S3.
- Check `/health`, `server auth`, `dashboard`, and `audit show` after startup.

