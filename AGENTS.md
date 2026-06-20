# Chobo Agent Notes

## Architecture

ChoboServer follows Onion Architecture.

- Domain contracts and stable DTOs live in `Chobo.Contracts`.
- Application services own use-case logic, validation, audit decisions, and orchestration.
- Repository interfaces hide persistence from application logic.
- EF Core/SQLite repositories are infrastructure details.
- ASP.NET controllers are thin adapters only: route, bind, call application service, translate result to HTTP.

Keep boundaries crisp. Do not put business logic, credential handling, selector evaluation, audit decisions, or persistence decisions directly in controllers. New features should be easy to test at the application-service layer without booting HTTP.

## Audit invariant

Every new feature that changes configuration, user-visible state, or performs an automatic action must write an audit record.

Audit records must include:

- the authenticated user id and user name for user/API/CLI initiated actions;
- the reserved `system` actor for server startup, scheduled/background, retention, import, and local maintenance actions;
- action, entity type, entity id when available, timestamp, and concise JSON details.

Do not add mutating endpoints, CLI commands, background jobs, or local server commands without updating the audit path and tests.

## Versioning and migrations

Chobo has separate product/server, API, export, and SQLite schema versions. Keep them explicit and easy to advance.

- The CLI must call the API version it was built for and check `/api/v1/server/version` before normal commands.
- Server startup must know the current SQLite schema version and reject newer schemas.
- When increasing schema version, add a custom migration step in the schema upgrade service. EF schema changes alone are not enough if data transformation is needed.
- Import/export envelopes must remain versioned and must not expose raw access tokens or decrypted credentials.

## Configuration

Server runtime code should use typed options, not raw `IConfiguration`.

- Add option classes under `ChoboServer/Options` for each configuration concern.
- Register options in DI with `IOptions<T>`.
- Inject only the relevant `IOptions<T>` into each service.
- Keep direct `IConfiguration` access limited to composition/bootstrap code such as `Program.cs`, `ServiceCollectionExtensions`, and local command configuration assembly.
- Keep `appsettings.Development.json` complete enough to run a locally initialized development server without extra flags.

## Tests

`dotnet test` can hang for a long time when a database, HTTP test host, or external command is stuck. Always run tests with both:

- a shell/tool timeout; and
- a test hang timeout, for example `dotnet test Chobo.Tests\Chobo.Tests.csproj -v minimal --blame-hang --blame-hang-timeout 30s`.

Do not run open-ended test commands.

## Failure handling

Backup and restore work must be failure-friendly, not only success-oriented. Failure paths should finish instead of getting stuck, write a clear audit record, produce useful logs, and expose enough user-facing metadata to diagnose the issue from CLI/API output without spelunking first. Persist concise failure reasons on the relevant run records and keep detailed table/shard errors correlated with operation ids, nodes, and storage paths.

## Build artifacts

Builds must support both standalone binaries and Docker images. Docker images should build inside Docker from source. Release artifacts should go under `.artifacts/build/<Configuration>/`.

## Import/export contract

`/api/v1/data/export` must export all restorable Chobo metadata except audit entries and application logs. This includes users, access tokens, clusters, backup targets, policies, schedules, schema definitions, backup runs, backup tables/shards, restores, and restore tables/shards.

`/api/v1/data/import` must restore the same exportable metadata and must not overwrite or import audit entries or application logs. It should append its own import audit record.

`/api/v1/config/export` and `/api/v1/config/import` remain configuration-only. Config import must not silently destroy backup/restore history.

Export envelopes may carry encrypted credential fields for compatibility, but import must treat ClickHouse and S3 credentials as empty. Operators must re-enter credentials after import so they are encrypted with the current server key. Never import raw access tokens, decrypted credentials, or local AES key material.
