#!/usr/bin/env bash
# Interactive initializer for deploy/.env and its file-backed secrets.

set -euo pipefail

REPOSITORY_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly REPOSITORY_ROOT
readonly TEMPLATE_FILE="${REPOSITORY_ROOT}/deploy/.env.example"

output_file="${REPOSITORY_ROOT}/deploy/.env"
profile=""
non_interactive=false
force=false

usage() {
    cat <<'EOF'
Usage: scripts/init-env.sh [options]

Create a secure Lumina CI environment file with a guided terminal interface.

Options:
  --output PATH           Destination (default: deploy/.env)
  --profile MODE          local or production
  --non-interactive       Use defaults and LUMINA_INIT_* environment variables
  --force                 Reinitialize a fresh deployment after creating backups
  -h, --help              Show this help

Non-interactive overrides:
  LUMINA_INIT_ADMIN_USERNAME
  LUMINA_INIT_DEVELOPER_USERNAME
  LUMINA_INIT_HOST_DATA_ROOT
  LUMINA_INIT_LDAP_HOST, LUMINA_INIT_LDAP_PORT, LUMINA_INIT_LDAP_BASE_DN
  LUMINA_INIT_LDAP_BIND_DN, LUMINA_INIT_LDAP_BIND_PASSWORD
  LUMINA_INIT_<IMAGE_VARIABLE> for each of the eight production image variables
EOF
}

while (( $# > 0 )); do
    case "$1" in
        --output)
            (( $# >= 2 )) || { printf '%s\n' '--output requires a path.' >&2; exit 2; }
            output_file="$2"
            shift 2
            ;;
        --profile)
            (( $# >= 2 )) || { printf '%s\n' '--profile requires local or production.' >&2; exit 2; }
            profile="$2"
            shift 2
            ;;
        --non-interactive)
            non_interactive=true
            shift
            ;;
        --force)
            force=true
            shift
            ;;
        -h | --help)
            usage
            exit 0
            ;;
        *)
            printf 'Unknown option: %s\n' "$1" >&2
            usage >&2
            exit 2
            ;;
    esac
done

[[ -f "$TEMPLATE_FILE" ]] ||
    { printf 'Environment template not found: %s\n' "$TEMPLATE_FILE" >&2; exit 1; }
command -v openssl >/dev/null 2>&1 ||
    { printf '%s\n' 'openssl is required to generate secure secrets.' >&2; exit 2; }
command -v sha256sum >/dev/null 2>&1 ||
    { printf '%s\n' 'sha256sum is required to preserve the master-key fingerprint.' >&2; exit 2; }

