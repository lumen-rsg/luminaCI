#!/usr/bin/env bash
# Exercise the real source -> build -> scan -> sign -> publish path.

set -euo pipefail

readonly BASE_URL="${BASE_URL:-https://localhost}"
readonly STAGING_USERNAME="${STAGING_USERNAME:-}"
readonly STAGING_PASSWORD="${STAGING_PASSWORD:-}"
readonly CANARY_SOURCE_URL="${CANARY_SOURCE_URL:-}"
readonly CANARY_SOURCE_SHA256="${CANARY_SOURCE_SHA256:-}"
readonly CANARY_SPEC_FILE="${CANARY_SPEC_FILE:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/Test/production_canary.spec}"
readonly CANARY_TIMEOUT_SECONDS="${CANARY_TIMEOUT_SECONDS:-900}"
readonly CURL_INSECURE="${CURL_INSECURE:-0}"

case "${CANARY_TARGET_ARCHITECTURE:-$(uname -m)}" in
    x86_64) target_architecture="x86_64" ;;
    aarch64 | arm64) target_architecture="aarch64" ;;
    *)
        printf 'Unsupported canary architecture.\n' >&2
        exit 2
        ;;
esac

if [[ -z "$STAGING_USERNAME" || -z "$STAGING_PASSWORD" ||
      -z "$CANARY_SOURCE_URL" || ! "$CANARY_SOURCE_SHA256" =~ ^[[:xdigit:]]{64}$ ]]; then
    printf 'Staging credentials, CANARY_SOURCE_URL, and a SHA-256 source digest are required.\n' >&2
    exit 2
fi
if [[ ! -s "$CANARY_SPEC_FILE" ]]; then
    printf 'Canary spec file is missing or empty: %s\n' "$CANARY_SPEC_FILE" >&2
    exit 2
fi
for command_name in curl jq; do
    command -v "$command_name" >/dev/null 2>&1 || {
        printf 'Required command is missing: %s\n' "$command_name" >&2
        exit 2
    }
done

cookie_jar="$(mktemp)"
body_file="$(mktemp)"
cleanup() {
    rm -f "$cookie_jar" "$body_file"
}
trap cleanup EXIT

curl_args=(
    --silent
    --show-error
    --connect-timeout 10
    --max-time 60
    --cookie "$cookie_jar"
    --cookie-jar "$cookie_jar"
)
if [[ "$CURL_INSECURE" == "1" ]]; then
    curl_args+=(--insecure)
fi

request() {
    local method="$1"
    local path="$2"
    local data="${3:-}"
    local args=("${curl_args[@]}" --request "$method" --output "$body_file")
    if [[ -n "$data" ]]; then
        args+=(--header "Content-Type: application/json" --data "$data")
    fi
    http_code="$(curl "${args[@]}" --write-out '%{http_code}' "${BASE_URL%/}${path}")"
    http_body="$(<"$body_file")"
}

require_status() {
    local expected="$1"
    local operation="$2"
    if [[ "$http_code" != "$expected" ]]; then
        printf '%s failed (HTTP %s): %s\n' "$operation" "$http_code" "$http_body" >&2
        exit 1
    fi
}

timestamp="$(date -u +%Y%m%d%H%M%S)"
slug="production-canary-${timestamp}"

login_payload="$(jq -nc \
    --arg username "$STAGING_USERNAME" \
    --arg password "$STAGING_PASSWORD" \
    '{username: $username, password: $password}')"
request POST "/api/auth/login" "$login_payload"
require_status 200 "Staging login"

repository_payload="$(jq -nc \
    --arg name "$slug" \
    --arg arch "$target_architecture" \
    '{
        name: $name,
        displayName: ("Production canary " + $name),
        basePath: ("canary/" + $name),
        arch: $arch,
        distribution: "fedora-44",
        createdBy: "production-canary"
    }')"
request POST "/api/repository" "$repository_payload"
require_status 201 "Canary repository creation"
repository_id="$(jq -er '.data.id' <<<"$http_body")"

source_payload="$(jq -nc \
    --arg slug "$slug" \
    --arg url "$CANARY_SOURCE_URL" \
    --arg sha256 "$CANARY_SOURCE_SHA256" \
    '{
        slug: $slug,
        sourceUrl: $url,
        sourceType: 2,
        expectedSha256: $sha256,
        isEnabled: true,
        fetchAutomatically: true
    }')"
