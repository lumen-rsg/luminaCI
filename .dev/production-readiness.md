# Production Readiness Follow-up

Date: 2026-07-28
Reviewed revision: `5244ec5`

## Verdict

The original audit findings appear materially resolved, and the project is now
a strong release candidate. The production environment, deployment validation,
and exposure boundary still require hardening.

## Production configuration blockers

The current ignored `deploy/.env` is not deployable as production
configuration. During validation, MinIO rejected its credentials, and the
required RPM-signing passphrase file was not configured. Secret values are not
recorded here.

Before deployment:

- Generate strong, unique credentials for PostgreSQL, RabbitMQ, MinIO, the
  administrator account, JWT signing, and other configured secrets.
- Configure `GPG_PASSPHRASE_FILE` and verify that the mounted secret is readable
  only where required.
- Preserve the existing secrets master key after encrypted secrets have been
  written; rotating it afterward would make those values unreadable.
- Install real production TLS certificates instead of the development pair.
- Keep `.env` and secret files outside version control.

The repository documents these requirements in `README.md:730`.

## Network exposure

The Compose configuration publishes the API gateway and WebApp debug ports
directly:

- API gateway: `deploy/docker-compose.yml:148` (`5000:5000`)
- WebApp: `deploy/docker-compose.yml:522` (`5005:80`)

These listeners bypass the nginx TLS and security-header boundary. For
production, remove the host publications or bind/firewall them so that only
the intended reverse proxy can reach them.

## Evidence already passing

The following checks passed for the reviewed revision:

- Hosted required CI gate and all constituent jobs.
- `git diff --check`.
- Deployment and Dockerfile validation.
- Release build.
- All 236 .NET tests.
- Dependency audit with zero known vulnerable NuGet packages.
- Shell syntax validation.
- PostgreSQL, RabbitMQ, and MinIO restart-durability test.
- Complete RPM build, provenance, lint, embedded signing, signature
  verification, repository generation, DNF installation, and execution test.
- Hosted browser accessibility test.

Passing CI run:

<https://github.com/lumen-rsg/luminaCI/actions/runs/30308171994>

## Production acceptance checklist

- [ ] A real source-to-build-to-scan-to-sign-to-publish canary succeeds in
      staging.
- [ ] Strong production secrets and signing files are installed.
- [ ] Real TLS certificates and production DNS are configured.
- [ ] Direct debug port exposure is removed or restricted.
- [ ] Backup and restore procedures are tested.
- [ ] Rollback procedure is documented and rehearsed.
- [ ] Monitoring and actionable alerts cover the application and its
      dependencies.
- [ ] Expected production load and a suitable soak test pass.

Production approval should be reconsidered after the release blocker is fixed
and the complete staging acceptance checklist passes.
