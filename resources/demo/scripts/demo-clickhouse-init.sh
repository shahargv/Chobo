#!/usr/bin/env bash
set -euo pipefail

BIG_TABLE_TARGET_GB="${CHOBO_DEMO_BIG_TABLE_TARGET_GB:-}"
BIG_TABLE_TARGET_MB="${CHOBO_DEMO_BIG_TABLE_TARGET_MB:-100}"
ROWS_PER_BATCH="${CHOBO_DEMO_ROWS_PER_BATCH:-200000}"
CLUSTER="chobo_demo_cluster"
DB="demo"
NODES=(clickhouse-s1-r1 clickhouse-s1-r2 clickhouse-s2-r1 clickhouse-s2-r2)
LOCAL_TABLES=(local_ontime local_ontime_events)
DIST_TABLES=(dist_ontime dist_ontime_events)
OUT_DIR="${CHOBO_DEMO_OUTPUT_DIR:-/demo-output}"
mkdir -p "$OUT_DIR"

ch() {
  local host="$1"
  local query="$2"
  clickhouse-client --host "$host" --multiquery --format TSVRaw --query "$query"
}

until ch clickhouse-s1-r1 'SELECT 1' >/dev/null 2>&1; do
  echo 'Waiting for ClickHouse...'
  sleep 2
done

schema_file="$OUT_DIR/schema.sql"
cat > "$schema_file" <<SQL
DROP DATABASE IF EXISTS ${DB} ON CLUSTER ${CLUSTER} SYNC;
CREATE DATABASE ${DB} ON CLUSTER ${CLUSTER} ENGINE = Replicated('/clickhouse/databases/{uuid}', '{shard}', '{replica}');

CREATE TABLE ${DB}.local_ontime
(
    flight_date Date,
    year UInt16,
    month UInt8,
    day_of_month UInt8,
    reporting_airline LowCardinality(String),
    origin LowCardinality(String),
    dest LowCardinality(String),
    flight_num UInt32,
    dep_delay Int16,
    arr_delay Int16,
    distance UInt16,
    payload String
)
ENGINE = ReplicatedMergeTree
PARTITION BY toYYYYMM(flight_date)
ORDER BY (year, month, day_of_month, reporting_airline, origin, dest, flight_num);

CREATE TABLE ${DB}.dist_ontime AS ${DB}.local_ontime
ENGINE = Distributed(${CLUSTER}, ${DB}, local_ontime, cityHash64(origin, dest, flight_num));

CREATE TABLE ${DB}.local_ontime_events
(
    event_time DateTime,
    event_date Date,
    carrier LowCardinality(String),
    airport LowCardinality(String),
    event_type LowCardinality(String),
    aircraft_id UInt32,
    route_id UInt32,
    metric_1 Float64,
    metric_2 Float64,
    payload String
)
ENGINE = ReplicatedMergeTree
PARTITION BY toYYYYMM(event_date)
ORDER BY (event_date, carrier, airport, event_type, aircraft_id);

CREATE TABLE ${DB}.dist_ontime_events AS ${DB}.local_ontime_events
ENGINE = Distributed(${CLUSTER}, ${DB}, local_ontime_events, cityHash64(carrier, airport, aircraft_id));
SQL

for i in $(seq 1 10); do
  cat >> "$schema_file" <<SQL

CREATE TABLE ${DB}.local_small_${i}
(
    id UInt64,
    tenant_id UInt32,
    name String,
    created_at DateTime,
    amount Decimal(18, 2)
)
ENGINE = ReplicatedMergeTree
ORDER BY (tenant_id, id);

CREATE TABLE ${DB}.dist_small_${i} AS ${DB}.local_small_${i}
ENGINE = Distributed(${CLUSTER}, ${DB}, local_small_${i}, cityHash64(tenant_id, id));
SQL
done

ch clickhouse-s1-r1 "$(cat "$schema_file")" >/dev/null

small_sql=''
for i in $(seq 1 10); do
  small_sql+="INSERT INTO ${DB}.dist_small_${i} SELECT number + 1, (number % 25) + 1, concat('small-${i}-', toString(number)), now() - toIntervalSecond(number % 86400), toDecimal64((number % 100000) / 100, 2) FROM numbers(10000);"
  small_sql+=$'\n'
