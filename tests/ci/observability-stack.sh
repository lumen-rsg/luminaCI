#!/usr/bin/env bash

set -euo pipefail

REPOSITORY_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
readonly REPOSITORY_ROOT
readonly BASE_COMPOSE="${REPOSITORY_ROOT}/deploy/docker-compose.yml"
readonly OBSERVABILITY_COMPOSE="${REPOSITORY_ROOT}/deploy/docker-compose.observability.yml"
readonly OBSERVABILITY_DIR="${REPOSITORY_ROOT}/deploy/observability"
readonly PROJECT_NAME="lumina-observability-${RANDOM}-$$"
readonly GRAFANA_PORT="$((30000 + ($$ % 20000)))"
readonly GRAFANA_ADMIN_PASSWORD="ci-observability-password"

temporary_directory="$(mktemp -d)"
started=false
cleanup() {
    if [[ "$started" == true ]]; then
        docker compose \
            --project-name "$PROJECT_NAME" \
            --file "$BASE_COMPOSE" \
            --file "$OBSERVABILITY_COMPOSE" \
            down --volumes --remove-orphans >/dev/null 2>&1 || true
    fi
    rm -rf "$temporary_directory"
}
trap cleanup EXIT

for command_name in docker jq curl; do
    command -v "$command_name" >/dev/null 2>&1 ||
        { printf 'Required command is missing: %s\n' "$command_name" >&2; exit 2; }
done

printf '%s\n' 'ci-only-passphrase' > "${temporary_directory}/gpg-passphrase"

export POSTGRES_PASSWORD=ci-postgres-password
export RABBITMQ_PASSWORD=ci-rabbitmq-password
export MINIO_USER=lumina-ci
export MINIO_PASSWORD=ci-minio-password
export JWT_SECRET=ci-jwt-secret-that-is-at-least-thirty-two-characters
export ADMIN_PASSWORD=ci-admin-password
export SECRETS_MASTER_KEY=ci-master-key-that-is-at-least-thirty-two-characters
export GPG_PASSPHRASE_FILE="${temporary_directory}/gpg-passphrase"
export GRAFANA_ADMIN_PASSWORD
export GRAFANA_PORT

compose=(
    docker compose
    --project-name "$PROJECT_NAME"
    --file "$BASE_COMPOSE"
    --file "$OBSERVABILITY_COMPOSE"
)

"${compose[@]}" config --format json > "${temporary_directory}/compose.json"
jq -e '
    .services as $services
    | ($services.grafana.ports | length == 1)
      and ($services.grafana.ports[0].host_ip == "127.0.0.1")
      and (($services["otel-collector"].ports // []) | length == 0)
      and (($services.prometheus.ports // []) | length == 0)
      and (($services.tempo.ports // []) | length == 0)
      and ([
        "api-gateway",
        "build-service",
        "security-service",
        "scanner-service",
        "repository-service",
        "source-service"
      ] | all(. as $name |
        $services[$name].environment.OTEL_EXPORTER_OTLP_ENDPOINT ==
          "http://otel-collector:4317"))
' "${temporary_directory}/compose.json" >/dev/null

docker run --rm \
    --volume "${OBSERVABILITY_DIR}/otel-collector.yml:/etc/otelcol-contrib/config.yaml:ro" \
    ghcr.io/open-telemetry/opentelemetry-collector-releases/opentelemetry-collector-contrib:0.157.0@sha256:f2f01157055a9b2aab9df7118e1f1c9abf345e99b23bc7a2bc791db374a7d0f6 \
    validate --config=/etc/otelcol-contrib/config.yaml

docker run --rm \
    --entrypoint /bin/promtool \
    --volume "${OBSERVABILITY_DIR}:/etc/prometheus:ro" \
    prom/prometheus:v3.13.0@sha256:c6b27ea434f8389bfe233fbc7be381cf50587c286e871bc842008f5a1b1908a7 \
    check config /etc/prometheus/prometheus.yml

docker run --rm \
    --volume "${OBSERVABILITY_DIR}/tempo.yml:/etc/tempo/tempo.yml:ro" \
    grafana/tempo:2.10.5@sha256:ee21727732c7a7199cb71c3eee9153bbf23f9b0b87619f0555a0cf21a67f1a33 \
    -config.file=/etc/tempo/tempo.yml -config.verify=true

jq -e '
    .uid == "lumina-control-plane"
    and (.panels | length >= 5)
' "${OBSERVABILITY_DIR}/grafana/dashboards/lumina-overview.json" >/dev/null

"${compose[@]}" up --detach tempo otel-collector prometheus grafana
started=true

for _ in $(seq 1 30); do
    if curl --fail --silent "http://127.0.0.1:${GRAFANA_PORT}/api/health" |
        jq -e '.database == "ok"' >/dev/null 2>&1 &&
       docker exec "${PROJECT_NAME}-grafana-1" \
        wget -qO- http://prometheus:9090/-/ready >/dev/null 2>&1 &&
       docker exec "${PROJECT_NAME}-grafana-1" \
        wget -qO- http://tempo:3200/ready >/dev/null 2>&1 &&
       docker exec "${PROJECT_NAME}-grafana-1" \
        wget -qO- http://otel-collector:13133/ >/dev/null 2>&1; then
        break
    fi
    sleep 2
done

curl --fail --silent \
    --user "admin:${GRAFANA_ADMIN_PASSWORD}" \
    "http://127.0.0.1:${GRAFANA_PORT}/api/dashboards/uid/lumina-control-plane" |
    jq -e '.dashboard.uid == "lumina-control-plane"' >/dev/null

docker exec "${PROJECT_NAME}-grafana-1" \
    wget -qO- 'http://prometheus:9090/api/v1/targets?state=active' |
    jq -e '
        .status == "success"
        and ([.data.activeTargets[] | select(.health != "up")] | length == 0)
    ' >/dev/null

printf 'Observability stack validation passed.\n'
