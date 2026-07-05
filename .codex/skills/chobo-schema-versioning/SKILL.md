---
name: chobo-schema-versioning
description: Use when changing Chobo SQLite schema, EF migrations, ChoboApi.SchemaVersion, export schema compatibility, or release-bound schema upgrade policy. Guides whether to edit the baseline schema in place or create a new release migration.
---

# Chobo Schema Versioning

Chobo treats schema versions as release-level contracts, not PR-level or chat-level counters.
Release versions use `X.Y.Z`: `X` is the owner-selected major line, `Y` is the SQLite schema-change counter within that major line, and `Z` is the same-schema feature/patch counter. `ChoboApi.SchemaVersion` remains the internal monotonic SQLite compatibility integer.

## Current Baseline

- Current development schema: `ChoboApi.SchemaVersion = 1`.
- Current baseline migration: `ChoboServer/Data/Migrations/000000000001_Baseline.cs`.
- Latest published release at the time this policy was added: `v0.1.0`.
- Until a release is explicitly declared as carrying schema v1, ongoing schema work for that release should modify the v1 baseline in place.

## Workflow

1. Identify whether the requested change targets the current unreleased schema or an already published schema.
2. Verify published release context when it matters:
   - Check local tags with `git tag --sort=-creatordate`.
   - Prefer GitHub release metadata for authoritative published status when available.
   - Check `Chobo.Contracts/ChoboApi.cs` for the current working `SchemaVersion`.
3. If the current schema version has not been published, edit these in place:
   - entity classes under `ChoboServer/Data`;
   - `ChoboDbContext` model configuration;
   - `000000000001_Baseline.cs`;
   - tests and fixtures.
4. If the schema version has been published, do not rewrite its baseline. Add the next schema version:
   - increment `ChoboApi.SchemaVersion`;
   - add a new migration file named for the target schema version, not the PR date;
   - add a custom upgrade step in `SchemaUpgradeService`;
   - add upgrade tests from the last published schema to the new schema.
5. Before release, run `.\scripts\Test-ReleaseVersionPolicy.ps1 -Version <version>` and review its schema advisory. It checks schema-sensitive git diffs since the previous release tag and warns when a missed schema bump is likely or ambiguous.
6. Keep `ExportVersion` separate. Only change it for serialized export envelope compatibility changes, not every SQLite schema change.

## Rules Of Thumb

- Do not add migration files just because a PR changes an entity during the same unreleased schema cycle.
- Do not bump `SchemaVersion` until we intentionally start compatibility work for a release after the last published schema.
- When `ChoboApi.SchemaVersion` increases on the same major line, release minor `Y` must increase by one and patch `Z` resets to zero.
- When `ChoboApi.SchemaVersion` does not increase on the same major line, release minor `Y` must stay the same and patch `Z` increases.
- When release major `X` increases, release minor `Y` resets to zero.
- When collapsing or resetting pre-release schema history, keep exactly one baseline migration matching the current `DbContext`.
- If unsure whether a schema is published, stop and verify instead of guessing.

## Export envelope compatibility

Export envelopes intentionally exclude audit entries and application logs. Data envelopes include restorable operational backup/restore metadata. Change ChoboApi.ExportVersion only when serialized envelope compatibility changes, and keep imported credentials empty unless a future explicit re-encryption workflow is designed.