done
ch clickhouse-s1-r1 "$small_sql" >/dev/null

if [ -n "$BIG_TABLE_TARGET_GB" ]; then
  case "$BIG_TABLE_TARGET_GB" in
    *[!0-9]*) echo "CHOBO_DEMO_BIG_TABLE_TARGET_GB must be a positive integer." >&2; exit 1 ;;
  esac
  target_bytes=$((BIG_TABLE_TARGET_GB * 1024 * 1024 * 1024))
  target_label="${BIG_TABLE_TARGET_GB} GB"
else
  case "$BIG_TABLE_TARGET_MB" in
    ''|*[!0-9]*) echo "CHOBO_DEMO_BIG_TABLE_TARGET_MB must be a positive integer." >&2; exit 1 ;;
  esac
  target_bytes=$((BIG_TABLE_TARGET_MB * 1024 * 1024))
  target_label="${BIG_TABLE_TARGET_MB} MB"
fi

target_rows=$(( (target_bytes + 511) / 512 ))
if [ "$target_rows" -lt 1 ]; then target_rows=1; fi
batches=$(( (target_rows + ROWS_PER_BATCH - 1) / ROWS_PER_BATCH ))
echo "Loading about ${target_label} into each large demo table."

for ((batch=0; batch<batches; batch++)); do
  offset=$(( batch * ROWS_PER_BATCH ))
  count=$ROWS_PER_BATCH
  remaining=$(( target_rows - offset ))
  if [ "$remaining" -lt "$count" ]; then count=$remaining; fi
  echo "Loading large table batch $((batch + 1)) of $batches ($count rows per large table)."
  ch clickhouse-s1-r1 "
INSERT INTO ${DB}.dist_ontime
SELECT
    toDate('2015-01-01') + (number % 730),
    toYear(toDate('2015-01-01') + (number % 730)),
    toMonth(toDate('2015-01-01') + (number % 730)),
    toDayOfMonth(toDate('2015-01-01') + (number % 730)),
    concat('AIR', toString(number % 32)),
    concat('O', leftPad(toString(number % 500), 3, '0')),
    concat('D', leftPad(toString((number * 7) % 500), 3, '0')),
    toUInt32(number % 100000),
    toInt16((number % 240) - 120),
    toInt16(((number * 3) % 240) - 120),
    toUInt16(100 + (number % 4500)),
    repeat(concat('ontime-demo-', toString(number), '-'), 20)
FROM numbers(${offset}, ${count});

INSERT INTO ${DB}.dist_ontime_events
SELECT
    toDateTime('2015-01-01 00:00:00') + toIntervalSecond(number % 63072000),
    toDate(toDateTime('2015-01-01 00:00:00') + toIntervalSecond(number % 63072000)),
    concat('AIR', toString(number % 32)),
    concat('A', leftPad(toString(number % 500), 3, '0')),
    concat('event_', toString(number % 20)),
    toUInt32(number % 1000000),
    toUInt32((number * 13) % 1000000),
    sin(number),
    cos(number),
    repeat(concat('event-demo-', toString(number), '-'), 20)
FROM numbers(${offset}, ${count});
" >/dev/null
done

for node in "${NODES[@]}"; do
  for table in "${LOCAL_TABLES[@]}"; do
    ch "$node" "SYSTEM SYNC REPLICA ${DB}.${table}" >/dev/null
    rows=$(ch "$node" "SELECT count() FROM ${DB}.${table}")
    if [ "$rows" -le 0 ]; then echo "${DB}.${table} has no rows on ${node}" >&2; exit 1; fi
  done
  for table in "${DIST_TABLES[@]}"; do
    rows=$(ch "$node" "SELECT count() FROM ${DB}.${table}")
    if [ "$rows" -le 0 ]; then echo "${DB}.${table} has no rows on ${node}" >&2; exit 1; fi
  done
done

touch "$OUT_DIR/clickhouse-init.done"
echo 'ClickHouse demo data initialized.'