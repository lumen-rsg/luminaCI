#!/usr/bin/env bash

set -euo pipefail

REPOSITORY_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
readonly REPOSITORY_ROOT
readonly COMPOSE_FILE="${REPOSITORY_ROOT}/deploy/docker-compose.yml"

temporary_directory="$(mktemp -d)"
cleanup() {
    rm -rf "$temporary_directory"
}
trap cleanup EXIT

secret_file="${temporary_directory}/gpg-passphrase"
environment_file="${temporary_directory}/compose.env"
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

docker compose \
    --env-file "$environment_file" \
    --file "$COMPOSE_FILE" \
    config --quiet

docker compose \
    --file "${REPOSITORY_ROOT}/deploy/docker-compose.linux.yml" \
    config --quiet

for dockerfile in "${REPOSITORY_ROOT}"/deploy/docker/*.Dockerfile; do
    docker buildx build \
        --check \
        --file "$dockerfile" \
        "$REPOSITORY_ROOT"
done
