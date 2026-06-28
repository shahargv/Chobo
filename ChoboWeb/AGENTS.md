# ChoboWeb Agent Notes

## Shape of the App

ChoboWeb is a Vite + React + TypeScript single page app.

Architecture overview:

- `src/main.tsx` boots React, React Router, and React Query.
- `src/App.tsx` is the application composition root: setup/install/login state, `ChoboApiClient` creation, API context, logout cleanup, and routes.
- `src/components/AppShell.tsx` is the persistent operations shell with navigation, top bar, toast placement, and main content frame.
- `src/pages/` contains route-level screens. Search here first for user-visible workflows: dashboard, backups, restores, policies, schedules, schema browser, clusters, targets, users, logs, audit, import/export, monitoring, queue, settings, and garbage collection.
- `src/pages/restores/` contains the restore workflow split into wizard, history, detail, types, and pure helpers because restore UX is larger than a single page.
- `src/components/` contains shared or cross-page components, plus a few focused editors/screens. Keep page-private components inside their page until reuse is real.
- `src/components/ui.tsx` is the local design-system layer: page shells, CRUD layout, drawers, tables, status badges, form controls, empty states, and details.
- `src/api/` contains API access. `client.ts` is the handwritten wrapper used by pages; `generated.ts` is generated/maintained from OpenAPI contract shapes.
- `src/api-context.ts` exposes the API client and toast hook through `useApi`.
- `src/auth.ts` owns token persistence.
- `src/policies.ts`, `src/schedule.ts`, and `src/utils/` contain pure helpers with targeted tests where behavior is non-trivial.
- `src/styles.css` is the global stylesheet and visual system for the operational UI.

Route map:

- `/` -> `pages/DashboardPage.tsx`
- `/backups` and `/backups/:backupId` -> `pages/BackupsPage.tsx`
- `/restores`, `/restores/start`, and `/restores/:restoreId` -> `pages/RestoresPage.tsx` re-exporting `pages/restores/*`
- `/policies` and `/policies/:policyId` -> `pages/PoliciesPage.tsx`
- `/schedules` and `/schedules/:scheduleId` -> `pages/SchedulesPage.tsx`
- `/clusters` and `/clusters/:clusterId` -> `pages/ClustersPage.tsx`
- `/targets`, `/users`, `/settings`, `/schema`, `/queue`, `/monitoring`, `/gc`, and `/import-export` -> same-named page modules under `src/pages`
- `/logs` and `/audit` -> `pages/EntriesPages.tsx`

- `src/App.tsx` is intentionally small. It owns auth state, `ChoboApiClient` creation, `ApiContext.Provider`, logout cleanup, and route registration only.
- `src/components/AppShell.tsx` owns the sidebar navigation, top bar, toast placement, and main layout shell.
- `src/components/LoginScreen.tsx` owns the token login form.
- `src/components/ui.tsx` owns shared UI primitives, including `Page`, `CrudPage`, `Drawer`, `DataTable`, `Status`, `Input`, `Select`, `Empty`, and `Detail`.
- `src/pages/*Page.tsx` files own route-level screens and their page-private helpers/editors.
- `src/api-context.ts` exposes the API client + toast context through `useApi`.
- `src/api/client.ts` is the handwritten HTTP client wrapper.
- `src/api/generated.ts` contains API DTO types and OpenAPI schema-name metadata. Treat it as generated.
- `src/auth.ts` owns token persistence.
- `src/policies.ts` and `src/schedule.ts` contain pure logic with Vitest coverage.
- `src/utils/arrays.ts` and `src/utils/format.ts` contain tiny cross-page helpers. Keep single-use helpers inside their owning page module.
- `src/styles.css` is the global stylesheet. Keep UI styling here unless a new extraction is genuinely worthwhile.

The app uses:

- React Query for server state and mutations.
- React Router for routes.
- TanStack Table through the local `DataTable` wrapper in `src/components/ui.tsx`.
- lucide-react for icons.

## Module Boundaries

Keep `App.tsx` boring. New pages should be added as route components under `src/pages`, then imported into `App.tsx` for routing.

Page modules should own their feature-specific nested components and validation helpers. For example:

- backup list/detail behavior lives in `pages/BackupsPage.tsx`;
- logs and audit paging/filtering lives in `pages/EntriesPages.tsx`;
- restore mapping/shard helpers live in `pages/RestoresPage.tsx`;
- policy selector/retention helpers live in `pages/PoliciesPage.tsx`;
- schedule editor UI lives in `pages/SchedulesPage.tsx`.

