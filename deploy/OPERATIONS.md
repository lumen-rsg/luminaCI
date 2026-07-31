# Lumina CI Operations

## Initial configuration

Run `./scripts/init-env.sh` from the repository root. The terminal wizard
creates `deploy/.env`, generates independent strong secrets, and writes the GPG
passphrase and secrets-master-key fingerprint under `deploy/secrets/`. Choose
the production profile to enter the seven immutable image references required
by the production preflight. The initializer never silently overwrites an
existing deployment; confirmed replacements receive timestamped backups.
Replacement generates a completely new credential set and is only appropriate
for a fresh deployment whose existing data can be discarded.

For unattended provisioning, use `--non-interactive`, select a profile with
`--profile`, and supply the documented `LUMINA_INIT_*` overrides. Generated
login passwords are intentionally not printed in unattended mode; retrieve
them from the mode-0600 environment file through your secret-management
workflow.

## Backup and restore

Take backups before every deployment and on the production retention schedule.
Stop application writes first:

```bash
docker compose --env-file deploy/.env -f deploy/docker-compose.yml \
  stop nginx webapp api-gateway build-service security-service \
  scanner-service repository-service source-service
```

Back up the PostgreSQL database with `pg_dump --format=custom`. Snapshot the
RabbitMQ, MinIO, Redis, GPG key, repository, and source named volumes while
their owning containers are stopped. Snapshot `/opt/lumina/builds`,
`/opt/lumina/sources`, and `/opt/lumina/extra-sources` with ownership and modes
preserved. Encrypt the backup, write a SHA-256 manifest, copy it off-host, and
record the deployed Git revision and image digests beside it.

Restore only into stopped services. Verify the manifest before extraction,
restore PostgreSQL with `pg_restore --clean --if-exists`, restore volume
snapshots with ownership preserved, then start the stack with
`docker compose up --detach --wait`. Finish with:

```bash
./scripts/smoke-test.sh
```

Never treat an untested backup as recoverable. CI performs a destructive
backup/mutate/restore exercise for PostgreSQL, durable RabbitMQ messages, and
MinIO objects in `tests/ci/backup-restore.sh`. Production operators must run
the same exercise against a disposable restore environment on the retention
schedule and record the recovery point and recovery time.

## Kubernetes build namespace

The Kubernetes executor has a complete immutable input and short-lived artifact
transport, but remains disabled by default. Keep `BUILD_EXECUTOR_TYPE=Docker`
and `KUBERNETES_ENABLED=false` until both native workers pass the checks below.
The namespace policy can be applied in advance:

```bash
kubectl apply --server-side --field-manager=lumina-bootstrap \
  -f deploy/kubernetes/build-namespace.yaml
```

The manifest creates a Pod Security `restricted` namespace, a namespace-wide
default-deny NetworkPolicy, bounded quota/default limits, and two identities:

- `lumina-build-controller` has only the Job, Pod-log, per-Job NetworkPolicy,
  and short-lived transport-Secret operations used by BuildService.
- `lumina-build-runner` has no RBAC grants and never receives an automatically
  mounted service-account token.

If BuildService remains outside the cluster, provision a short-lived,
rotatable kubeconfig for the controller service account through the cluster's
credential workflow. Never copy its token into a build Job or runner image.
Before enablement, confirm each required permission with `kubectl auth can-i
--as=system:serviceaccount:lumina-builds:lumina-build-controller -n
lumina-builds`, and confirm the BuildService `/health/ready` response reports
the `kubernetes-executor` check healthy.

Every native worker must be dedicated before it accepts builds. Replace the
placeholder with the exact node name and verify the architecture label already
reported by kubelet:

```bash
kubectl label node NODE_NAME lumina.1t.ru/build-worker=true
kubectl taint node NODE_NAME lumina.1t.ru/build-worker=true:NoSchedule
kubectl get node NODE_NAME -L kubernetes.io/arch,lumina.1t.ru/build-worker
```

Do not add a toleration for this taint to ordinary services. Fedora runner
images must be configured for both `fedora-44-x86_64` and
`fedora-44-aarch64`, each by full `sha256` digest; startup rejects missing,
unknown, mutable, or truncated runner references once Kubernetes selection is
enabled. Cut over only by changing both `BUILD_EXECUTOR_TYPE=Kubernetes` and
`KUBERNETES_ENABLED=true`; the service fails startup instead of falling back to
Docker if the runner or cluster policy is incomplete.

## Audit ledger

The gateway records every public API mutation attempt and outcome in
`audit.audit_logs`. Ledger rows are append-only at the database boundary and
linked by SHA-256 hashes. Admins should verify the chain after deployments,
restores, and suspected incidents:

```bash
curl -skb cookies.txt https://localhost/api/audit/integrity | jq
```

Approval requires `valid: true`; retain the reported entry count with the
release or incident record. A false result identifies the first broken
sequence and must be investigated before normal operations resume. Correlate
records with application logs using `correlationId`.

