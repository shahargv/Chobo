---
name: chobo-technical-writing-screenshot-taker
description: Capture documentation-ready screenshots of ChoboWeb GUI flows. Use when writing or updating Web GUI documentation, tutorials, release notes, or docs that need screenshots after opening an existing ChoboWeb instance or starting a local ChoboWeb environment with ClickHouse and S3 dependencies, navigating to a requested screen or completing a requested user flow, and saving screenshots to a user-specified output folder.
---

# Chobo Technical Writing Screenshot Taker

Capture polished, reproducible ChoboWeb screenshots for documentation. Treat this as a documentation workflow, not a broad QA pass: open or start ChoboWeb, navigate the requested GUI flow, put the screen in a clean representative state, and save the requested screenshots into the caller's output folder.

## Inputs

Require or infer these before starting:

- `outputFolder`: destination folder for final screenshots. Create it if missing.
- `flow`: the screen or user journey to capture, for example bootstrap, storage target setup, policy selector preview, schedule editor, backup details, restore wizard, logs, or audit.
- `shotList`: specific screenshots requested by the user. If omitted, capture the major meaningful states of the flow.
- `baseUrl`: optional URL for an already-running ChoboWeb instance.
- `scenario`: local environment shape when `baseUrl` is not supplied. Default to `full`; use narrower scenarios when sufficient.

If `outputFolder` is missing and cannot be inferred from the docs task, ask one concise question before launching the environment.

## Choose The Environment

Use an existing GUI when the user provides `baseUrl` or explicitly says ChoboWeb is already running. Verify the page loads before starting any local containers.

Start a local GUI when no usable `baseUrl` is provided, or when the requested screenshots need a known seeded environment. Prefer the existing Chobo UI scripts; they create all relevant dependencies for documentation flows:

- ChoboServer built from the current repo.
- ChoboWeb served through the published ChoboServer URL.
- MinIO S3-compatible storage, with bucket `data-bucket` and alias `backup-s3`.
- Source and restore ClickHouse containers, reachable as `clickhouse-source:9000` and `clickhouse-restore:9000` from ChoboServer.
- Seed data in `backup_single_source.source_orders` on the source ClickHouse container.

From the repository root:

```powershell
$envInfo = .\.codex\skills\chobo-ui-tests\scripts\start-ui-env.ps1 -Scenario full -KeepEnvironment
$envInfo.BaseUrl
$envInfo.EnvFile
```

Use `-Scenario bootstrap`, `cluster`, `storage`, `policy`, `schedule-edit`, `backup`, `restore`, `details`, or `logs-audit` when that narrower environment is enough. Use `-TestId docs-screenshots-<topic>` when a stable artifact folder name helps future inspection. The script writes artifacts under `.artifacts/TestResults/<TestId>/ui/` and returns the browser URL and env file.

Read `.codex/skills/chobo-ui-tests/references/test-data.md` before filling Chobo forms in a local environment. Use those values unless the user asks for different visible names or values. Do not use `dev-static-access-token` or `static-test-token` for bootstrap screenshots.

## Drive The GUI

Use a real browser perspective, preferably the Browser plugin or Playwright/Chrome. Navigate through ChoboWeb exactly as a user would:

- Prefer role, label, and visible-text selectors.
- Wait for network idle or stable UI state before taking each screenshot.
- Close transient toasts, autocomplete menus, debug overlays, and unrelated dialogs unless the screenshot is specifically about them.
- Use realistic but documentation-friendly names from the UI test data, or user-provided names when the docs require them.
- For destructive confirmations, capture the visible in-app dialog before confirming or cancelling. Native browser confirmations cannot appear in screenshots, so avoid using them as documentation evidence.

When a requested screenshot depends on prior data, create the data through the GUI flow where practical. Use backend/API setup only for boring prerequisites that are not part of the documented journey, and mention that shortcut in the final note.

## Browser Automation Notes

If the Browser plugin is unavailable or the Node REPL browser route fails, use local Playwright from PowerShell. The Codex desktop runtime may need both normal and pnpm `node_modules` paths in `NODE_PATH`:

```powershell
$env:NODE_PATH = 'C:\Users\shaha\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\node_modules;C:\Users\shaha\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\node_modules\.pnpm\node_modules'
```

