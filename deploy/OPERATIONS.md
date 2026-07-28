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
