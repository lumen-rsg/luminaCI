#!/usr/bin/env bash

set -euo pipefail

REPOSITORY_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
readonly REPOSITORY_ROOT
temporary_directory="$(mktemp -d)"
readonly temporary_directory

cleanup() {
    rm -rf "$temporary_directory"
}
trap cleanup EXIT

mkdir -p "$temporary_directory/certs"
openssl req -x509 -newkey rsa:2048 -nodes -days 365 \
    -keyout "$temporary_directory/ca.key" \
    -out "$temporary_directory/ca.crt" \
    -subj "/CN=Lumina CI Test CA" >/dev/null 2>&1
openssl req -newkey rsa:2048 -nodes \
    -keyout "$temporary_directory/certs/console.key" \
    -out "$temporary_directory/console.csr" \
    -subj "/CN=localhost" >/dev/null 2>&1
printf '%s\n' 'subjectAltName=DNS:localhost' > "$temporary_directory/extensions"
openssl x509 -req -days 90 \
    -in "$temporary_directory/console.csr" \
    -CA "$temporary_directory/ca.crt" \
    -CAkey "$temporary_directory/ca.key" \
    -CAcreateserial \
    -extfile "$temporary_directory/extensions" \
    -out "$temporary_directory/certs/console.crt" >/dev/null 2>&1

master_key='preflight-master-key-00000000000000000000000000000000000000000001'
printf '%s' "$master_key" | sha256sum | awk '{print $1}' > "$temporary_directory/master.sha256"
printf '%s\n' 'preflight-gpg-passphrase-00000000000000000001' > "$temporary_directory/gpg-passphrase"
cat > "$temporary_directory/production.env" <<EOF
POSTGRES_PASSWORD=preflight-postgres-00000000000000000000000001
RABBITMQ_PASSWORD=preflight-rabbitmq-00000000000000000000000001
MINIO_USER=preflight-minio-user-00000000000000000000000001
MINIO_PASSWORD=preflight-minio-000000000000000000000000001
JWT_SECRET=preflight-jwt-00000000000000000000000000000000000000000001
ADMIN_PASSWORD=preflight-admin-0000000000000000000000000000001
SECRETS_MASTER_KEY=${master_key}
SECRETS_MASTER_KEY_FINGERPRINT_FILE=${temporary_directory}/master.sha256
GPG_PASSPHRASE_FILE=${temporary_directory}/gpg-passphrase
GRAFANA_ADMIN_PASSWORD=preflight-grafana-000000000000000000000000000001
API_GATEWAY_IMAGE=registry.invalid/api-gateway@sha256:0000000000000000000000000000000000000000000000000000000000000001
BUILD_SERVICE_IMAGE=registry.invalid/build-service@sha256:0000000000000000000000000000000000000000000000000000000000000002
SECURITY_SERVICE_IMAGE=registry.invalid/security-service@sha256:0000000000000000000000000000000000000000000000000000000000000003
SCANNER_SERVICE_IMAGE=registry.invalid/scanner-service@sha256:0000000000000000000000000000000000000000000000000000000000000004
REPOSITORY_SERVICE_IMAGE=registry.invalid/repository-service@sha256:0000000000000000000000000000000000000000000000000000000000000005
SOURCE_SERVICE_IMAGE=registry.invalid/source-service@sha256:0000000000000000000000000000000000000000000000000000000000000006
WEBAPP_IMAGE=registry.invalid/webapp@sha256:0000000000000000000000000000000000000000000000000000000000000007
PACKAGES_WEB_IMAGE=registry.invalid/packages-web@sha256:0000000000000000000000000000000000000000000000000000000000000008
EOF
chmod 0600 \
    "$temporary_directory/production.env" \
    "$temporary_directory/master.sha256" \
    "$temporary_directory/gpg-passphrase" \
    "$temporary_directory/certs/console.key"

ENV_FILE="$temporary_directory/production.env" \
CERT_DIR="$temporary_directory/certs" \
PRODUCTION_HOST=localhost \
EXPECTED_PUBLIC_IP=127.0.0.1 \
    "$REPOSITORY_ROOT/scripts/production-preflight.sh"

cp "$temporary_directory/production.env" "$temporary_directory/grafana-invalid.env"
sed -i 's/^GRAFANA_ADMIN_PASSWORD=.*/GRAFANA_ADMIN_PASSWORD=<set-a-password>/' \
    "$temporary_directory/grafana-invalid.env"
chmod 0600 "$temporary_directory/grafana-invalid.env"
if ENV_FILE="$temporary_directory/grafana-invalid.env" \
   CERT_DIR="$temporary_directory/certs" \
   PRODUCTION_HOST=localhost \
       "$REPOSITORY_ROOT/scripts/production-preflight.sh" >/dev/null 2>&1; then
    printf 'Production preflight accepted a placeholder Grafana password.\n' >&2
    exit 1
fi

sed -i 's/preflight-postgres-[0-9]*/password/' "$temporary_directory/production.env"
if ENV_FILE="$temporary_directory/production.env" \
   CERT_DIR="$temporary_directory/certs" \
   PRODUCTION_HOST=localhost \
       "$REPOSITORY_ROOT/scripts/production-preflight.sh" >/dev/null 2>&1; then
    printf 'Production preflight accepted a weak password.\n' >&2
    exit 1
fi

printf 'Production preflight acceptance and rejection paths passed.\n'
