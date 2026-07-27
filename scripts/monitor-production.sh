#!/usr/bin/env bash
# Probe Lumina CI from outside its deployment boundary and emit an actionable alert.

set -euo pipefail

readonly BASE_URL="${BASE_URL:-}"
readonly ALERT_WEBHOOK_URL="${ALERT_WEBHOOK_URL:-}"
readonly DEPLOYMENT_ENVIRONMENT="${DEPLOYMENT_ENVIRONMENT:-production}"
readonly RUNBOOK_URL="${RUNBOOK_URL:-}"
readonly CURL_INSECURE="${CURL_INSECURE:-0}"

if [[ -z "$BASE_URL" || -z "$ALERT_WEBHOOK_URL" || -z "$RUNBOOK_URL" ]]; then
    printf 'BASE_URL, ALERT_WEBHOOK_URL, and RUNBOOK_URL are required.\n' >&2
    exit 2
fi
for command_name in curl jq; do
    command -v "$command_name" >/dev/null 2>&1 || {
        printf 'Required command is missing: %s\n' "$command_name" >&2
        exit 2
    }
done

body_file="$(mktemp)"
alert_file="$(mktemp)"
cleanup() {
    rm -f "$body_file" "$alert_file"
}
trap cleanup EXIT

curl_args=(
    --silent
    --show-error
    --connect-timeout 10
    --max-time 30
)
if [[ "$CURL_INSECURE" == "1" ]]; then
    curl_args+=(--insecure)
fi

probe() {
    local path="$1"
    if probe_code="$(curl "${curl_args[@]}" --output "$body_file" --write-out '%{http_code}' \
        "${BASE_URL%/}${path}")"; then
        probe_body="$(<"$body_file")"
    else
        probe_code="000"
        probe_body=""
    fi
}

probe "/health/live"
live_code="$probe_code"

probe "/health/ready"
ready_code="$probe_code"
ready_body="$probe_body"

if [[ "$live_code" == "200" &&
      "$ready_code" == "200" ]] &&
    jq -e '
      .status == "Healthy"
      and (.checks | type == "object")
      and all(.checks[]; .status == "Healthy")
    ' <<<"$ready_body" >/dev/null 2>&1; then
    printf 'Lumina CI production probe passed: live=200 ready=Healthy\n'
    exit 0
fi

if jq -e '.checks | type == "object"' <<<"$ready_body" >/dev/null 2>&1; then
    failed_checks="$(jq -c '
      [
        .checks
        | to_entries[]
        | select(.value.status != "Healthy")
        | {
            name: .key,
            status: .value.status,
            description: (.value.description // "No description"),
            durationMs: (.value.duration // null)
          }
      ]
    ' <<<"$ready_body")"
else
    failed_checks="$(jq -nc \
        --arg status "HTTP ${ready_code}" \
        '[{name: "readiness", status: $status, description: "No valid readiness payload was returned", durationMs: null}]')"
fi

observed_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
alert_payload="$(jq -nc \
    --arg environment "$DEPLOYMENT_ENVIRONMENT" \
    --arg base_url "$BASE_URL" \
    --arg live_status "HTTP ${live_code}" \
    --arg ready_status "HTTP ${ready_code}" \
    --arg observed_at "$observed_at" \
    --arg runbook_url "$RUNBOOK_URL" \
    --argjson failed_checks "$failed_checks" \
    '{
      severity: "critical",
      service: "lumina-ci",
      environment: $environment,
      summary: ("Lumina CI " + $environment + " readiness failed"),
      baseUrl: $base_url,
      liveStatus: $live_status,
      readinessStatus: $ready_status,
      failedChecks: $failed_checks,
      observedAt: $observed_at,
      runbookUrl: $runbook_url
    }')"

alert_code="$(curl "${curl_args[@]}" \
    --request POST \
    --header 'Content-Type: application/json' \
    --data "$alert_payload" \
    --output "$alert_file" \
    --write-out '%{http_code}' \
    "$ALERT_WEBHOOK_URL")"
if [[ ! "$alert_code" =~ ^2[0-9][0-9]$ ]]; then
    printf 'Production probe failed and alert delivery returned HTTP %s.\n' "$alert_code" >&2
    exit 2
fi

printf 'Production probe failed; actionable alert delivered (live=%s ready=%s).\n' \
    "$live_code" "$ready_code" >&2
exit 1
