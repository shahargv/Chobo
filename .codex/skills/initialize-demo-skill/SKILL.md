---
name: initialize-demo-skill
description: Initialize and verify a full local Chobo demo environment. Use when Codex is asked to start or prepare a Chobo demo with Docker Compose, a 2-shard x 2-replica ClickHouse cluster, Replicated database engine, ReplicatedMergeTree local_ tables, Distributed dist_ tables, 5 GB OnTime-like demo tables, MinIO S3 backup storage, Chobo Web GUI, published ports, CLI-registered Chobo cluster/storage settings, and post-start connectivity/data verification through a low-freedom Docker Compose workflow.
---

# Initialize Chobo Demo Environment

Use this skill to start and verify the repository's local demo stack for Chobo.

Execution must be low-freedom and Docker Compose backed. The normal path is one Docker Compose command from the repository root. Do not generate ad hoc Docker Compose files, ClickHouse DDL, ClickHouse seed commands, MinIO commands, or Chobo CLI commands in the chat unless you are editing the demo implementation itself. The Compose stack and `resources/demo/scripts/Initialize-DemoEnvironment.ps1` own those details.

## Operating Contract

Use `resources/demo/docker-compose.demo.local.yml` as the skill/local-development entry point. It builds from the current checkout and keeps the initializer script bundled in a local image:

```bash
docker compose -f resources/demo/docker-compose.demo.local.yml up -d --build
```

Then inspect initialization progress with:

```bash
docker compose -f resources/demo/docker-compose.demo.local.yml logs -f demo-init
```

Do not ask the LLM to manually compose these operations:

- Docker Compose service definitions.
- ClickHouse Keeper, cluster, or macro XML.
- `CREATE DATABASE`, `CREATE TABLE`, seed `INSERT`, or verification SQL.
- MinIO bucket creation.
- `ChoboCli targets add-s3`, `clusters add`, or `test-connection` commands.
- Published port wiring or access-summary formatting.

If behavior needs to change, edit `resources/demo/docker-compose.demo.local.yml`, `resources/demo/**`, or `resources/demo/scripts/Initialize-DemoEnvironment.ps1` rather than giving a one-off command sequence. For larger datasets, run Compose with `CHOBO_DEMO_BIG_TABLE_TARGET_GB=<gb>` set before startup.

## Demo Stack

The demo stack includes:

- ChoboServer and Web GUI exposed at `http://localhost:18080`.
- Chobo API reachable from the developer machine on the same published port.
- MinIO S3 API at `http://localhost:19000` and Console at `http://localhost:19001`.
- Four ClickHouse instances exposed on published HTTP and native TCP ports.
- One ClickHouse Keeper instance for replicated metadata.
- A 2-shard x 2-replica ClickHouse cluster named `chobo_demo_cluster`.
- A `demo` database created with the ClickHouse `Replicated` database engine.
- Two large replicated local tables with `local_` prefixes and matching distributed tables with `dist_` prefixes, about 5 GB each by default, configurable with `CHOBO_DEMO_BIG_TABLE_TARGET_GB`.
- Ten additional small replicated local tables and matching distributed tables.
- Chobo storage target and ClickHouse cluster registered through `ChoboCli` from the `demo-init` container.
- Chobo-to-MinIO and Chobo-to-ClickHouse connectivity verified through the CLI.
- Data presence verified in each large `local_` table on every ClickHouse node, and distributed table accessibility verified.

## Verification Contract

The initializer container must verify all of the following before reporting success:

- Chobo Web GUI is reachable through the published localhost port.
- Chobo API is reachable through `/api/v1/server/version` using the demo access token.
- MinIO Console and S3 health endpoint are reachable through published ports.
- Chobo can access the registered MinIO S3 target using `ChoboCli targets test-connection`.
- Chobo can access the registered ClickHouse cluster using `ChoboCli clusters test-connection`.
- Each ClickHouse node has the large local tables `demo.local_ontime` and `demo.local_ontime_events`.
- Each large local table has rows on every ClickHouse node.
- Each ClickHouse node has the matching distributed tables `demo.dist_ontime` and `demo.dist_ontime_events`.
- The distributed tables are queryable and have rows.

## Output

To create larger demo datasets, set `CHOBO_DEMO_BIG_TABLE_TARGET_GB` before first startup, for example:

```bash
CHOBO_DEMO_BIG_TABLE_TARGET_GB=100 docker compose -f resources/demo/docker-compose.demo.local.yml up -d --build
```

The initializer writes deterministic artifacts under `.artifacts/demo`:

- `schema.sql`: generated ClickHouse database/table DDL.
- `demo-env.json`: final machine-readable access and verification summary.

Return the final summary to the user, including access details and verification results. Do not provide a manual fallback command sequence. If the Compose workflow is wrong or incomplete, fix the Compose file or initializer script and rerun it.