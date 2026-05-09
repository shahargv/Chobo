# ChoboCli Architecture

ChoboCli uses a small registration-based command architecture.

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

## Adding A Command

1. Add or open a subject class in `Commands/`.
2. Register the verb in the constructor.
3. Implement a private `VerbNameAsync(CommandContext context)` method.
4. Register a new subject in `Program.cs` only if the subject itself is new.
5. Update `COMMANDS.md` with a sample.

