---
name: chobo-api-contracts
description: Use when changing Chobo.Contracts, ChoboServer controllers, Swagger/OpenAPI output, ChoboWeb API calls, or generated TypeScript API shapes. Keeps browser DTOs synchronized with the HTTP API and fixes contract mismatches.
---

# Chobo API Contracts

Use `Chobo.Contracts` and ChoboServer Swagger/OpenAPI as the source of truth. Do not hand-invent browser DTOs when the server contract already exists.

## Workflow

1. Make server contract or controller changes first.
2. Build/run ChoboServer enough to expose `/swagger/v1/swagger.json`.
3. From `ChoboWeb`, run:
   ```powershell
   npm run update:api -- -ServerUrl http://localhost:8080
   ```
   Use `-SkipDownload` only when intentionally regenerating from the committed snapshot.
4. Inspect changes to:
   - `ChoboWeb/openapi/chobo.v1.json`
   - `ChoboWeb/src/api/generated.ts`
   - web API call sites and forms
5. Fix mismatches at the boundary:
   - prefer correcting `Chobo.Contracts` or controller response shapes when the API is wrong;
   - prefer correcting ChoboWeb code when the UI sent the wrong request body or enum casing.
6. Run `npm run typecheck`, `npm test`, and the relevant bounded `dotnet test` command.

## Rules

- Keep enum string casing aligned with server JSON.
- Keep credentials write-only in UI state and displays.
- Do not duplicate API types outside `src/api/generated.ts`; import from it.
- New mutating server endpoints must preserve Chobo audit invariants.
