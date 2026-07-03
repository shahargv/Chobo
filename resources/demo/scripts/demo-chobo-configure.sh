#!/usr/bin/env sh
set -eu

CHOBO_URL="${CHOBO_INTERNAL_URL:-http://choboserver:8080}"
TOKEN="${CHOBO_ACCESS_TOKEN:-demo-static-access-token}"
MINIO_ENDPOINT="${MINIO_INTERNAL_ENDPOINT:-http://minio:9000}"
BUCKET="${MINIO_BUCKET:-data-bucket}"
ACCESS_KEY="${MINIO_ACCESS_KEY:-chobo-access-key}"
SECRET_KEY="${MINIO_SECRET_KEY:-chobo-secret-key}"
OUT_DIR="${CHOBO_DEMO_OUTPUT_DIR:-/demo-output}"
CLI='dotnet /app/ChoboCli.dll'
mkdir -p "$OUT_DIR"

wait_for_file() {
  file="$1"
  name="$2"
  remaining=600
  until [ -f "$file" ]; do
    if [ "$remaining" -le 0 ]; then
      echo "Timed out waiting for $name." >&2
      exit 1
    fi
    echo "Waiting for $name..."
    remaining=$((remaining - 2))
    sleep 2
  done
}

wait_for_file "$OUT_DIR/minio-init.done" 'MinIO initialization'
wait_for_file "$OUT_DIR/clickhouse-init.done" 'ClickHouse demo data initialization'
until $CLI users list --server-url "$CHOBO_URL" --access-token "$TOKEN" >/dev/null 2>&1; do
  echo 'Waiting for Chobo API...'
  sleep 2
done

echo 'Chobo API is ready; waiting 5 seconds for startup work to settle...'
sleep 5

json_id() {
  sed -n 's/.*"id"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -n 1
}

json_id_by_name() {
  awk -v wanted="$1" '
    /"id"[[:space:]]*:/ {
      id=$0
      sub(/^.*"id"[[:space:]]*:[[:space:]]*"/, "", id)
      sub(/".*$/, "", id)
    }
    /"name"[[:space:]]*:/ {
      name=$0
      sub(/^.*"name"[[:space:]]*:[[:space:]]*"/, "", name)
      sub(/".*$/, "", name)
      if (name == wanted && id != "") { print id; exit }
    }
  '
}

target_id="$($CLI targets list --server-url "$CHOBO_URL" --access-token "$TOKEN" | json_id_by_name demo-minio || true)"
if [ -z "$target_id" ]; then
  target_id="$($CLI targets add-s3 --name demo-minio --endpoint "$MINIO_ENDPOINT" --bucket "$BUCKET" --access-key "$ACCESS_KEY" --secret-key "$SECRET_KEY" --force-path-style --server-url "$CHOBO_URL" --access-token "$TOKEN" | json_id)"
fi

cluster_id="$($CLI clusters list --server-url "$CHOBO_URL" --access-token "$TOKEN" | json_id_by_name demo-clickhouse-cluster || true)"
if [ -z "$cluster_id" ]; then
  cluster_id="$($CLI clusters add --name demo-clickhouse-cluster --mode Cluster --node clickhouse-s1-r1:9000 --clickhouse-cluster-name chobo_demo_cluster --backup-restore-maxdop 2 --server-url "$CHOBO_URL" --access-token "$TOKEN" | json_id)"
fi

$CLI targets test-connection --id "$target_id" --server-url "$CHOBO_URL" --access-token "$TOKEN" >/dev/null
$CLI clusters test-connection --id "$cluster_id" --server-url "$CHOBO_URL" --access-token "$TOKEN" >/dev/null

cat > "$OUT_DIR/demo-env.json" <<JSON
{
  "chobo": {
    "webUrl": "http://localhost:18080",
    "apiUrl": "http://localhost:18080",
    "accessToken": "$TOKEN"
  },
  "minio": {
    "consoleUrl": "http://localhost:19001",
    "accessKey": "$ACCESS_KEY",
    "secretKey": "$SECRET_KEY",
    "bucket": "$BUCKET"
  },
  "choboRegistration": {
    "storageTargetId": "$target_id",
    "clusterId": "$cluster_id"
  }
}
JSON

echo 'Chobo demo configuration succeeded.'
cat "$OUT_DIR/demo-env.json"