If Playwright reports that its bundled browser executable is missing, do not download browsers unless the user agrees. Prefer an installed Chrome or Edge executable:

```powershell
Get-ChildItem -Path 'C:\Program Files\Google\Chrome\Application\chrome.exe','C:\Program Files (x86)\Google\Chrome\Application\chrome.exe','C:\Program Files\Microsoft\Edge\Application\msedge.exe','C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe' -ErrorAction SilentlyContinue
```

Launch Playwright with `executablePath` set to the discovered browser. A minimal capture script can be piped to the bundled Node executable after setting `NODE_PATH`; save the screenshot and `index.md` directly into `outputFolder`.

## Screenshot Standards

Save final images directly under `outputFolder` unless the user asks for a nested structure. Use deterministic, documentation-friendly names:

```text
001-bootstrap-install.png
002-storage-target-form.png
003-policy-selector-preview.png
```

Capture PNG files. Prefer full-page screenshots for documentation pages, and viewport screenshots for focused dialogs or forms where full-page capture adds noise. Before saving, ensure:

- The screenshot shows the actual ChoboWeb UI, not a blank loading page.
- The intended subject is visible without scrolling in the image.
- No secrets are exposed. Mask or avoid access tokens, passwords, S3 secret keys, and local key material.
- Browser chrome, Playwright traces, terminal windows, and test harness pages are not visible.
- Text is readable at documentation scale.
- The UI is in a stable success, validation, preview, or confirmation state relevant to the docs.

If a screenshot would reveal a secret, change the UI state first, crop away the secret field, or retake after Chobo masks the value.

## Artifact Index

Create or update `index.md` in `outputFolder` with one row per screenshot:

```markdown
| File | Screen | Purpose |
| --- | --- | --- |
| `001-storage-target-form.png` | Backup storage form | Shows the required MinIO fields. |
```

Keep the purpose sentence short and documentation-oriented. Include setup shortcuts or caveats below the table only when they matter to future docs maintenance.

## Verify The Capture

After saving screenshots, verify they exist and are not blank. At minimum check file size and image dimensions; for PNGs on Windows, `System.Drawing` can sample a grid of pixels:

```powershell
Add-Type -AssemblyName System.Drawing
$bmp = [System.Drawing.Bitmap]::new('<screenshot.png>')
try {
  $colors = [System.Collections.Generic.HashSet[int]]::new()
  for ($x = 0; $x -lt $bmp.Width; $x += 60) {
    for ($y = 0; $y -lt $bmp.Height; $y += 60) {
      [void]$colors.Add($bmp.GetPixel($x, $y).ToArgb())
    }
  }
  [pscustomobject]@{ Width = $bmp.Width; Height = $bmp.Height; UniqueGridColors = $colors.Count }
}
finally { $bmp.Dispose() }
```

A single-color grid usually means a blank or failed capture; retake after waiting for the UI to finish loading.

## Cleanup

Stop only environments this workflow started. For local environments, use the env file returned by `start-ui-env.ps1`:

```powershell
.\.codex\skills\chobo-ui-tests\scripts\stop-ui-env.ps1 -EnvFile <env-file-from-start>
```

If the user explicitly wants to inspect the running UI, leave it running and report the URL and env file path. If cleanup fails due to Docker or sandbox permissions, rerun the stop script with the required escalation instead of leaving containers behind silently. Verify cleanup with `docker ps --filter label=chobo.ui-test=true --format "{{.Names}}"` when the run used the local dependency-backed environment.

## Skill Validation Notes

Run the skill creator validator when available:

```powershell
python C:\Users\shaha\.codex\skills\.system\skill-creator\scripts\quick_validate.py .codex\skills\chobo-technical-writing-screenshot-taker
```

If validation fails with `ModuleNotFoundError: No module named 'yaml'`, the Python runtime is missing PyYAML. Do a manual hygiene check instead: required YAML frontmatter, folder name matches `name`, no template placeholders, and `agents/openai.yaml` default prompt includes `$chobo-technical-writing-screenshot-taker`.

## Final Response

Report:

- The output folder.
- The screenshot files created.
- Whether an existing `baseUrl` was used or a local dependency-backed environment was started.
- Any deviations from the requested flow, setup shortcuts, masked data, browser fallback used, or environment left running.

