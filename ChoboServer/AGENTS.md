# ChoboServer Agent Notes

ChoboServer is the ASP.NET host and infrastructure edge for Chobo. Keep controllers thin: bind routes, call application services, and translate results to HTTP. Business rules, credential handling, selector evaluation, audit decisions, and persistence orchestration belong in application services or repositories.

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