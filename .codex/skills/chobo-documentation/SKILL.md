---
name: chobo-documentation
description: Create, reorganize, or improve Chobo documentation, especially DBA/operator user docs, README screenshots, restore/backups/policies/security guidance, docs/user vs docs/developer separation, GUI screenshot coverage, CLI examples, and documentation review passes. Use when editing docs under docs/, README.md, ChoboCli/COMMANDS.md, or when asked to make Chobo documentation clearer, more accurate, more visual, or more operator-focused.
---

# Chobo Documentation

Use this skill for Chobo documentation work. The target reader for user docs is a DBA operating ClickHouse instances or clusters who needs backups to run reliably, errors to be diagnosable, and restores to feel calm and predictable.

## Core Workflow

1. Separate audience first.
   - User/operator docs live under `docs/user/`.
   - Developer/test/release docs live under `docs/developer/`.
   - Top-level `docs/README.md` links to both sections.
   - Main `README.md` links to the most useful user docs and uses informative screenshots.

2. Inspect the product surface before writing.
   - Read relevant CLI command implementations and `ChoboCli/COMMANDS.md` before adding CLI examples.
   - Read contracts/DTOs before inventing JSON sample output.
   - Read web components or run the UI before describing GUI behavior.
   - Keep internal development/test-hook behavior out of user docs unless the page is explicitly developer-focused.

3. Write user docs for DBA operations.
   - Cover installation, first setup, ClickHouse clusters, S3 storage, policies, schedules, backups, restores, users/tokens, logs, audit, schema browser, security, troubleshooting, lifecycle, and metadata recovery.
   - Explain both GUI and CLI paths when both exist.
   - Prefer plain, concise language with enough context to make operational choices clear.
   - For restore docs, assume the reader is stressed. Make the safest path obvious, especially scratch restores and validation before production changes.

4. Make screenshots informative.
   - Use screenshots that show real, populated, successful GUI states.
   - Avoid empty tables, first-combo-box defaults, failure states, or ambiguous screenshots unless the section is explicitly about failures.
   - When documenting a GUI feature, show the actual control: selector builder, restore layout cards, table mappings, confirmation dialog, history/progress, and details pages.
   - If suitable screenshots do not exist, use the `chobo-ui-tests` skill to run/capture real GUI flows.

5. Validate and review.
   - Run local Markdown link checks after moving files or adding images.
   - Verify screenshots exist and are valid image files.
   - For large documentation changes, use reviewer subagents for language, technical accuracy, and missing coverage, then apply their recommendations.

## Detailed Standards

Read `references/documentation-standards.md` when making a non-trivial documentation change, adding screenshots, touching restore/policy/backup docs, or reorganizing documentation.