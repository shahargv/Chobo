# ChoboCli Architecture

ChoboCli uses a small registration-based command architecture.

## Architecture Map

ChoboCli is a thin command-line adapter over the Chobo API. It should parse user intent, call `ChoboApiClient`, and let the server own business rules, validation, auditing, and persistence.

- `Program.cs` is the composition root. It creates `CommandRegistry`, registers command subjects, wires `ChoboApiClientFactory`, `ProfileStore`, and `JsonOutputWriter`, then runs `CliApplication`.
- `Cli/` is the tiny command framework: parsing, option bags, command context, subject/verb registration, dispatch, and app lifecycle.
- `Commands/` contains one subject class per CLI area. Start here when adding or changing user-facing CLI behavior for server, users, clusters, targets, policies, schedules, dashboard, metrics, garbage collection, settings, queue, backups, schema, restores, logs, audit, import/export, or test hooks.
- `Commands/CommandHelpers.cs` holds reusable parsing/formatting helpers used by multiple subjects. Keep single-command helpers private to their subject class.
- `Infrastructure/ChoboApiClient.cs` owns HTTP calls, API version checks, JSON serialization, and typed request/response handling. All server calls should flow through it.
- `Infrastructure/ChoboApiClientFactory.cs` builds clients from profiles and command options.
- `Infrastructure/ProfileStore.cs` and `CliProfile.cs` own local server profile persistence.
- `Infrastructure/JsonOutputWriter.cs` is the output boundary. Commands should return plain objects rather than writing ad hoc JSON.
- `COMMANDS.md` is the user-facing command inventory. Update it when adding or changing commands.

## Shape

- `Program.cs` is composition only. It registers subjects and starts `CliApplication`.
- `Cli/` contains the command framework:
  - `CliSubject` groups commands by subject, such as `logs` or `clusters`.
  - each subject registers verbs in its constructor with `Verb("name", "description", MethodAsync)`.
  - `ParsedCommand` and `OptionBag` parse command-line arguments.
  - `CommandRegistry` resolves `<subject> <verb>`.
- `Commands/` contains one class per subject:
  - `LogCommands`
  - `AuditCommands`
  - `ClusterCommands`
  - etc.
- `Infrastructure/` contains cross-cutting adapters:
  - profile storage
  - API client and API-version check
  - JSON output

## Rules

- Do not add subject/verb logic to `Program.cs`.
- Do not create a giant command class. Add one subject class per CLI subject.
- Keep one method per verb. If a verb needs helpers, keep them private in the subject class or move reusable behavior to `CommandHelpers`.
- All server calls must go through `ChoboApiClient` so API version checks and JSON handling stay consistent.
- Subject commands should return plain objects. `JsonOutputWriter` owns rendering.
- Keep CLI commands fast: avoid extra API calls beyond the shared server-version check and the command’s actual operation.
- CLI request/response behavior should mirror `Chobo.Contracts` and server routes. If a contract shape changes, update `ChoboApiClient`, affected command subjects, and `COMMANDS.md` together.
- Install, profile selection, server URL resolution, and shared API-version checking live in `CliApplication`, `ProfileStore`, `ChoboApiClientFactory`, and `ChoboApiClient`; check those before adding per-command setup logic.

## Adding A Command

1. Add or open a subject class in `Commands/`.
2. Register the verb in the constructor.
3. Implement a private `VerbNameAsync(CommandContext context)` method.
4. Register a new subject in `Program.cs` only if the subject itself is new.
5. Update `COMMANDS.md` with a sample.

