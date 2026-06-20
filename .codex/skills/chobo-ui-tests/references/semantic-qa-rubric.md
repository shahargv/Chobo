# Semantic UI QA Rubric

Judge Chobo screens like an experienced manual QA engineer using Chrome. Do not require exact element positions, exact labels, or pixel-perfect screenshots.

## Navigation And Orientation

- The current section is clear from page heading, navigation state, or main content.
- The user can infer the next action without reading source code or API docs.
- A partial or empty state explains what is missing and how to proceed.
- Back/forward navigation and route reloads do not lose important persisted state.

## Forms

- Required fields are discoverable.
- Defaults are sensible for the current task.
- Disabled save/next buttons have nearby validation or progress context.
- Validation failures are specific enough to fix.
- Cancel/reset behavior does not create accidental records.
- Secret fields are not shown after save unless the user is actively replacing them.

## Operations

- Queued/running/succeeded/failed/partially-succeeded states are visible and understandable.
- Long operations expose progress or a refresh path.
- Detail pages show enough context to diagnose issues: operation id, status, table/shard rows, failure reason, related logs, and related audit.
- User-visible actions that mutate configuration or operational state are reflected in audit records.

## Layout And Visual Quality

- No blank app shell, React/Vite overlay, or infinite loading spinner after data is available.
- Text is not clipped or overlapping in normal desktop viewport.
- Dense tables remain scannable; horizontal scroll is acceptable for operational detail tables.
- Drawers/modals do not hide primary actions or trap the user unexpectedly.
- Toasts are visible but do not obscure form completion.

## Browser Health

- Console has no relevant uncaught exceptions.
- Network log has no unexpected 4xx/5xx responses except deliberate negative-path checks.
- The app does not repeatedly retry a failing request without useful feedback.

## Artifact Notes

For each screenshot, record:

- route or URL
- scenario step
- what a human should notice
- pass/warn/fail semantic status

Warnings are acceptable when the workflow succeeds but the UX could mislead a real operator.