Do not run retention deletes against this table: the database rejects update,
delete, and truncate operations by design. Capacity planning must include
ledger growth. If archival is introduced later, export and independently
anchor a verified chain before adding a reviewed chain-rotation migration.
Backups must continue to include the entire audit schema.

## Rollback

Every release record must contain the seven application image references pinned
by digest, the Git revision, migration inventory, backup identifier, and smoke
result. Preserve the previous known-good release environment file.

If the new release fails:

1. Stop triggers and external traffic; preserve logs and the failed release
   manifest.
2. If the release applied a migration that the previous application does not
   support, restore the pre-deployment backup before starting old containers.
3. Replace the seven `*_IMAGE` values with the previous digest-pinned manifest.
4. Run `production-preflight.sh`, then:

   ```bash
   docker compose --env-file deploy/.env -f deploy/docker-compose.yml \
     up --detach --no-build --no-deps --force-recreate --wait \
     api-gateway build-service security-service scanner-service \
     repository-service source-service webapp nginx
   ./scripts/smoke-test.sh
   ```

5. Re-enable traffic only after probes, smoke tests, and the canary pass. Record
   recovery time, data-loss window, cause, and follow-up owner.

The complete Compose CI job rehearses this path by switching all seven
application services to a preserved image manifest without rebuilding and
rerunning the end-to-end smoke suite.

## Staging acceptance canary

Configure the `staging` GitHub Environment with `LUMINA_BASE_URL`,
`LUMINA_CANARY_SOURCE_URL`, `LUMINA_CANARY_SOURCE_SHA256`,
`LUMINA_TARGET_ARCHITECTURE`, and the two `LUMINA_STAGING_*` credentials.
The source URL must serve a digest-pinned archive compatible with
`Test/production_canary.spec`.
Run the `staging acceptance` workflow after every deployment. Approval requires
the retained canary build to show four successful steps and a signed, scanned
artifact in its dedicated repository.

## Monitoring and alerts

Configure the `production` GitHub Environment with `LUMINA_BASE_URL`,
`LUMINA_RUNBOOK_URL`, and the `LUMINA_ALERT_WEBHOOK_URL` secret. The
`production monitoring` workflow probes liveness and full dependency readiness
every five minutes. A failed application or dependency check sends a critical
JSON alert containing the failed check, observed status, deployment, timestamp,
and runbook link; workflow failure is a second independent signal.

Capture the `X-Correlation-ID` response header when investigating failed API
operations. The gateway preserves that identifier across downstream HTTP
requests, and application logs expose it as the structured `CorrelationId`
property.

When `OTEL_EXPORTER_OTLP_ENDPOINT` is configured, confirm the internal
Collector is accepting both trace and metric OTLP pipelines before enabling
the exporters in production. Alert on sustained export failures, collector
queue growth, request error rate, request latency, build queue age, and runtime
resource pressure. Do not expose OTLP receiver ports through the public nginx
boundary.

The repository-provided single-host stack is started by adding
`deploy/docker-compose.observability.yml` to the normal Compose command.
Grafana is loopback-only; access it through an authenticated SSH or VPN tunnel.
Back up the `grafana_data`, `prometheus_data`, and `tempo_data` volumes with the
rest of the deployment. The default retention is 30 days for metrics and seven
days for traces; adjust `PROMETHEUS_RETENTION` and `tempo.yml` only after
checking disk growth under representative load.

Prometheus evaluates initial rules for Collector availability, HTTP 5xx ratio,
p95 request latency, and managed-heap pressure. These rules are visible in
Grafana but do not page anyone by themselves. Connect Prometheus to the
organization's authenticated Alertmanager or translate the rules into the
existing production monitoring webhook before treating them as an on-call
signal. Validate every topology or dashboard change with:

```bash
tests/ci/observability-stack.sh
```

Route the webhook to the on-call system, page on the first failed scheduled run,
and page separately when the monitoring workflow itself stops running. The
runbook must include owner contacts, log and dashboard locations, dependency
checks, rollback steps, and the staging-canary command. Validate both the
healthy and alert paths before each monitoring change with:

```bash
tests/ci/monitoring-alerts.sh
```

## Load and soak acceptance

The accepted baseline for this internal console is 10 concurrent authenticated
users for two minutes, followed by a five-user ten-minute soak. Each user
loads the console, dependency readiness, pipelines, builds, repositories, and
sources once per second. Acceptance requires fewer than 0.5% failed requests
and checks, p95 latency below 1 second, and p99 below 2 seconds.

Run the `staging load and soak` workflow after material application, database,
proxy, or infrastructure changes and retain its k6 summaries with the release
record. If expected concurrency grows beyond 10 active users, raise the profile
before approval rather than treating this baseline as permanent capacity.
The complete Compose smoke job also runs a short version to catch authentication,
routing, threshold, and script regressions on every protected branch.
