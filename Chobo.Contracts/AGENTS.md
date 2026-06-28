# Chobo.Contracts Agent Notes

Chobo.Contracts is the stable DTO and versioning project shared by ChoboServer, ChoboCli, tests, and the web API generation flow. Treat it as the public API surface, not a place for server business logic.

## Architecture Map

- `ChoboApi.cs` defines product/API/export/SQLite schema version constants and the API prefix. Keep these explicit and advance them deliberately with matching server, CLI, migration, import/export, and OpenAPI changes.
- `CommonDtos.cs` contains cross-cutting response envelopes and shared primitives such as `ErrorResponse`, `ServerVersionDto`, install DTOs, `QueryWindow`, and `PagedResultDto<T>`.
- `*Contracts.cs` files group request/response records by API resource: users, access tokens, clusters, ClickHouse settings, backup targets, policies, schedules, runtime settings, dashboard, connection tests, logs, audit, backup/restore, storage manifests, import/export, and test hooks.
- Contract records are transport DTOs. Keep them serializable, stable, and free of persistence entities, EF attributes, server services, credential protection logic, or UI-only state.
- The server OpenAPI snapshot and `ChoboWeb/src/api/generated.ts` are downstream of these contracts plus controller action shapes. When contracts change, refresh and inspect the OpenAPI/web generated files.
- The CLI should call the API version it was built for and check `/api/v1/server/version` before normal commands. Contract changes that affect CLI behavior need matching `ChoboCli` updates.

## Change Rules

- Prefer additive DTO changes when possible. Renames, removed fields, enum changes, and response-shape changes can break the CLI, web app, imports/exports, and tests.
- Never add raw access tokens, decrypted credentials, or AES key material to export/import contracts.
- Import/export envelopes are versioned. Data export/import should cover restorable metadata except audit entries and application logs; config import/export remains configuration-only.
- Keep nullable fields intentional and document behavior through names and server validation rather than comments on every property.
- If a contract change touches SQLite schema compatibility or exported metadata, also review `ChoboApi.SchemaVersion`, migration/upgrade code, and import/export tests.

## Contract Change Checklist

1. Update the contract records and the matching server controller/application service behavior.
2. Refresh and inspect `ChoboWeb/openapi/chobo.v1.json` and `ChoboWeb/src/api/generated.ts`.
3. Update `ChoboWeb/src/api/client.ts`, page call sites, and UI tests/helpers affected by the new shape.
4. Update `ChoboCli/Infrastructure/ChoboApiClient.cs`, command subjects, and `ChoboCli/COMMANDS.md` when CLI-visible requests or responses change.
5. Update import/export, versioning, and migration tests when metadata shape, export shape, API version, or schema version changes.