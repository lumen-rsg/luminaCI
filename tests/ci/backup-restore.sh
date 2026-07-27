#!/usr/bin/env bash

set -euo pipefail

REPOSITORY_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
readonly REPOSITORY_ROOT
readonly COMPOSE_FILE="${REPOSITORY_ROOT}/deploy/docker-compose.yml"
readonly PROJECT_NAME="lumina-ci-backup-restore"

temporary_directory="$(mktemp -d)"
readonly temporary_directory
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

archive_volume() {
    local container_name="$1"
    local destination="$2"
    local archive_name="$3"
    local volume_name
    volume_name="$(
        docker inspect "$container_name" |
            jq -er --arg destination "$destination" \
                '.[0].Mounts[] | select(.Destination == $destination) | .Name'
    )"
    docker run --rm \
        --security-opt label=disable \
        --volume "${volume_name}:/state:ro" \
        --volume "${temporary_directory}:/backup" \
        alpine:3.22 \
        tar -C /state -czf "/backup/${archive_name}" .
}

restore_volume() {
    local container_name="$1"
    local destination="$2"
    local archive_name="$3"
    local volume_name
    volume_name="$(
        docker inspect "$container_name" |
            jq -er --arg destination "$destination" \
                '.[0].Mounts[] | select(.Destination == $destination) | .Name'
    )"
    docker run --rm \
        --security-opt label=disable \
        --volume "${volume_name}:/state" \
        --volume "${temporary_directory}:/backup:ro" \
        alpine:3.22 \
        sh -euc 'find /state -mindepth 1 -delete; tar -C /state -xzf "/backup/$1"' \
        restore "$archive_name"
}

compose up --detach --wait postgres rabbitmq minio

docker exec lumina-postgres psql \
    --username lumina \
    --dbname lumina_ci \
    --command "CREATE TABLE ci_restore_probe (value text NOT NULL); INSERT INTO ci_restore_probe VALUES ('postgres-restored');"
docker exec lumina-postgres pg_dump \
    --username lumina \
    --format=custom \
    --clean \
    --if-exists \
    --dbname lumina_ci > "${temporary_directory}/postgres.dump"

docker exec lumina-rabbitmq rabbitmqadmin \
    --username lumina \
    --password ci-rabbitmq-password \
    declare queue name=ci_restore_probe durable=true
docker exec lumina-rabbitmq rabbitmqadmin \
    --username lumina \
    --password ci-rabbitmq-password \
    publish exchange=amq.default routing_key=ci_restore_probe \
    payload=rabbitmq-restored properties='{"delivery_mode":2}'

docker exec lumina-minio mc alias set \
    ci http://localhost:9000 lumina-ci ci-minio-password
docker exec lumina-minio mc mb --ignore-existing ci/ci-restore-probe
printf '%s' 'minio-restored' |
    docker exec --interactive lumina-minio mc pipe ci/ci-restore-probe/probe.txt

compose stop rabbitmq minio
archive_volume lumina-rabbitmq /var/lib/rabbitmq rabbitmq-data.tar.gz
archive_volume lumina-minio /data minio-data.tar.gz
compose start rabbitmq minio
compose up --detach --wait rabbitmq minio

docker exec lumina-postgres psql \
    --username lumina \
    --dbname lumina_ci \
    --command "DROP TABLE ci_restore_probe;"
docker exec lumina-rabbitmq rabbitmqadmin \
    --username lumina \
    --password ci-rabbitmq-password \
    delete queue name=ci_restore_probe
docker exec lumina-minio mc rm --recursive --force ci/ci-restore-probe

docker exec lumina-postgres dropdb \
    --username lumina \
    --force \
    lumina_ci
docker exec lumina-postgres createdb \
    --username lumina \
    lumina_ci
docker exec --interactive lumina-postgres pg_restore \
    --username lumina \
    --dbname lumina_ci < "${temporary_directory}/postgres.dump"
compose stop rabbitmq minio
restore_volume lumina-rabbitmq /var/lib/rabbitmq rabbitmq-data.tar.gz
restore_volume lumina-minio /data minio-data.tar.gz
compose start rabbitmq minio
compose up --detach --wait postgres rabbitmq minio

postgres_value="$(
    docker exec lumina-postgres psql \
        --username lumina \
        --dbname lumina_ci \
        --tuples-only \
        --no-align \
        --command "SELECT value FROM ci_restore_probe LIMIT 1;"
)"
[[ "$postgres_value" == "postgres-restored" ]]

rabbitmq_message="$(
    docker exec lumina-rabbitmq rabbitmqadmin \
        --username lumina \
        --password ci-rabbitmq-password \
        get queue=ci_restore_probe ackmode=ack_requeue_false count=1
)"
grep -q 'rabbitmq-restored' <<<"$rabbitmq_message"

docker exec lumina-minio mc alias set \
    ci http://localhost:9000 lumina-ci ci-minio-password
minio_value="$(docker exec lumina-minio mc cat ci/ci-restore-probe/probe.txt)"
[[ "$minio_value" == "minio-restored" ]]

printf '%s\n' "PostgreSQL, RabbitMQ, and MinIO backup restoration passed."
