#!/usr/bin/env bash
# Lumina CI deployment smoke test.
#
# This test is deliberately read-only apart from creating and revoking its own
# authenticated session. All application requests go through the public
# gateway/nginx boundary; internal service and infrastructure ports are never
# assumed to be host-published.
#
# Usage:
#   SMOKE_USERNAME=admin SMOKE_PASSWORD='...' ./scripts/smoke-test.sh
#
# Optional:
#   BASE_URL=https://console.lumina.1t.ru
#   CURL_INSECURE=1          # local self-signed TLS only
#   CHECK_CONTAINERS=0       # remote deployment without Docker access

set -euo pipefail

if [[ "${1:-}" == "--help" ]]; then
    sed -n '2,15p' "$0"
    exit 0
fi

readonly BASE_URL="${BASE_URL:-https://localhost}"
readonly SMOKE_USERNAME="${SMOKE_USERNAME:-}"
readonly SMOKE_PASSWORD="${SMOKE_PASSWORD:-}"
readonly CHECK_CONTAINERS="${CHECK_CONTAINERS:-1}"
readonly CURL_INSECURE="${CURL_INSECURE:-0}"

if [[ -z "$SMOKE_USERNAME" || -z "$SMOKE_PASSWORD" ]]; then
    printf 'SMOKE_USERNAME and SMOKE_PASSWORD are required; default credentials are not supported.\n' >&2
    exit 2
fi

for command_name in curl jq; do
    if ! command -v "$command_name" >/dev/null 2>&1; then
        printf 'Required command is missing: %s\n' "$command_name" >&2
        exit 2
    fi
done

COOKIE_JAR="$(mktemp)"
BODY_FILE="$(mktemp)"
cleanup() {
    rm -f "$COOKIE_JAR" "$BODY_FILE"
}
trap cleanup EXIT

CURL_ARGS=(
    --silent
    --show-error
    --connect-timeout 5
    --max-time 30
    --cookie "$COOKIE_JAR"
    --cookie-jar "$COOKIE_JAR"
)
if [[ "$CURL_INSECURE" == "1" ]]; then
    CURL_ARGS+=(--insecure)
fi

PASS=0
FAIL=0
HTTP_CODE=""
HTTP_BODY=""

pass() {
    PASS=$((PASS + 1))
    printf '  PASS  %s\n' "$1"
}

fail() {
    FAIL=$((FAIL + 1))
    printf '  FAIL  %s\n' "$1"
}

section() {
    printf '\n%s\n' "$1"
}

request() {
    local method="$1"
    local path="$2"
    local data="${3:-}"
    local request_args=("${CURL_ARGS[@]}" --request "$method" --output "$BODY_FILE")

    if [[ -n "$data" ]]; then
        request_args+=(--header "Content-Type: application/json" --data "$data")
    fi

    if ! HTTP_CODE="$(curl "${request_args[@]}" --write-out '%{http_code}' "${BASE_URL%/}$path")"; then
        HTTP_CODE="000"
    fi
    HTTP_BODY="$(<"$BODY_FILE")"
}

expect_status() {
    local description="$1"
    shift
    local expected
    for expected in "$@"; do
        if [[ "$HTTP_CODE" == "$expected" ]]; then
            pass "$description (HTTP $HTTP_CODE)"
            return
        fi
    done
    fail "$description (HTTP $HTTP_CODE, expected: $*)"
}

expect_json() {
    local description="$1"
    shift
    if jq -e "$@" >/dev/null 2>&1 <<<"$HTTP_BODY"; then
        pass "$description"
    else
        fail "$description (response did not match the expected JSON shape)"
    fi
}

section "Container state"
if [[ "$CHECK_CONTAINERS" == "1" ]]; then
    if ! command -v docker >/dev/null 2>&1; then
        fail "Docker is required when CHECK_CONTAINERS=1"
    else
        REQUIRED_CONTAINERS=(
            lumina-postgres
            lumina-redis
            lumina-rabbitmq
            lumina-minio
            lumina-api-gateway
            lumina-build-service
            lumina-docker-socket-proxy
            lumina-security-service
            lumina-scanner-service
            lumina-trivy
            lumina-repository-service
            lumina-source-service
            lumina-webapp
            lumina-nginx
        )
        for container_name in "${REQUIRED_CONTAINERS[@]}"; do
            container_state="$(docker inspect --format '{{.State.Status}}' "$container_name" 2>/dev/null || true)"
            if [[ "$container_state" == "running" ]]; then
                pass "$container_name is running"
            else
                fail "$container_name is ${container_state:-missing}"
            fi
        done
    fi
else
    printf '  SKIP  local container checks disabled\n'
fi

section "Public boundary and authentication"
request GET "/"
expect_status "Web console entry document" 200

for health_path in /health/startup /health/live /health/ready; do
    request GET "$health_path"
    expect_status "Anonymous ${health_path} probe" 200
done
expect_json "Readiness response reports healthy dependencies" \
    '.status == "Healthy" and (.checks | type == "object")'

request GET "/api/pipelines?page=1&pageSize=1"
expect_status "Protected API rejects anonymous requests" 401

login_payload="$(jq -nc \
    --arg username "$SMOKE_USERNAME" \
    --arg password "$SMOKE_PASSWORD" \
    '{username: $username, password: $password}')"
request POST "/api/auth/login" "$login_payload"
expect_status "Login" 200
expect_json "Login returns the authenticated identity" \
    --arg username "$SMOKE_USERNAME" '.username == $username and (.role | type == "string")'

request GET "/api/auth/me"
expect_status "Session cookie authenticates requests" 200
expect_json "Current-user response matches the smoke account" \
    --arg username "$SMOKE_USERNAME" '.username == $username'

section "Gateway service routes"
request GET "/api/pipelines?page=1&pageSize=1"
expect_status "BuildService pipeline route" 200
expect_json "Pipeline response envelope" \
    '.success == true and (.data.pipelines | type == "array")'

request GET "/api/builds?page=1&pageSize=1"
expect_status "BuildService build route" 200
expect_json "Build response envelope" \
    '.success == true and (.data.builds | type == "array")'

request GET "/api/sources"
expect_status "SourceService route" 200
expect_json "Source response envelope" \
    '.success == true and (.data.packages | type == "array")'

request GET "/api/security/keys"
expect_status "SecurityService route" 200
expect_json "Signing-key response envelope" \
    '.success == true and (.data | type == "array")'

request GET "/api/scanner/scans?page=1&pageSize=1"
expect_status "ScannerService route" 200
expect_json "Scanner response envelope" \
    '.success == true and (.data.scans | type == "array")'

request GET "/api/repository?page=1&pageSize=1"
expect_status "RepositoryService route" 200
expect_json "Repository response envelope" \
    '.success == true and (.data | type == "array")'

section "Session lifecycle"
request POST "/api/auth/refresh"
expect_status "Refresh-token rotation" 200

request POST "/api/auth/logout"
expect_status "Logout" 200

request GET "/api/auth/me"
expect_status "Revoked session is rejected" 401

printf '\nResults: %d passed, %d failed\n' "$PASS" "$FAIL"
if (( FAIL > 0 )); then
    exit 1
fi
