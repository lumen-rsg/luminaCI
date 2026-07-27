#!/usr/bin/env bash
# Run the accepted production load and soak profiles with a pinned k6 image.

set -euo pipefail

REPOSITORY_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly REPOSITORY_ROOT
readonly BASE_URL="${BASE_URL:-}"
readonly LOAD_USERNAME="${LOAD_USERNAME:-}"
readonly LOAD_PASSWORD="${LOAD_PASSWORD:-}"
readonly LOAD_VUS="${LOAD_VUS:-10}"
readonly LOAD_DURATION="${LOAD_DURATION:-2m}"
readonly SOAK_VUS="${SOAK_VUS:-5}"
readonly SOAK_DURATION="${SOAK_DURATION:-10m}"
readonly K6_P95_MILLISECONDS="${K6_P95_MILLISECONDS:-1000}"
readonly K6_P99_MILLISECONDS="${K6_P99_MILLISECONDS:-2000}"
readonly K6_INSECURE_SKIP_TLS_VERIFY="${K6_INSECURE_SKIP_TLS_VERIFY:-false}"
readonly RESULTS_DIR="${RESULTS_DIR:-${REPOSITORY_ROOT}/load-results}"
readonly PROFILE="${1:-all}"
readonly K6_IMAGE="${K6_IMAGE:-grafana/k6:0.54.0}"

if [[ -z "$BASE_URL" || -z "$LOAD_USERNAME" || -z "$LOAD_PASSWORD" ]]; then
    printf 'BASE_URL, LOAD_USERNAME, and LOAD_PASSWORD are required.\n' >&2
    exit 2
fi
if [[ "$PROFILE" != "load" && "$PROFILE" != "soak" && "$PROFILE" != "all" ]]; then
    printf 'Usage: %s [load|soak|all]\n' "$0" >&2
    exit 2
fi
for value in "$LOAD_VUS" "$SOAK_VUS"; do
    [[ "$value" =~ ^[1-9][0-9]*$ ]] || {
        printf 'Virtual-user counts must be positive integers.\n' >&2
        exit 2
    }
done

mkdir -p "$RESULTS_DIR"

run_profile() {
    local name="$1"
    local virtual_users="$2"
    local duration="$3"

    printf 'Running %s profile: %s VUs for %s\n' "$name" "$virtual_users" "$duration"
    docker run --rm \
        --network host \
        --user "$(id -u):$(id -g)" \
        --volume "${REPOSITORY_ROOT}/tests/load:/scripts:ro" \
        --volume "${RESULTS_DIR}:/results:Z" \
        --env BASE_URL \
        --env LOAD_USERNAME \
        --env LOAD_PASSWORD \
        --env K6_INSECURE_SKIP_TLS_VERIFY \
        --env K6_P95_MILLISECONDS \
        --env K6_P99_MILLISECONDS \
        --env K6_VUS="$virtual_users" \
        --env K6_DURATION="$duration" \
        "$K6_IMAGE" run \
        --summary-export "/results/${name}.json" \
        /scripts/production.js
}

if [[ "$PROFILE" == "load" || "$PROFILE" == "all" ]]; then
    run_profile load "$LOAD_VUS" "$LOAD_DURATION"
fi
if [[ "$PROFILE" == "soak" || "$PROFILE" == "all" ]]; then
    run_profile soak "$SOAK_VUS" "$SOAK_DURATION"
fi

printf 'Load acceptance passed; summaries are in %s.\n' "$RESULTS_DIR"
