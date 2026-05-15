---
name: chobo-schema-versioning
description: Use when changing Chobo SQLite schema, EF migrations, ChoboApi.SchemaVersion, export schema compatibility, or release-bound schema upgrade policy. Guides whether to edit the baseline schema in place or create a new release migration.
---

# Chobo Schema Versioning

Chobo treats schema versions as release-level contracts, not PR-level or chat-level counters.

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
5. Keep `ExportVersion` separate. Only change it for serialized export envelope compatibility changes, not every SQLite schema change.

## Rules Of Thumb

- Do not add migration files just because a PR changes an entity during the same unreleased schema cycle.
- Do not bump `SchemaVersion` until we intentionally start compatibility work for a release after the last published schema.
- When collapsing or resetting pre-release schema history, keep exactly one baseline migration matching the current `DbContext`.
- If unsure whether a schema is published, stop and verify instead of guessing.
