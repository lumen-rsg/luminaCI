#!/usr/bin/env bash

set -euo pipefail

REPOSITORY_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly REPOSITORY_ROOT
readonly ENV_FILE="${ENV_FILE:-${REPOSITORY_ROOT}/deploy/.env}"
readonly CERT_DIR="${CERT_DIR:-${REPOSITORY_ROOT}/deploy/nginx/certs}"
readonly PRODUCTION_HOST="${PRODUCTION_HOST:?PRODUCTION_HOST must be the public console DNS name}"
readonly EXPECTED_PUBLIC_IP="${EXPECTED_PUBLIC_IP:-}"
readonly COMPOSE_FILE="${REPOSITORY_ROOT}/deploy/docker-compose.yml"

for command_name in docker jq openssl sha256sum getent stat; do
    command -v "$command_name" >/dev/null 2>&1 ||
        { printf 'Required command is missing: %s\n' "$command_name" >&2; exit 2; }
done

[[ -f "$ENV_FILE" ]] || { printf 'Environment file not found: %s\n' "$ENV_FILE" >&2; exit 1; }

declare -A environment=()
while IFS='=' read -r key value; do
    [[ "$key" =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]] || continue
    value="${value%%[[:space:]]#*}"
    value="${value%$'\r'}"
    environment["$key"]="$value"
done < "$ENV_FILE"

required_secrets=(
    POSTGRES_PASSWORD
    RABBITMQ_PASSWORD
    MINIO_USER
    MINIO_PASSWORD
    JWT_SECRET
    ADMIN_PASSWORD
    SECRETS_MASTER_KEY
)

release_images=(
    API_GATEWAY_IMAGE
    BUILD_SERVICE_IMAGE
    SECURITY_SERVICE_IMAGE
    SCANNER_SERVICE_IMAGE
    REPOSITORY_SERVICE_IMAGE
    SOURCE_SERVICE_IMAGE
    WEBAPP_IMAGE
)