Only move code into `components` or `utils` when it is genuinely reused by multiple pages. Avoid growing another mega-file.

## API Contract Rules

Use `Chobo.Contracts` and the server OpenAPI snapshot as the source of truth. Do not invent duplicate DTOs in the UI.

When changing server contracts or controller response shapes:

1. Update the server contract/controller first.
2. Regenerate or update `ChoboWeb/openapi/chobo.v1.json` and `ChoboWeb/src/api/generated.ts`.
3. Prefer:
   ```powershell
   npm run update:api -- -ServerUrl http://localhost:8080
   ```
   Use `-SkipDownload` only when intentionally regenerating from the committed snapshot.
4. Inspect both `openapi/chobo.v1.json` and `src/api/generated.ts`.
5. Update `src/api/client.ts` call signatures and all call sites.

`generated.ts` currently has some manually maintained TypeScript interface content plus generated schema-name metadata. Preserve the header and the `openApiSchemaNames` marker because `scripts/generate-api.mjs` depends on them.

## Local Development Gotchas

`vite.config.ts` proxies `/api`, `/health`, and `/swagger` to `http://localhost:8080` by default. Some Chobo debug launch profiles run the backend on `http://localhost:5202`. If the UI shows `Connecting...` or empty data while the backend is running, check the proxy target and the backend port before debugging React code.

The dev token commonly used in local debug is:

```text
dev-static-access-token
```

The app stores auth tokens under:

- `localStorage["chobo.auth.remembered"]`
- `sessionStorage["chobo.auth.session"]`

## UI Patterns

Use existing primitives in `src/components/ui.tsx` before adding new ones:

- `Page` for route pages.
- `CrudPage` for simple CRUD screens.
- `Drawer` for right-side details/edit panels.
- `DataTable` for tables/grids.
- `Status` for status badges.
- `Input`, `Select`, `Empty`, and `Detail` for common controls.

`DataTable` wraps TanStack Table. It parses `<tr><td>...</td></tr>` children into table data and provides:

- global search;
- sortable columns;
- per-column filters;
- select filters for small categorical columns;
- empty states.

Do not reimplement table search/filter/sort in individual pages. If a page needs table behavior, improve `DataTable` or pass better cell text/classes into it.

Long text in tables should not crush layout. Prefer table-specific CSS classes such as `wide-cell`, `mono`, or a scoped section class. For drawer/detail tables, allow horizontal scroll rather than shrinking every column.

## Logs, Audit, and Backup Detail

Logs and Audit use backend paging and time filtering:

- endpoints return `PagedResultDto<T>`;
- default UI window is last 1 hour;
- page size defaults to 200;
- pagination is offset/limit based.

Backup detail drawers load related logs/audit by the single Chobo backup operation id (`backup.id`). Do not correlate related activity heuristically through table ids, shard ids, ClickHouse operation ids, S3 paths, or timestamps. ClickHouse async ids belong in `clickHouseOperationId`, not `operationId`.

## Styling Guidance

Keep the UI dense and operational. Chobo is an admin/operations tool, not a marketing site.

- Prefer compact layouts, scannable tables, restrained colors, and predictable controls.
- Use lucide icons inside action buttons when an icon exists.
- Avoid nested cards and decorative sections.
- Make fixed-format controls stable with explicit sizing.
- For drawer/table-heavy views, prioritize readable columns and usable scroll over fitting everything into one viewport.

## Testing and Validation

Run these from `ChoboWeb` after frontend changes:

```powershell
npm run build
```

Run tests when pure logic or tested helpers change:

```powershell
npm test
```

Run typecheck alone when you only need TS validation:

```powershell
npm run typecheck
```

For rendered UI changes, also do a browser sanity check against a running dev server. Verify:

- the target route renders and is not blank;
- there is no Vite/React error overlay;
- console has no relevant errors/warnings;
- the changed interaction works;
- text does not overlap or get clipped at normal desktop width.

If backend data is needed, make sure the Vite proxy points at the backend you actually started.

## Change Hygiene

- Keep unrelated refactors out of feature fixes.
- Do not edit build output under `dist`.
- Do not hand-edit `package-lock.json` except as the result of an intentional dependency change.
- If adding a dependency, prefer existing libraries already in `package.json` first.
- Preserve user work in dirty files; do not revert unrelated changes.