request POST "/api/sources" "$source_payload"
require_status 201 "Canary source creation"

deadline=$((SECONDS + CANARY_TIMEOUT_SECONDS))
while (( SECONDS < deadline )); do
    request GET "/api/sources/${slug}"
    require_status 200 "Canary source status"
    source_status="$(jq -er '.data.status' <<<"$http_body")"
    if [[ "$source_status" == "2" ]]; then
        break
    fi
    if [[ "$source_status" == "3" || "$source_status" == "4" ]]; then
        printf 'Canary source fetch failed: %s\n' "$http_body" >&2
        exit 1
    fi
    sleep 2
done
if [[ "${source_status:-}" != "2" ]]; then
    printf 'Canary source fetch timed out after %s seconds.\n' "$CANARY_TIMEOUT_SECONDS" >&2
    exit 1
fi

spec_content="$(<"$CANARY_SPEC_FILE")"
pipeline_payload="$(jq -nc \
    --arg name "$slug" \
    --arg repository_id "$repository_id" \
    --arg spec_content "$spec_content" \
    --arg architecture "$target_architecture" \
    '{
        name: $name,
        description: "Production acceptance canary",
        steps: [
            {type: 0, name: "Build", order: 1, configuration: {}},
            {type: 2, name: "Scan", order: 2, configuration: {}},
            {type: 1, name: "Sign", order: 3, configuration: {}},
            {type: 3, name: "Publish", order: 4, configuration: {repositoryId: $repository_id}}
        ],
        tags: ["production-canary"],
        webhookSecret: ("canary-" + $name),
        buildImage: "lumina-rpm-build:f44-v1",
        specContent: $spec_content,
        targetDistribution: "fedora",
        targetRelease: "44",
        targetArchitecture: $architecture,
        buildProfile: ("fedora-44-" + $architecture)
    }')"
request POST "/api/pipelines" "$pipeline_payload"
require_status 201 "Canary pipeline creation"
pipeline_id="$(jq -er '.data.id' <<<"$http_body")"

trigger_payload="$(jq -nc \
    --arg spec_content "$spec_content" \
    --arg source_url "$CANARY_SOURCE_URL" \
    --arg key "$slug" \
    '{
        specName: "production_canary.spec",
        specContent: $spec_content,
        sourceUrl: $source_url,
        triggeredBy: "production-canary",
        idempotencyKey: $key
    }')"
request POST "/api/pipelines/${pipeline_id}/trigger" "$trigger_payload"
require_status 200 "Canary pipeline trigger"
build_id="$(jq -er '.data.id' <<<"$http_body")"

while (( SECONDS < deadline )); do
    request GET "/api/builds/${build_id}"
    require_status 200 "Canary build status"
    build_status="$(jq -er '.data.status' <<<"$http_body")"
    if [[ "$build_status" == "2" ]]; then
        break
    fi
    if [[ "$build_status" == "3" || "$build_status" == "4" ]]; then
        jq -r '.data.logs' <<<"$http_body" >&2
        printf 'Canary build failed: %s\n' "$http_body" >&2
        exit 1
    fi
    sleep 5
done
if [[ "${build_status:-}" != "2" ]]; then
    printf 'Canary pipeline timed out after %s seconds.\n' "$CANARY_TIMEOUT_SECONDS" >&2
    exit 1
fi

jq -e \
    --arg repository_id "$repository_id" \
    '
      .data.status == 2
      and (.data.stepRuns | length == 4)
      and all(.data.stepRuns[]; .status == 2)
      and (.data.artifacts | length > 0)
      and all(.data.artifacts[];
        (.signedAt != null)
        and (.signingKeyFingerprint | length > 0)
        and (.cveScanStatus == 5)
        and (.publishedRepositoryId == $repository_id)
        and (.publishedAt != null))
    ' <<<"$http_body" >/dev/null

artifact_name="$(jq -er '.data.artifacts[0].fileName' <<<"$http_body")"
request GET "/api/repository/${repository_id}/packages"
require_status 200 "Published package lookup"
jq -e \
    --arg artifact_name "$artifact_name" \
    '.success == true and any(.data[]; .fileName == $artifact_name and (.signingKeyFingerprint | length > 0))' \
    <<<"$http_body" >/dev/null

printf 'Staging canary passed: build=%s repository=%s artifact=%s\n' \
    "$build_id" "$repository_id" "$artifact_name"
