# Lumina CI Operations

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
