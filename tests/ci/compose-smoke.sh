#!/usr/bin/env bash

set -euo pipefail

REPOSITORY_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
readonly REPOSITORY_ROOT
readonly COMPOSE_FILE="${REPOSITORY_ROOT}/deploy/docker-compose.yml"
readonly OVERRIDE_FILE="${REPOSITORY_ROOT}/tests/ci/docker-compose.smoke.yml"
readonly PROJECT_NAME="lumina-ci-smoke"
CALLER_UID="$(id -u)"
readonly CALLER_UID
CALLER_GID="$(id -g)"
readonly CALLER_GID

temporary_directory="$(mktemp -d)"
readonly temporary_directory
secret_file="${temporary_directory}/gpg-passphrase"
environment_file="${temporary_directory}/compose.env"
export LUMINA_CI_DATA="${temporary_directory}/data"
export LUMINA_CI_CERTS="${temporary_directory}/certs"

mkdir -p \
    "$LUMINA_CI_CERTS" \
    "${LUMINA_CI_DATA}/builds" \
    "${LUMINA_CI_DATA}/sources" \
    "${LUMINA_CI_DATA}/extra-sources"

cleanup() {
    exit_status=$?
    if (( exit_status != 0 )); then
        compose ps --all || true
        compose logs --no-color --tail 200 || true
    fi
    compose down --volumes --remove-orphans >/dev/null 2>&1 || true
    docker run --rm \
        --security-opt label=disable \
        --volume "${temporary_directory}:/cleanup" \
        alpine:3.22 \
        chown -R "${CALLER_UID}:${CALLER_GID}" /cleanup >/dev/null 2>&1 || true
    rm -rf "$temporary_directory"
    exit "$exit_status"
}
trap cleanup EXIT

printf '%s\n' 'ci-only-passphrase' > "$secret_file"
chmod 0600 "$secret_file"
openssl req \
    -x509 \
    -newkey rsa:2048 \
    -nodes \
    -days 1 \
    -keyout "${LUMINA_CI_CERTS}/console.key" \
    -out "${LUMINA_CI_CERTS}/console.crt" \
    -subj "/CN=localhost" >/dev/null 2>&1
chmod 0400 "${LUMINA_CI_CERTS}/console.key"

cat > "$environment_file" <<EOF
POSTGRES_PASSWORD=ci-postgres-password
RABBITMQ_PASSWORD=ci-rabbitmq-password
MINIO_USER=lumina-ci
MINIO_PASSWORD=ci-minio-password
JWT_SECRET=ci-jwt-secret-that-is-at-least-thirty-two-characters
JWT_COOKIE_SECURE=true
ADMIN_USERNAME=admin
ADMIN_PASSWORD=ci-admin-password
SECRETS_MASTER_KEY=ci-master-key-that-is-at-least-thirty-two-characters
GPG_PASSPHRASE_FILE=${secret_file}
EOF

compose() {
    docker compose \
        --project-name "$PROJECT_NAME" \
        --env-file "$environment_file" \
        --file "$COMPOSE_FILE" \
        --file "$OVERRIDE_FILE" \
        "$@"
}

compose up --detach --build --wait --wait-timeout 900

BASE_URL=https://localhost \
CHECK_CONTAINERS=1 \
CURL_INSECURE=1 \
SMOKE_USERNAME=admin \
SMOKE_PASSWORD=ci-admin-password \
    "${REPOSITORY_ROOT}/scripts/smoke-test.sh"

docker tag lumina-api-gateway:local lumina-api-gateway:rollback-rehearsal
docker tag lumina-build-service:local lumina-build-service:rollback-rehearsal
docker tag lumina-security-service:local lumina-security-service:rollback-rehearsal
docker tag lumina-scanner-service:local lumina-scanner-service:rollback-rehearsal
docker tag lumina-repository-service:local lumina-repository-service:rollback-rehearsal
docker tag lumina-source-service:local lumina-source-service:rollback-rehearsal
docker tag lumina-webapp:local lumina-webapp:rollback-rehearsal

cat >> "$environment_file" <<'EOF'
API_GATEWAY_IMAGE=lumina-api-gateway:rollback-rehearsal
BUILD_SERVICE_IMAGE=lumina-build-service:rollback-rehearsal
SECURITY_SERVICE_IMAGE=lumina-security-service:rollback-rehearsal
SCANNER_SERVICE_IMAGE=lumina-scanner-service:rollback-rehearsal
REPOSITORY_SERVICE_IMAGE=lumina-repository-service:rollback-rehearsal
SOURCE_SERVICE_IMAGE=lumina-source-service:rollback-rehearsal
WEBAPP_IMAGE=lumina-webapp:rollback-rehearsal
EOF

compose up \
    --detach \
    --no-build \
    --no-deps \
    --force-recreate \
    --wait \
    --wait-timeout 900 \
    api-gateway \
    build-service \
    security-service \
    scanner-service \
    repository-service \
    source-service \
    webapp \
    nginx

BASE_URL=https://localhost \
CHECK_CONTAINERS=1 \
CURL_INSECURE=1 \
SMOKE_USERNAME=admin \
SMOKE_PASSWORD=ci-admin-password \
    "${REPOSITORY_ROOT}/scripts/smoke-test.sh"

printf '%s\n' "Immutable-image rollback rehearsal passed."
