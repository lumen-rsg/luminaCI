#!/usr/bin/env bash

set -euo pipefail

REPOSITORY_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
readonly REPOSITORY_ROOT
readonly COMPOSE_FILE="${REPOSITORY_ROOT}/deploy/docker-compose.yml"
readonly PROJECT_NAME="lumina-ci-integration"

temporary_directory="$(mktemp -d)"
secret_file="${temporary_directory}/gpg-passphrase"
environment_file="${temporary_directory}/compose.env"

cleanup() {
    docker compose \
        --project-name "$PROJECT_NAME" \
        --env-file "$environment_file" \
        --file "$COMPOSE_FILE" \
        down --volumes --remove-orphans >/dev/null 2>&1 || true
    rm -rf "$temporary_directory"
}
trap cleanup EXIT

printf '%s\n' 'ci-only-passphrase' > "$secret_file"
cat > "$environment_file" <<EOF
POSTGRES_PASSWORD=ci-postgres-password
RABBITMQ_PASSWORD=ci-rabbitmq-password
MINIO_USER=lumina-ci
MINIO_PASSWORD=ci-minio-password
JWT_SECRET=ci-jwt-secret-that-is-at-least-thirty-two-characters
ADMIN_PASSWORD=ci-admin-password
SECRETS_MASTER_KEY=ci-master-key-that-is-at-least-thirty-two-characters
GPG_PASSPHRASE_FILE=${secret_file}
EOF

compose() {
    docker compose \
        --project-name "$PROJECT_NAME" \
        --env-file "$environment_file" \
        --file "$COMPOSE_FILE" \
        "$@"
}

compose up --detach --wait postgres rabbitmq minio

docker exec lumina-postgres psql \
    --username lumina \
    --dbname lumina_ci \
    --command "CREATE TABLE ci_restart_probe (value text NOT NULL); INSERT INTO ci_restart_probe VALUES ('postgres-durable');"

docker exec lumina-rabbitmq rabbitmqadmin \
    --username lumina \
    --password ci-rabbitmq-password \
    declare queue \
    name=ci_restart_probe durable=true
docker exec lumina-rabbitmq rabbitmqadmin \
    --username lumina \
    --password ci-rabbitmq-password \
    publish \
    exchange=amq.default \
    routing_key=ci_restart_probe \
    payload=rabbitmq-durable \
    properties='{"delivery_mode":2}'

docker exec lumina-minio mc alias set \
    ci http://localhost:9000 lumina-ci ci-minio-password
docker exec lumina-minio mc mb --ignore-existing ci/ci-restart-probe
printf '%s' 'minio-durable' |
    docker exec --interactive lumina-minio mc pipe ci/ci-restart-probe/probe.txt

compose restart postgres rabbitmq minio
compose up --detach --wait postgres rabbitmq minio

postgres_value="$(docker exec lumina-postgres psql \
    --username lumina \
    --dbname lumina_ci \
    --tuples-only \
    --no-align \
    --command "SELECT value FROM ci_restart_probe LIMIT 1;")"
[[ "$postgres_value" == "postgres-durable" ]]

rabbitmq_message="$(docker exec lumina-rabbitmq rabbitmqadmin \
    --username lumina \
    --password ci-rabbitmq-password \
    get \
    queue=ci_restart_probe \
    ackmode=ack_requeue_false \
    count=1)"
grep -q 'rabbitmq-durable' <<<"$rabbitmq_message"

minio_value="$(docker exec lumina-minio mc cat ci/ci-restart-probe/probe.txt)"
[[ "$minio_value" == "minio-durable" ]]

printf '%s\n' "Postgres, RabbitMQ, and MinIO retained state across restart."