if [[ "$output_file" != /* ]]; then
    output_file="$(pwd)/$output_file"
fi
output_directory="$(dirname "$output_file")"
mkdir -p "$output_directory"
output_directory="$(cd "$output_directory" && pwd)"
output_file="${output_directory}/$(basename "$output_file")"
if [[ "$output_file" == *'$'* || "$output_file" == *'#'* ||
      "$output_file" == *$'\n'* || "$output_file" == *$'\r'* ]]; then
    printf '%s\n' 'The output path cannot contain $, #, or newline characters.' >&2
    exit 1
fi
secrets_directory="${output_directory}/secrets"
gpg_file="${secrets_directory}/gpg-passphrase"
fingerprint_file="${secrets_directory}/secrets-master-key.sha256"
readonly output_directory output_file secrets_directory gpg_file fingerprint_file

if [[ -t 1 && -z "${NO_COLOR:-}" ]]; then
    readonly bold=$'\033[1m'
    readonly blue=$'\033[34m'
    readonly green=$'\033[32m'
    readonly yellow=$'\033[33m'
    readonly reset=$'\033[0m'
else
    readonly bold="" blue="" green="" yellow="" reset=""
fi

heading() {
    printf '\n%s%s%s\n' "${blue}${bold}" "$1" "$reset"
}

note() {
    printf '  %s\n' "$1"
}

prompt_value() {
    local variable_name="$1"
    local label="$2"
    local default_value="$3"
    local value
    while true; do
        read -r -p "  ${label} [${default_value}]: " value </dev/tty
        value="${value:-$default_value}"
        if [[ "$value" != *'$'* && "$value" != *'#'* &&
              "$value" != *$'\n'* && "$value" != *$'\r'* ]]; then
            printf -v "$variable_name" '%s' "$value"
            return
        fi
        note "Please avoid \$, #, and newline characters in .env values."
    done
}

prompt_yes_no() {
    local label="$1"
    local default_answer="$2"
    local answer
    while true; do
        read -r -p "  ${label} [${default_answer}]: " answer </dev/tty
        answer="${answer:-$default_answer}"
        case "${answer,,}" in
            y | yes) return 0 ;;
            n | no) return 1 ;;
            *) note "Enter yes or no." ;;
        esac
    done
}

prompt_secret() {
    local variable_name="$1"
    local label="$2"
    local value
    while true; do
        read -r -s -p "  ${label}: " value </dev/tty
        printf '\n'
        if [[ -n "$value" && "$value" != *'$'* && "$value" != *'#'* &&
              "$value" != *$'\n'* && "$value" != *$'\r'* ]]; then
            printf -v "$variable_name" '%s' "$value"
            return
        fi
        note "A non-empty value without \$, #, or newlines is required."
    done
}

if ! $non_interactive; then
    [[ -r /dev/tty ]] ||
        { printf '%s\n' 'No interactive terminal found; use --non-interactive.' >&2; exit 2; }
    printf '%s\n' "${bold}Lumina CI deployment initializer${reset}"
    note "Secrets are generated locally and written with private permissions."
    note "Press Enter to accept a displayed default."

    if [[ -z "$profile" ]]; then
        heading "Deployment profile"
        note "1) Local       Build application images from this checkout"
        note "2) Production Use immutable registry images pinned by digest"
        while true; do
            read -r -p '  Select profile [1]: ' selection </dev/tty
            case "${selection:-1}" in
                1 | local) profile="local"; break ;;
                2 | production) profile="production"; break ;;
                *) note "Enter 1 or 2." ;;
            esac
        done
    fi
fi

profile="${profile:-${LUMINA_INIT_PROFILE:-local}}"
[[ "$profile" == "local" || "$profile" == "production" ]] ||
    { printf 'Invalid profile: %s (expected local or production)\n' "$profile" >&2; exit 2; }

existing_targets=()
for target in "$output_file" "$gpg_file" "$fingerprint_file"; do
    [[ -e "$target" ]] && existing_targets+=("$target")
done
if (( ${#existing_targets[@]} > 0 )) && [[ "$force" != true ]]; then
    if $non_interactive; then
        printf '%s\n' 'Refusing to overwrite existing deployment files:' >&2
        printf '  %s\n' "${existing_targets[@]}" >&2
        printf '%s\n' 'Run again with --force to create a backup and replace it.' >&2
        exit 1
    fi
    heading "Existing configuration"
    note "The following deployment files already exist:"
    printf '  %s\n' "${existing_targets[@]}"
    printf '\n%s%sWarning:%s replacing these files generates new database, storage,\n' \
        "$yellow" "$bold" "$reset"
    note "signing, JWT, and encryption credentials. Existing deployment data"
    note "will no longer be usable with the new configuration."
    if prompt_yes_no "This is a fresh deployment; back up and replace them?" "no"; then
        force=true
    else
        note "No files changed."
        exit 0
    fi
fi

declare -A config=()
config[JWT_ACCESS_MINUTES]="15"
config[JWT_REFRESH_HOURS]="8"
config[JWT_COOKIE_SECURE]="true"
if [[ "$output_directory" == "${REPOSITORY_ROOT}/deploy" ]]; then
    config[GPG_PASSPHRASE_FILE]="./secrets/gpg-passphrase"
    config[SECRETS_MASTER_KEY_FINGERPRINT_FILE]="./secrets/secrets-master-key.sha256"
else
    config[GPG_PASSPHRASE_FILE]="$gpg_file"
    config[SECRETS_MASTER_KEY_FINGERPRINT_FILE]="$fingerprint_file"
fi
config[ADMIN_USERNAME]="${LUMINA_INIT_ADMIN_USERNAME:-admin}"
config[DEVELOPER_USERNAME]="${LUMINA_INIT_DEVELOPER_USERNAME:-developer}"
config[DEVELOPER_PASSWORD]=""
config[GRAFANA_ADMIN_USER]="admin"
config[LDAP_HOST]="${LUMINA_INIT_LDAP_HOST:-}"
config[LDAP_PORT]="${LUMINA_INIT_LDAP_PORT:-389}"
config[LDAP_BASE_DN]="${LUMINA_INIT_LDAP_BASE_DN:-}"
config[LDAP_BIND_DN]="${LUMINA_INIT_LDAP_BIND_DN:-}"
config[LDAP_BIND_PASSWORD]="${LUMINA_INIT_LDAP_BIND_PASSWORD:-}"
config[BUILD_NETWORK]="lumina-buildnet"
config[HOST_DATA_ROOT]="${LUMINA_INIT_HOST_DATA_ROOT:-/opt/lumina}"
config[BUILD_MEMORY_BYTES]="2147483648"
config[BUILD_PIDS_LIMIT]="512"
config[BUILD_CPU_QUOTA]="150000"

if ! $non_interactive; then
    admin_username=""
    developer_username=""
    host_data_root=""
    ldap_host=""
    ldap_port=""
    ldap_base_dn=""
    ldap_bind_dn=""
    ldap_bind_password=""

    heading "Accounts"
    while true; do
        prompt_value admin_username "Initial admin username" "${config[ADMIN_USERNAME]}"
        [[ "$admin_username" =~ ^[A-Za-z0-9._@-]{1,64}$ ]] && break
        note "Use 1-64 letters, digits, or . _ @ - characters."
    done
    config[ADMIN_USERNAME]="$admin_username"
    if prompt_yes_no "Create an initial developer account?" "no"; then
        while true; do
            prompt_value developer_username "Developer username" "${config[DEVELOPER_USERNAME]}"
            [[ "$developer_username" =~ ^[A-Za-z0-9._@-]{1,64}$ ]] && break
            note "Use 1-64 letters, digits, or . _ @ - characters."
        done
        config[DEVELOPER_USERNAME]="$developer_username"
        create_developer=true
    else
        create_developer=false
    fi

    heading "Storage"
    while true; do
        prompt_value host_data_root "Absolute host data directory" "${config[HOST_DATA_ROOT]}"
        [[ "$host_data_root" == /* ]] && break
        note "Enter an absolute path beginning with /."
    done
    config[HOST_DATA_ROOT]="$host_data_root"

    heading "LDAP"
    if prompt_yes_no "Configure LDAP authentication now?" "no"; then
        prompt_value ldap_host "LDAP host" "ldap.example.com"
        while true; do
            prompt_value ldap_port "LDAP port" "${config[LDAP_PORT]}"
            if [[ "$ldap_port" =~ ^[0-9]{1,5}$ ]] &&
               (( 10#$ldap_port >= 1 && 10#$ldap_port <= 65535 )); then
                break
            fi
            note "Enter a port between 1 and 65535."
        done
        prompt_value ldap_base_dn "Base DN" "dc=example,dc=com"
        prompt_value ldap_bind_dn "Bind DN" "cn=lumina,dc=example,dc=com"
        prompt_secret ldap_bind_password "Bind password"
        config[LDAP_HOST]="$ldap_host"
        config[LDAP_PORT]="$ldap_port"
        config[LDAP_BASE_DN]="$ldap_base_dn"
        config[LDAP_BIND_DN]="$ldap_bind_dn"
        config[LDAP_BIND_PASSWORD]="$ldap_bind_password"
    fi
else
    create_developer=false
    [[ -n "${LUMINA_INIT_DEVELOPER_USERNAME:-}" ]] && create_developer=true
fi

[[ "${config[ADMIN_USERNAME]}" =~ ^[A-Za-z0-9._@-]{1,64}$ ]] ||
    { printf '%s\n' 'Admin username must use 1-64 letters, digits, or . _ @ - characters.' >&2; exit 1; }
if $create_developer &&
   [[ ! "${config[DEVELOPER_USERNAME]}" =~ ^[A-Za-z0-9._@-]{1,64}$ ]]; then
    printf '%s\n' 'Developer username must use 1-64 letters, digits, or . _ @ - characters.' >&2
    exit 1
fi
[[ "${config[HOST_DATA_ROOT]}" == /* ]] ||
    { printf '%s\n' 'HOST_DATA_ROOT must be an absolute path.' >&2; exit 1; }
[[ "${config[LDAP_PORT]}" =~ ^[0-9]{1,5}$ ]] ||
    { printf '%s\n' 'LDAP_PORT must be a number between 1 and 65535.' >&2; exit 1; }
(( 10#${config[LDAP_PORT]} >= 1 && 10#${config[LDAP_PORT]} <= 65535 )) ||
    { printf '%s\n' 'LDAP_PORT must be a number between 1 and 65535.' >&2; exit 1; }
for key in HOST_DATA_ROOT LDAP_HOST LDAP_BASE_DN LDAP_BIND_DN LDAP_BIND_PASSWORD; do
    value="${config[$key]}"
    if [[ "$value" == *'$'* || "$value" == *'#'* ||
          "$value" == *$'\n'* || "$value" == *$'\r'* ]]; then
        printf '%s cannot contain $, #, or newline characters.\n' "$key" >&2
        exit 1
    fi
done

generate_secret() {
    openssl rand -base64 48 | tr -d '\n'
}

config[POSTGRES_PASSWORD]="$(generate_secret)"
config[RABBITMQ_PASSWORD]="$(generate_secret)"
config[MINIO_USER]="lumina-$(openssl rand -hex 12)"
config[MINIO_PASSWORD]="$(generate_secret)"
config[JWT_SECRET]="$(generate_secret)"
config[SECRETS_MASTER_KEY]="$(generate_secret)"
config[ADMIN_PASSWORD]="$(generate_secret)"
config[GRAFANA_ADMIN_PASSWORD]="$(generate_secret)"
gpg_passphrase="$(generate_secret)"
if $create_developer; then
    config[DEVELOPER_PASSWORD]="$(generate_secret)"
fi

readonly -a image_variables=(
    API_GATEWAY_IMAGE
    BUILD_SERVICE_IMAGE
    SECURITY_SERVICE_IMAGE
    SCANNER_SERVICE_IMAGE
    REPOSITORY_SERVICE_IMAGE
    SOURCE_SERVICE_IMAGE
    WEBAPP_IMAGE
    PACKAGES_WEB_IMAGE
)
readonly -a local_images=(
    lumina-api-gateway:local
    lumina-build-service:local
    lumina-security-service:local
    lumina-scanner-service:local
    lumina-repository-service:local
    lumina-source-service:local
    lumina-webapp:local
    lumina-packages-web:local
)

for index in "${!image_variables[@]}"; do
    variable_name="${image_variables[$index]}"
    if [[ "$profile" == "local" ]]; then
        config["$variable_name"]="${local_images[$index]}"
        continue
    fi

    environment_override="LUMINA_INIT_${variable_name}"
    image_reference="${!environment_override:-}"
    if ! $non_interactive; then
        heading "${variable_name}"
        note "Paste a registry reference ending in @sha256:<64 hex characters>."
        image_name="${variable_name%_IMAGE}"
        image_name="${image_name,,}"
        image_name="${image_name//_/-}"
        while true; do
            prompt_value image_reference "Image" \
                "${image_reference:-registry.example/lumina-${image_name}@sha256:<digest>}"
            [[ "$image_reference" =~ ^[^[:space:]@#\$]+@sha256:[[:xdigit:]]{64}$ ]] && break
            note "That image is not pinned to a complete SHA-256 digest."
            image_reference=""
        done
    fi
    if [[ ! "$image_reference" =~ ^[^[:space:]@#\$]+@sha256:[[:xdigit:]]{64}$ ]]; then
        printf '%s must be an immutable digest-pinned image reference.\n' "$variable_name" >&2
        exit 1
    fi
    config["$variable_name"]="$image_reference"
done

mkdir -p "$secrets_directory"
chmod 0700 "$secrets_directory"

backup_suffix="$(date -u +%Y%m%dT%H%M%SZ).$$"
backup_file() {
    local path="$1"
    if [[ -e "$path" ]]; then
        backup_path="${path}.bak.${backup_suffix}"
        cp -p "$path" "$backup_path"
        printf '  Backed up %s\n' "$backup_path"
    fi
}

if $force; then
    backup_file "$output_file"
    backup_file "$gpg_file"
    backup_file "$fingerprint_file"
fi

umask 077
temporary_env="$(mktemp "${output_directory}/.lumina-env.XXXXXX")"
temporary_gpg="$(mktemp "${secrets_directory}/.gpg-passphrase.XXXXXX")"
temporary_fingerprint="$(mktemp "${secrets_directory}/.master-fingerprint.XXXXXX")"
cleanup() {
    rm -f "$temporary_env" "$temporary_gpg" "$temporary_fingerprint"
}
trap cleanup EXIT

while IFS= read -r line || [[ -n "$line" ]]; do
    if [[ "$line" =~ ^([A-Z_][A-Z0-9_]*)= ]]; then
        key="${BASH_REMATCH[1]}"
        if [[ -v "config[$key]" ]]; then
            printf '%s=%s\n' "$key" "${config[$key]}" >> "$temporary_env"
            continue
        fi
    fi
    printf '%s\n' "$line" >> "$temporary_env"
done < "$TEMPLATE_FILE"

printf '%s\n' "$gpg_passphrase" > "$temporary_gpg"
printf '%s' "${config[SECRETS_MASTER_KEY]}" |
    sha256sum | awk '{print $1}' > "$temporary_fingerprint"
chmod 0600 "$temporary_env" "$temporary_gpg" "$temporary_fingerprint"
mv -f "$temporary_gpg" "$gpg_file"
mv -f "$temporary_fingerprint" "$fingerprint_file"
mv -f "$temporary_env" "$output_file"
trap - EXIT

heading "Configuration ready"
note "Environment: $output_file"
note "Profile: $profile"
note "File-backed secrets: $secrets_directory"
note "All generated files are private to the current user."
if ! $non_interactive; then
    printf '\n%sSave these generated login credentials now:%s\n' "${yellow}${bold}" "$reset"
    printf '  Admin username:  %s\n' "${config[ADMIN_USERNAME]}"
    printf '  Admin password:  %s\n' "${config[ADMIN_PASSWORD]}"
    if $create_developer; then
        printf '  Developer user:  %s\n' "${config[DEVELOPER_USERNAME]}"
        printf '  Developer pass:  %s\n' "${config[DEVELOPER_PASSWORD]}"
    fi
fi
printf '\n%sNext:%s\n' "${green}${bold}" "$reset"
if [[ "$profile" == "production" ]]; then
    note "Install the production TLS certificate and key under deploy/nginx/certs."
    note "Run PRODUCTION_HOST=<dns-name> ./scripts/production-preflight.sh."
else
    note "Provide TLS files, then run: docker compose --env-file \"$output_file\" -f deploy/docker-compose.yml up -d --build"
fi
