# ChoboWeb UI State

## Decisions

- `ChoboWeb` is a React + Vite + TypeScript SPA.
- ChoboServer serves the GUI by default on the same port as the API.
- `Chobo:Web:IsGuiEnabled` disables GUI serving when false.
- `Chobo:Web:GuiPort` is nullable; null means same port, a value adds a GUI listener.
- Browser auth uses existing Chobo access tokens and Bearer API calls.
- Schedule editing includes presets plus an always-visible editable Quartz cron expression.
- Schedule saves require the current cron expression to validate through the server.
- Policy editing includes a visual ordered selector builder plus JSON preview.
- Chobo API contracts are synchronized via `.codex/skills/chobo-api-contracts`.

## Current Implementation

- Backend options/config aliases/static serving are implemented.
- ChoboWeb source includes dashboard, backups, restores, policies, schedules, clusters, targets, users, logs, audit, and import/export screens.
- API type sync scripts and initial OpenAPI snapshot are present.
- The scheduler skips stored schedules with invalid cron expressions and writes an audit record plus warning log.

## Follow-ups

- Replace the initial placeholder OpenAPI snapshot with a downloaded Swagger snapshot once the server is running.
- Expand generated type generation if future API churn makes the current lightweight generator insufficient.
- Add richer live ClickHouse inventory support for selector previews when the backend exposes it.
