# ChoboServer Agent Notes

ChoboServer is the ASP.NET host and infrastructure edge for Chobo. Keep controllers thin: bind routes, call application services, and translate results to HTTP. Business rules, credential handling, selector evaluation, audit decisions, and persistence orchestration belong in application services or repositories.

## Architecture Map

ChoboServer follows Onion Architecture with ASP.NET, EF Core/SQLite, background services, ClickHouse, and S3 adapters around the application layer.

- `Program.cs` builds the web host, configures Serilog/OpenTelemetry/static GUI hosting, runs database initialization, maps middleware/controllers, and should stay composition-focused.
- `ServiceCollectionExtensions.cs` is the main dependency graph: typed options, controllers, Swagger, DbContext, repositories, application services, infrastructure services, queues, and hosted services are registered here.
- `Controllers/` contains HTTP adapters for API resources such as clusters, targets, policies, schedules, backups, restores, logs, audit, import/export, settings, schema browsing, queue, metrics, server version, users, and gated test hooks.
- `Application/` contains use-case orchestration. Start here for behavior changes: cluster/target/policy/schedule CRUD, backup/restore preparation and runners, selector evaluation, dashboard aggregation, schema browsing, queue operations, cleanup, and storage manifest logic.
- `Repositories/` contains repository interfaces plus EF/SQLite implementations and `EfUnitOfWork`. Persistence decisions should be hidden here rather than leaking into application services or controllers.
- `Data/` contains EF Core entities, DbContext, migrations, and database maintenance/bootstrap details.
- `Services/` contains infrastructure and cross-cutting services: audit, actor context, token auth, credential protection, ClickHouse access, S3 backup storage, import/export, runtime settings, endpoint rewrites, operation correlation, schema upgrades, logs, and test-hook coordination.
- `BackgroundServices/` contains hosted workers for backup/restore execution and resume, scheduling, retention, garbage collection, SQLite self-backup, and data retention.
- `Options/` contains typed configuration bound in DI. Add new runtime configuration here and inject `IOptions<T>` into the smallest service that needs it.

## Where To Start

- Backup/restore behavior: `Application/*Backup*`, `Application/*Restore*`, `BackgroundServices/*Backup*`, `BackgroundServices/*Restore*`, `Services/BackupStorage*`, and backup/restore repositories/data entities.
- Auth, install, users, and tokens: `Services/TokenAuthMiddleware.cs`, `Services/TokenService.cs`, `Application/UserApplicationService.cs`, `Controllers/UsersController.cs`, and user/token contracts.
- Import/export: `Services/ExportImportService.cs`, `Controllers/ImportExportController.cs`, and `Chobo.Contracts/ExportContracts.cs`.
- Policies and schedules: `Application/PolicyApplicationService.cs`, `Application/PolicySelectorEvaluationService.cs`, `Application/ScheduleApplicationService.cs`, scheduler background services, and policy/schedule controllers.
- Logs and audit: `Services/ApplicationLogSqliteSink.cs`, `Repositories/ApplicationLogStore.cs`, `Services/AuditService.cs`, `Repositories/AuditStore.cs`, and logs/audit controllers.
- Startup, schema, and local storage: `Program.cs`, `ServiceCollectionExtensions.cs`, `Services/DatabaseBootstrap.cs`, `Services/SchemaUpgradeService.cs`, `Data/`, and `ChoboPaths.cs`.

Follow the Onion Architecture boundaries used by the rest of the repo:

- stable DTOs and contracts live in `Chobo.Contracts`;
- application services own use-case logic, validation, audit records, and orchestration;
- repository interfaces hide persistence from application logic;
- EF Core/SQLite entities, migrations, and repositories are infrastructure details.

Every mutating endpoint, local command, background action, or test-only mutation must preserve the audit invariant unless it is intentionally excluded as a local diagnostic hook. Audit records need actor, action, entity type/id when available, timestamp, and concise JSON details.

Do not expose test hooks in normal deployments. Test hook endpoints must require both explicit `Chobo:TestHooks:Enabled` configuration and the dedicated `SystemTest` ASP.NET environment, and they must stay hidden from OpenAPI. Destructive hooks such as crash, SQLite deletion, schema mutation, or direct metadata seeding are for the TestingSuite only.

Use typed options under `ChoboServer/Options`; keep direct `IConfiguration` access limited to bootstrap/composition code. Import/export must never import raw access tokens, decrypted credentials, or local AES key material. Imported ClickHouse/S3 credentials should be stored empty so operators re-enter them under the current server key.

When changing SQLite schema or startup compatibility, keep `ChoboApi.SchemaVersion`, migrations, `DatabaseBootstrap`, and `SchemaUpgradeService` aligned. The startup guard should reject newer schemas and allow older supported schemas into upgrade code.

Run bounded tests. Prefer:

```powershell
dotnet test Chobo.Tests\Chobo.Tests.csproj -v minimal --blame-hang --blame-hang-timeout 30s
```