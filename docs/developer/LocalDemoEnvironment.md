# Local Demo Environment

The repository includes two Docker Compose demo paths. `resources/demo/docker-compose.demo.yml` uses official Docker Hub Chobo images for the README quickstart. `resources/demo/docker-compose.demo.local.yml` builds ChoboServer and the initializer from the local checkout for development. The host only needs Docker with Compose support.

## Start

```bash
git clone https://github.com/shahargv/Chobo.git
cd Chobo
docker compose -f resources/demo/docker-compose.demo.yml up -d
docker compose -f resources/demo/docker-compose.demo.yml logs -f clickhouse-init demo-init
```

For an existing checkout:

```bash
git checkout master
git pull
docker compose -f resources/demo/docker-compose.demo.yml up -d
docker compose -f resources/demo/docker-compose.demo.yml logs -f clickhouse-init demo-init
```

Watch initialization:

```bash
docker compose -f resources/demo/docker-compose.demo.yml logs -f clickhouse-init demo-init
```

For the Docker Hub demo, `clickhouse-init` loads the sample data and `demo-init` registers Chobo storage and cluster settings. Initialization is complete when `demo-init` prints `Chobo demo configuration succeeded.` Open `http://localhost:18080` after initialization completes so the demo storage and cluster are already visible. The access and registration summary is written to `.artifacts/demo/demo-env.json`.

## Access

```text
Chobo Web/API:   http://localhost:18080
Access token:    demo-static-access-token
MinIO Console:   http://localhost:19001
MinIO username:  chobo-access-key
MinIO password:  chobo-secret-key
```

ClickHouse node ports:

```text
Shard 1 replica 1: HTTP http://localhost:18111, native TCP localhost:19111, Play http://localhost:8153/play
Shard 1 replica 2: HTTP http://localhost:18112, native TCP localhost:19112
Shard 2 replica 1: HTTP http://localhost:18121, native TCP localhost:19121
Shard 2 replica 2: HTTP http://localhost:18122, native TCP localhost:19122
```


## Local Source-Build Demo

Use the local Compose file when you want to test changes from the current checkout instead of Docker Hub images:

```bash
docker compose -f resources/demo/docker-compose.demo.local.yml up -d --build
```

Watch initialization:

```bash
docker compose -f resources/demo/docker-compose.demo.local.yml logs -f demo-init
```
## CLI

The Docker Hub demo keeps a CLI container running for quick commands:

```bash
docker exec chobo-demo-cli dotnet /app/ChoboCli.dll clusters list --server-url http://choboserver:8080 --access-token demo-static-access-token
```
## Dataset Size

The Docker Hub README demo creates two replicated local tables of about 100 MB each by default, plus matching distributed tables and ten smaller tables. The local source-build demo defaults to about 5 GB per large table.

To create a different quickstart size, set `CHOBO_DEMO_BIG_TABLE_TARGET_MB` before the first startup:

```bash
CHOBO_DEMO_BIG_TABLE_TARGET_MB=250 docker compose -f resources/demo/docker-compose.demo.yml up -d
docker compose -f resources/demo/docker-compose.demo.yml logs -f clickhouse-init demo-init
```

To create larger large-table targets, set `CHOBO_DEMO_BIG_TABLE_TARGET_GB` instead:

```bash
CHOBO_DEMO_BIG_TABLE_TARGET_GB=100 docker compose -f resources/demo/docker-compose.demo.yml up -d
docker compose -f resources/demo/docker-compose.demo.yml logs -f clickhouse-init demo-init
```

Use a clean environment when changing dataset size:

```bash
docker compose -f resources/demo/docker-compose.demo.yml down -v --remove-orphans
rm -rf .artifacts/demo
CHOBO_DEMO_BIG_TABLE_TARGET_MB=250 docker compose -f resources/demo/docker-compose.demo.yml up -d
docker compose -f resources/demo/docker-compose.demo.yml logs -f clickhouse-init demo-init
```
## What It Verifies

The public demo initialization services:

- create the MinIO bucket;
- create the ClickHouse `demo` database with the `Replicated` database engine;
- create `local_` `ReplicatedMergeTree` tables and matching `dist_` distributed tables;
- load the demo data;
- register the MinIO target and ClickHouse cluster in Chobo;
- verify the Chobo Web/API published port after initialization completes;
- verify Chobo can connect to MinIO and ClickHouse;
- verify data exists in the large local tables on every ClickHouse node;
- verify the distributed tables are queryable.

## Remove

```bash
docker compose -f resources/demo/docker-compose.demo.yml down -v --remove-orphans
rm -rf .artifacts/demo
```