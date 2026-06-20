---
name: chobo-openapi-refresh
description: Refresh and verify Chobo OpenAPI and generated TypeScript contracts. Use when changing Chobo.Contracts DTOs, ChoboServer controllers/routes, Swagger/OpenAPI output, generated web API types, or when API drift appears in ChoboWeb; especially use after hiding/removing endpoints such as test hooks or stale Monitoring paths.
---

# Chobo OpenAPI Refresh

Use this skill to update Chobo's OpenAPI snapshot and generated TypeScript without repeating the fragile server-launch steps by hand.

## Workflow

1. Check current branch and working tree so unrelated changes are not lost.
2. Build the server first unless a fresh build already exists.
3. Refresh OpenAPI by running the bundled helper from the repo root:

```powershell
.codex\skills\chobo-openapi-refresh\scripts\update-chobo-openapi.ps1
```

Use `-NoBuild` only after a successful local build in the same working tree. The helper starts `ChoboServer` with `--no-launch-profile`, `ASPNETCORE_ENVIRONMENT=Development`, a temp `Chobo__DataDirectory`, and no `CHOBO_TEST_HOOKS_ENABLED`, then downloads `/swagger/v1/swagger.json` into `ChoboWeb/openapi/chobo.v1.json`.

4. Let the helper run `npm run generate:api` and `npm run typecheck` in `ChoboWeb`.
5. Verify drift explicitly:

```powershell
npm run check:api
rg -n "test-hooks|metrics/prometheus|prometheusMetrics" ChoboWeb/openapi/chobo.v1.json ChoboWeb/src -g "*.json" -g "*.ts" -g "*.tsx"
```

The `rg` command should return no matches for hidden test hooks or removed Monitoring paths.

## Common Failure Modes

- If Swagger fetch hangs or hits the wrong port, confirm the server was launched with `--no-launch-profile`; launch settings override `ASPNETCORE_URLS` during `dotnet run`.
- If test-hook endpoints remain in `chobo.v1.json`, confirm `TestHooksController` has `[ApiExplorerSettings(IgnoreApi = true)]` and the refresh was run against the rebuilt server.
- If generated TypeScript keeps stale methods, update `ChoboWeb/scripts/generate-api.mjs` or `ChoboWeb/src/api/client.ts`; do not hand-edit `src/api/generated.ts` except to repair the generator.
- If nullable or optional shapes drift, fix the generator rules and rerun the helper.

## Validation

For contract-only changes, run:

```powershell
npm run check:api
npm run typecheck
```

For server route/DTO changes, also run bounded backend tests:

```powershell
dotnet test Chobo.Tests\Chobo.Tests.csproj -v minimal --blame-hang --blame-hang-timeout 30s
```
