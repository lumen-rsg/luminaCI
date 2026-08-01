#!/usr/bin/env bash

set -euo pipefail

REPOSITORY_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
readonly REPOSITORY_ROOT
readonly INITIALIZER="${REPOSITORY_ROOT}/scripts/init-env.sh"
temporary_directory="$(mktemp -d)"
readonly temporary_directory

cleanup() {
    rm -rf "$temporary_directory"
}
trap cleanup EXIT

fail() {
    printf 'Environment initializer test failed: %s\n' "$1" >&2
    exit 1
}

environment_value() {
    local file="$1"
    local key="$2"
    sed -n "s/^${key}=//p" "$file"
}

local_directory="${temporary_directory}/local"
local_environment="${local_directory}/lumina.env"
"$INITIALIZER" \
    --non-interactive \
    --profile local \
    --output "$local_environment" >/dev/null

[[ "$(stat -c '%a' "$local_environment")" == "600" ]] ||
    fail "environment file is not mode 0600"
[[ "$(stat -c '%a' "${local_directory}/secrets/gpg-passphrase")" == "600" ]] ||
    fail "GPG passphrase is not mode 0600"
[[ "$(stat -c '%a' "${local_directory}/secrets/secrets-master-key.sha256")" == "600" ]] ||
    fail "master-key fingerprint is not mode 0600"
[[ "$(stat -c '%a' "${local_directory}/secrets")" == "700" ]] ||
    fail "secrets directory is not mode 0700"
if grep -Eq '^([A-Z_][A-Z0-9_]*)=<set-' "$local_environment"; then
    fail "generated environment still contains a value placeholder"
fi

required_secrets=(
    POSTGRES_PASSWORD
    RABBITMQ_PASSWORD
    MINIO_USER
    MINIO_PASSWORD
    JWT_SECRET
    ADMIN_PASSWORD
    SECRETS_MASTER_KEY
)
declare -A seen_secrets=()
for key in "${required_secrets[@]}"; do
    value="$(environment_value "$local_environment" "$key")"
    (( ${#value} >= 24 )) || fail "$key is shorter than 24 characters"
    [[ ! -v "seen_secrets[$value]" ]] || fail "$key reuses another generated secret"
    seen_secrets["$value"]="$key"
done

master_key="$(environment_value "$local_environment" SECRETS_MASTER_KEY)"
expected_fingerprint="$(
    printf '%s' "$master_key" | sha256sum | awk '{print $1}'
)"
actual_fingerprint="$(
    tr -d '[:space:]' < "${local_directory}/secrets/secrets-master-key.sha256"
)"
[[ "$expected_fingerprint" == "$actual_fingerprint" ]] ||
    fail "master-key fingerprint does not match"
[[ "$(environment_value "$local_environment" API_GATEWAY_IMAGE)" == \
   "lumina-api-gateway:local" ]] ||
    fail "local profile did not select local images"
[[ "$(environment_value "$local_environment" GPG_PASSPHRASE_FILE)" == \
   "${local_directory}/secrets/gpg-passphrase" ]] ||
    fail "custom output did not use an absolute GPG secret path"

original_digest="$(sha256sum "$local_environment")"
if "$INITIALIZER" \
    --non-interactive \
    --profile local \
    --output "$local_environment" >/dev/null 2>&1; then
    fail "existing files were overwritten without --force"
fi
[[ "$(sha256sum "$local_environment")" == "$original_digest" ]] ||
    fail "refused overwrite changed the environment"

"$INITIALIZER" \
    --non-interactive \
    --profile local \
    --force \
    --output "$local_environment" >/dev/null
compgen -G "${local_environment}.bak.*" >/dev/null ||
    fail "--force did not back up the environment"
compgen -G "${local_directory}/secrets/gpg-passphrase.bak.*" >/dev/null ||
    fail "--force did not back up the GPG passphrase"
compgen -G "${local_directory}/secrets/secrets-master-key.sha256.bak.*" >/dev/null ||
    fail "--force did not back up the master-key fingerprint"

production_directory="${temporary_directory}/production"
production_environment="${production_directory}/lumina.env"
image_variables=(
    API_GATEWAY_IMAGE
    BUILD_SERVICE_IMAGE
    SECURITY_SERVICE_IMAGE
    SCANNER_SERVICE_IMAGE
    REPOSITORY_SERVICE_IMAGE
    SOURCE_SERVICE_IMAGE
    WEBAPP_IMAGE
    PACKAGES_WEB_IMAGE
)
for index in "${!image_variables[@]}"; do
    variable_name="${image_variables[$index]}"
    printf -v digest '%064x' "$((index + 1))"
    printf -v "LUMINA_INIT_${variable_name}" \
        'registry.example/lumina/%s@sha256:%s' \
        "${variable_name,,}" \
        "$digest"
    export "LUMINA_INIT_${variable_name}"
done

LUMINA_INIT_ADMIN_USERNAME=operator \
LUMINA_INIT_DEVELOPER_USERNAME=developer \
    "$INITIALIZER" \
        --non-interactive \
        --profile production \
        --output "$production_environment" >/dev/null

[[ "$(environment_value "$production_environment" ADMIN_USERNAME)" == "operator" ]] ||
    fail "admin username override was not applied"
[[ -n "$(environment_value "$production_environment" DEVELOPER_PASSWORD)" ]] ||
    fail "developer account override did not generate a password"
for variable_name in "${image_variables[@]}"; do
    value="$(environment_value "$production_environment" "$variable_name")"
    [[ "$value" =~ ^[^[:space:]@]+@sha256:[[:xdigit:]]{64}$ ]] ||
        fail "$variable_name is not digest pinned"
done

if LUMINA_INIT_API_GATEWAY_IMAGE=registry.example/lumina:latest \
   "$INITIALIZER" \
       --non-interactive \
       --profile production \
       --output "${temporary_directory}/invalid/lumina.env" >/dev/null 2>&1; then
    fail "production profile accepted a mutable image"
fi

printf '%s\n' 'Environment initializer generation, permissions, backups, and validation passed.'
