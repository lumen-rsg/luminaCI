#!/usr/bin/env bash

set -euo pipefail

REPOSITORY_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
readonly REPOSITORY_ROOT
temporary_directory="$(mktemp -d)"
readonly temporary_directory
readonly port_file="${temporary_directory}/port"
readonly alert_file="${temporary_directory}/alert.json"

python3 "${REPOSITORY_ROOT}/tests/ci/monitoring-fixture.py" \
    --port-file "$port_file" \
    --alert-file "$alert_file" &
fixture_pid=$!

cleanup() {
    kill "$fixture_pid" >/dev/null 2>&1 || true
    wait "$fixture_pid" 2>/dev/null || true
    rm -rf "$temporary_directory"
}
trap cleanup EXIT

for _ in {1..50}; do
    [[ -s "$port_file" ]] && break
    sleep 0.1
done
[[ -s "$port_file" ]] || {
    printf 'Monitoring fixture did not start.\n' >&2
    exit 1
}

port="$(<"$port_file")"
base_url="http://127.0.0.1:${port}"

BASE_URL="$base_url" \
ALERT_WEBHOOK_URL="${base_url}/alert" \
RUNBOOK_URL="https://runbooks.example/lumina" \
    "${REPOSITORY_ROOT}/scripts/monitor-production.sh"
[[ ! -e "$alert_file" ]]

curl --fail --silent --show-error --request POST "${base_url}/mode/unhealthy"
if BASE_URL="$base_url" \
    ALERT_WEBHOOK_URL="${base_url}/alert" \
    RUNBOOK_URL="https://runbooks.example/lumina" \
    "${REPOSITORY_ROOT}/scripts/monitor-production.sh"; then
    printf 'Unhealthy production probe unexpectedly passed.\n' >&2
    exit 1
fi

jq -e '
  .severity == "critical"
  and .service == "lumina-ci"
  and (.summary | contains("readiness failed"))
  and (.runbookUrl == "https://runbooks.example/lumina")
  and any(.failedChecks[]; .name == "postgres" and .status == "Unhealthy")
' "$alert_file" >/dev/null

printf 'Production monitoring alert-path test passed.\n'