for key in "${required_secrets[@]}"; do
    value="${environment[$key]:-}"
    if (( ${#value} < 24 )); then
        printf '%s must contain at least 24 characters.\n' "$key" >&2
        exit 1
    fi
    if [[ "$value" == *"<set-"* || "$value" == ci-* || "$value" == *password* ]]; then
        printf '%s still contains a placeholder or test value.\n' "$key" >&2
        exit 1
    fi
done

for key in "${release_images[@]}"; do
    value="${environment[$key]:-}"
    if [[ ! "$value" =~ ^[^[:space:]@]+@sha256:[[:xdigit:]]{64}$ ]]; then
        printf '%s must be an immutable registry reference pinned by SHA-256 digest.\n' "$key" >&2
        exit 1
    fi
done

if (( ${#environment[JWT_SECRET]} < 48 || ${#environment[SECRETS_MASTER_KEY]} < 48 )); then
    printf 'JWT_SECRET and SECRETS_MASTER_KEY must contain at least 48 characters.\n' >&2
    exit 1
fi

for (( left = 0; left < ${#required_secrets[@]}; left++ )); do
    for (( right = left + 1; right < ${#required_secrets[@]}; right++ )); do
        left_key="${required_secrets[$left]}"
        right_key="${required_secrets[$right]}"
        if [[ "${environment[$left_key]}" == "${environment[$right_key]}" ]]; then
            printf '%s and %s must be unique.\n' "$left_key" "$right_key" >&2
            exit 1
        fi
    done
done

check_private_file() {
    local path="$1"
    local description="$2"
    [[ -f "$path" && -s "$path" ]] ||
        { printf '%s is missing or empty: %s\n' "$description" "$path" >&2; exit 1; }
    local mode
    mode="$(stat -c '%a' "$path")"
    if (( (8#$mode & 8#077) != 0 )); then
        printf '%s must not be accessible by group or others: %s (mode %s)\n' \
            "$description" "$path" "$mode" >&2
        exit 1
    fi
}

gpg_passphrase_file="${environment[GPG_PASSPHRASE_FILE]:-}"
[[ -n "$gpg_passphrase_file" ]] ||
    { printf 'GPG_PASSPHRASE_FILE is required.\n' >&2; exit 1; }
if [[ "$gpg_passphrase_file" != /* ]]; then
    gpg_passphrase_file="$(cd "$(dirname "$ENV_FILE")" && pwd)/${gpg_passphrase_file#./}"
fi
check_private_file "$gpg_passphrase_file" "GPG passphrase file"
check_private_file "$ENV_FILE" "Production environment file"

master_key_fingerprint_file="${environment[SECRETS_MASTER_KEY_FINGERPRINT_FILE]:-}"
[[ -n "$master_key_fingerprint_file" ]] ||
    { printf 'SECRETS_MASTER_KEY_FINGERPRINT_FILE is required.\n' >&2; exit 1; }
if [[ "$master_key_fingerprint_file" != /* ]]; then
    master_key_fingerprint_file="$(cd "$(dirname "$ENV_FILE")" && pwd)/${master_key_fingerprint_file#./}"
fi
check_private_file "$master_key_fingerprint_file" "Secrets master-key fingerprint"
expected_master_fingerprint="$(tr -d '[:space:]' < "$master_key_fingerprint_file")"
actual_master_fingerprint="$(
    printf '%s' "${environment[SECRETS_MASTER_KEY]}" | sha256sum | awk '{print $1}'
)"
[[ "$actual_master_fingerprint" == "$expected_master_fingerprint" ]] ||
    { printf 'SECRETS_MASTER_KEY does not match its preserved fingerprint.\n' >&2; exit 1; }

certificate="${CERT_DIR}/console.crt"
private_key="${CERT_DIR}/console.key"
[[ -f "$certificate" ]] || { printf 'TLS certificate not found: %s\n' "$certificate" >&2; exit 1; }
check_private_file "$private_key" "TLS private key"
openssl x509 -in "$certificate" -noout -checkend 2592000 >/dev/null ||
    { printf 'TLS certificate expires in less than 30 days.\n' >&2; exit 1; }
openssl x509 -in "$certificate" -noout -checkhost "$PRODUCTION_HOST" >/dev/null ||
    { printf 'TLS certificate does not cover %s.\n' "$PRODUCTION_HOST" >&2; exit 1; }
certificate_subject="$(openssl x509 -in "$certificate" -noout -subject -nameopt RFC2253)"
certificate_issuer="$(openssl x509 -in "$certificate" -noout -issuer -nameopt RFC2253)"
[[ "${certificate_subject#subject=}" != "${certificate_issuer#issuer=}" ]] ||
    { printf 'A self-signed TLS certificate is not permitted in production.\n' >&2; exit 1; }
certificate_public_key="$(
    openssl x509 -in "$certificate" -pubkey -noout |
        openssl pkey -pubin -outform DER 2>/dev/null |
        sha256sum | awk '{print $1}'
)"
private_public_key="$(
    openssl pkey -in "$private_key" -pubout -outform DER 2>/dev/null |
        sha256sum | awk '{print $1}'
)"
[[ "$certificate_public_key" == "$private_public_key" ]] ||
    { printf 'TLS certificate and private key do not match.\n' >&2; exit 1; }

mapfile -t resolved_ips < <(getent ahosts "$PRODUCTION_HOST" | awk '{print $1}' | sort -u)
(( ${#resolved_ips[@]} > 0 )) ||
    { printf 'Production DNS name does not resolve: %s\n' "$PRODUCTION_HOST" >&2; exit 1; }
if [[ -n "$EXPECTED_PUBLIC_IP" ]] &&
   [[ ! " ${resolved_ips[*]} " =~ (^|[[:space:]])${EXPECTED_PUBLIC_IP}($|[[:space:]]) ]]; then
    printf '%s does not resolve to EXPECTED_PUBLIC_IP=%s.\n' \
        "$PRODUCTION_HOST" "$EXPECTED_PUBLIC_IP" >&2
    exit 1
fi

compose_json="$(
    docker compose --env-file "$ENV_FILE" --file "$COMPOSE_FILE" config --format json
)"
jq -e '
    ((.services["api-gateway"].ports // []) | length == 0) and
    ((.services.webapp.ports // []) | length == 0) and
    ([.services.nginx.ports[]?.published | tostring] | sort == ["443", "80"])
' >/dev/null <<<"$compose_json" ||
    { printf 'Only nginx ports 80 and 443 may be published.\n' >&2; exit 1; }

printf 'Production preflight passed for %s.\n' "$PRODUCTION_HOST"
