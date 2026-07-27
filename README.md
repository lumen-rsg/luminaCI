# Lumina CI

[![CI](https://github.com/lumen-rsg/luminaCI/actions/workflows/ci.yml/badge.svg?branch=staging)](https://github.com/lumen-rsg/luminaCI/actions/workflows/ci.yml)
[![.NET](https://img.shields.io/badge/.NET-10.0-512bd4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Docker](https://img.shields.io/badge/Docker-Compose-2496ed?logo=docker&logoColor=white)](https://docs.docker.com/compose/)
[![Platform](https://img.shields.io/badge/platform-Linux-1793d1?logo=linux&logoColor=white)](#system-requirements)

A microservice continuous-integration system for **RPM packages**. Lumina CI
fetches sources, builds RPMs in isolated containers, **PGP-signs** them,
**CVE-scans** them with Trivy, and **publishes** them to a managed repository —
triggered manually from the web console or automatically on a Git push.

It is built on .NET 10 (services + Blazor WASM UI) and orchestrated with Docker
Compose, with a security model designed around running **untrusted `.spec`
files** safely.

---

## Table of contents

- [Overview](#overview)
- [Architecture](#architecture)
- [Features](#features)
- [System requirements](#system-requirements)
- [Quick start](#quick-start)
- [Configuration](#configuration)
- [Usage](#usage)
- [Build security model](#build-security-model)
- [API reference](#api-reference)
- [Development](#development)
- [Service management](#service-management)
- [Troubleshooting](#troubleshooting)
- [Credentials & secrets](#credentials--secrets)

---

## Overview

Lumina CI automates the RPM release pipeline end to end:

1. **Source** — resolve a pinned HTTPS Git repository or tarball URL.
2. **Build** — create an SRPM, rebuild it in a fresh RPM topdir, and retain the
   inputs, dependency inventory, runner identity, and logs inside an ephemeral,
   isolated container.
3. **Scan** — run Trivy against the artifacts and enforce the CVE policy.
4. **Sign** — attach and verify an RPM PGP signature (security-service, GPG).
5. **Publish** — add the signed package to a managed RPM repository served by
   nginx.

Everything is fronted by a YARP **API gateway** that owns authentication and
fans requests out to the individual services. A Blazor **WASM** web app is the
primary UI; a REST API is available for automation and integrations.

---

## Architecture

```
                        ┌──────────────┐
                        │   Browser    │
                        │ (Blazor WASM)│
                        └──────┬───────┘
                               │ :443 / :80 (TLS terminated here)
                        ┌──────▼───────┐
                        │    nginx     │──── packages.lumina… (repo)
                        └──────┬───────┘           :80 → /srv/packages (read-only repo_data)
                               │ :5000
                   ┌───────────▼────────────┐
                   │     api-gateway        │   YARP reverse proxy,
                   │  (auth, rate-limit,    │   JWT issued here,
                   │   cookie ↔ bearer)     │   HttpOnly session cookies
                   └─┬─────┬─────┬─────┬─────┘
            ┌─────────┘     │     │     └─────────────┐
            │               │     │                   │
   ┌────────▼──────┐ ┌──────▼─────┐ ┌────────▼──────┐ ┌▼───────────────┐
   │ build-service │ │ security-  │ │ scanner-      │ │ repository-    │
   │  (rpmbuild    │ │ service    │ │ service       │ │ service        │
   │   orchestr.)  │ │ (PGP sign) │ │ (Trivy CVE)   │ │ (RPM repo mgmt)│
   └──────┬────────┘ └────────────┘ └───────┬───────┘ └────────────────┘
          │ docker-socket-proxy              │ trivy server
          │ (whitelisted API)                │
   ┌──────▼──────────────▼───┐         ┌─────▼─────┐
   │  lumina-buildnet        │         │   trivy   │
   │  (isolated build net)   │         └───────────┘
   │  ┌───────────────────┐  │
   │  │ rpm-build /       │  │  untrusted .spec → rpmbuild
   │  │ dotnet-build      │  │  (cap-drop, mem/cpu/pids limits,
   │  │ containers        │  │   no-new-privileges, non-root)
   │  └───────────────────┘  │
   └─────────────────────────┘

   Shared infrastructure (lumina-network): postgres · redis · rabbitmq · minio
   source-service pulls HTTPS Git/tar sources into /opt/lumina/sources
```

**Services**

| Service | Port | Role |
|---|---|---|
| `nginx` | 80, 443 | TLS termination, static UI, RPM repo HTTP |
| `api-gateway` | 5000 | Auth (login/refresh/logout/me), YARP routing, rate limiting |
| `build-service` | 5001 | Build orchestration, pipelines, builds, webhooks, extra sources |
| `security-service` | 5002 | PGP key management, signing, hashing |
| `scanner-service` | 5003 | Trivy CVE scanning |
| `repository-service` | 5004 | RPM repository management & publishing |
| `source-service` | 5006 | Revisioned package catalog and pinned HTTPS source fetching |
| `webapp` | 5005 | Blazor WASM UI |
| `docker-socket-proxy` | 2375 (internal) | Least-privilege Docker API for build-service |
| `trivy` | 8080 (internal) | CVE database & scan server |

> All infrastructure ports (Postgres, Redis, RabbitMQ, MinIO, Trivy) are
> intentionally **not published** to the host — services reach them over the
> internal `lumina-network`. Only `nginx` (80/443) and the per-service debug
> ports are exposed.

---

## Features

- **Pipeline-driven builds** — name, describe, tag, and trigger builds; each
  pipeline optionally wires up Git integration and a webhook secret.
- **Revisioned package sources** — HTTPS Git and checksum-pinned archives are
  managed in PostgreSQL through the API/UI. Saving a package appends an
  immutable revision and automatically queues a durable fetch processed by a
  bounded worker with leases, heartbeats, cancellation, and restart recovery.
- **Isolated build containers** — every `rpmbuild`/`dotnet build` runs in a
  throwaway container on a dedicated network, with caps, limits, and a
  non-root user (see [Build security model](#build-security-model)).
- **Traceable artifact bundles** — every successful build retains its SRPM,
  binary RPMs, complete log, source/spec/patch hashes, installed-package
  inventory, target platform, and resolved runner image digest or ID.
- **PGP signing** — generate/manage signing keys and embed verified RPM signatures.
- **CVE scanning** — Trivy integration with results stored per-artifact.
- **Managed RPM repository** — verify and stage packages privately, then
  atomically publish each architecture’s RPM set and `createrepo_c` metadata
  for nginx to serve.
- **Secure session model** — short-lived access JWT in an `HttpOnly`,
  `Secure`, `SameSite=Strict` cookie, rotated refresh tokens in Redis.
- **Rate limiting & lockout** — per-IP global token bucket plus a strict
  per-IP+per-username login bucket, and exponential account lockout.
- **Live build logs** — Server-Sent Events streaming of build output.

---

## System requirements

- **OS**: Linux (RHEL/CentOS/Fedora/ALTLinux/Ubuntu)
- **Docker**: >= 24.0
- **Docker Compose**: >= 2.20 (`docker compose` plugin)
- **RAM**: minimum 4 GB, 8 GB recommended
- **Disk**: minimum 20 GB free
- **Ports**: 80, 443 (entry points); 5000–5006 (per-service debug)

---

## Quick start

### 1. Clone

```bash
git clone <repository-url> lumina-ci
cd lumina-ci
```

### 2. Configure environment

```bash
cd deploy
cp .env.example .env
# Edit .env and fill in every <set-…> placeholder (see Configuration below).
```

Lumina CI has **no default credentials**. The stack will refuse to start unless
at least these are set:

```env
POSTGRES_PASSWORD=…        # PostgreSQL password
RABBITMQ_PASSWORD=…        # RabbitMQ password
MINIO_USER=…               # MinIO (S3) username
MINIO_PASSWORD=…           # MinIO password
JWT_SECRET=…               # >= 32 chars, signs access/refresh tokens
SECRETS_MASTER_KEY=…       # >= 32 chars, encrypts pipeline secrets at rest
GPG_PASSPHRASE_FILE=…      # path to the RPM signing secret file
ADMIN_PASSWORD=…           # initial admin password (seeded on first boot)
```

Create the signing secret file referenced by `GPG_PASSPHRASE_FILE` before
starting Compose. For the example value in `.env.example`:

```bash
mkdir -p secrets
openssl rand -base64 48 > secrets/gpg-passphrase
chmod 0400 secrets/gpg-passphrase
```

### 3. Provide a TLS certificate (required)

nginx terminates TLS for the console vhost and refuses to start without a cert.
For local dev, generate a self-signed pair:

```bash
openssl req -x509 -newkey rsa:2048 -nodes -days 365 \
  -keyout deploy/nginx/certs/console.key \
  -out deploy/nginx/certs/console.crt \
  -subj "/CN=console.lumina.1t.ru"
chmod 0400 deploy/nginx/certs/console.key
```

### 4. Bring the stack up

```bash
docker compose up -d --build
```

The first build takes ~5–10 minutes (compiling .NET services and build images,
pulling base images).

### 5. Verify

```bash
# Everything should be Up / Healthy
docker compose ps

# Full dependency readiness report
curl -sk https://localhost/health/ready | jq
```

Each application service also exposes `/health/startup`, `/health/live`, and
`/health/ready`. The readiness response names each dependency and returns HTTP
503 until every required database, cache, broker, storage, tool, key, runner,
and writable volume check succeeds.

Open the console at **`https://localhost`** (or `https://console.lumina.1t.ru`
if you added a hosts/DNS entry) and log in with the admin account you seeded.

---

## Configuration

All runtime configuration flows through `deploy/.env` (see
`deploy/.env.example` for the canonical, commented reference).

### Credentials & auth

| Variable | Required | Description |
|---|---|---|
| `POSTGRES_PASSWORD` | yes | PostgreSQL password |
| `RABBITMQ_PASSWORD` | yes | RabbitMQ password |
| `MINIO_USER` | no | MinIO username (default `luminaadmin`) |
| `MINIO_PASSWORD` | yes | MinIO password |
| `JWT_SECRET` | yes | JWT signing key, min 32 chars. Shared with every service so they can re-validate tokens (defense-in-depth). |
| `JWT_ACCESS_MINUTES` | no | Access-token lifetime (default `15`) |
| `JWT_REFRESH_HOURS` | no | Refresh-token lifetime / session length (default `8`) |
| `JWT_COOKIE_SECURE` | no | Set `false` **only** for plain-HTTP local dev (default `true`) |
| `SECRETS_MASTER_KEY` | yes | AES-256-GCM master key for at-rest encryption of pipeline secrets (`WebhookSecret`, `GitToken`). **Never change it after secrets are written** — they become undecryptable. |
| `GPG_PASSPHRASE_FILE` | yes | Host path to a file containing the RPM signing-key passphrase. Mounted into SecurityService as a Docker secret; never place the passphrase itself in `.env`. |
| `ADMIN_USERNAME` | no | Initial admin username (default `admin`) |
| `ADMIN_PASSWORD` | yes | Initial admin password (seeded once, when the users table is empty) |
| `DEVELOPER_USERNAME` | no | Optional developer account username |
| `DEVELOPER_PASSWORD` | no | Optional developer password (account is seeded only if set) |
| `AUTH_MAX_FAILED_LOGINS` | no | Failed attempts before lockout (default `5`) |
| `AUTH_LOCKOUT_MINUTES` | no | Base lockout window (default `15`); doubles each cycle |
| `AUTH_MAX_LOCKOUT_MINUTES` | no | Lockout ceiling (default `1440` = 24 h) |

### LDAP (optional)

Leave `LDAP_HOST` empty to use the built-in credential store.

| Variable | Default | Description |
|---|---|---|
| `LDAP_HOST` | _empty_ | LDAP server host |
| `LDAP_PORT` | `389` | LDAP port |
| `LDAP_BASE_DN` | _empty_ | Base DN |
| `LDAP_BIND_DN` | _empty_ | Bind DN |
| `LDAP_BIND_PASSWORD` | _empty_ | Bind password |

### Rate limiting

| Variable | Default | Description |
|---|---|---|
| `RATE_LIMIT_GLOBAL_PERMIT` | `100` | Global token bucket size (per IP) |
| `RATE_LIMIT_GLOBAL_TPS` | `100` | Global tokens replenished per second |
| `RATE_LIMIT_GLOBAL_QUEUE` | `10` | Global queued requests allowed |
| `RATE_LIMIT_LOGIN_PERMIT` | `5` | Login bucket size (per IP + username) |
| `RATE_LIMIT_LOGIN_TPS` | `5` | Login tokens replenished per second |
| `RATE_LIMIT_LOGIN_QUEUE` | `0` | Login queue (0 = reject immediately when empty) |

### Build-container isolation

These tune the per-build container that runs untrusted `.spec` files. Defaults
are intentionally tight — only relax them if you understand the impact.

| Variable | Default | Description |
|---|---|---|
| `BUILD_NETWORK` | `lumina-buildnet` | Dedicated bridge network for build containers (separate from `lumina-network`) |
| `BUILD_MEMORY_BYTES` | `2147483648` | Per-container memory cap in bytes; `memory-swap` is pinned equal to this (no overcommit) |
| `BUILD_PIDS_LIMIT` | `512` | Max processes per container (blunt fork-bomb guard) |
| `BUILD_CPU_QUOTA` | `150000` | CPU quota in µs per 100 000 µs period (`150000` = 1.5 CPUs) |

### DNS (production)

For hostname-based access, add DNS records or `/etc/hosts` entries:

```
<server-ip>  console.lumina.1t.ru
<server-ip>  packages.lumina.1t.ru
```

---

## Usage

### Web console

Open `https://<host>/`. Pages:

| Page | Description |
|---|---|
| **Dashboard** | System overview and statistics |
| **Pipelines** | Create, edit, tag, and trigger build pipelines |
| **Builds** | Build history, live logs, artifacts, scan/sign status |
| **Repositories** | Managed RPM repositories and published packages |
| **Sources** | Add and revise package sources; saves automatically queue fetching |
| **Security** | PGP signing keys, signing history, artifact hashes |

### Create a pipeline

1. **Pipelines → + New Pipeline**
2. Fill in **Name**, **Description**, and optional **Tags** (comma-separated).
3. (Optional) **Git integration**:
   - **Git Repository URL** — `.git` URL
   - **Branch** — default `main`
   - **Spec File Path** — path to the `.spec` inside the repo
   - **Webhook Secret** — shared secret used to verify inbound webhooks
4. Select the reviewed **Fedora 44** build target and its native
   **x86_64** or **aarch64** architecture.
5. **Create**.

### Trigger a build

- **Manual**: on the Pipelines page, press **▶ Run** and complete the form
  (package spec name, source URL, RPM spec content, triggered-by).
- **Automatic**: on push to the configured Git branch (see
  [Webhooks](#webhooks)).

### Watch a build

Open **Builds → `<id>`** for live log streaming (SSE), artifact list,
downloadable `.spec`, Trivy/PGP status, and the snapshotted target profile and
immutable Docker image ID used for the run.

### Webhooks

After creating a pipeline with Git integration, point your Git host at:

```
https://<host>/api/webhooks/<pipeline-id>
```

Find the pipeline id:

```bash
curl -sk https://localhost/api/pipelines?page=1 \
  | jq '.data.pipelines[] | select(.name=="my-package-build") | .id'
```

**GitHub** — Repository → Settings → Webhooks → Add webhook.
Payload URL as above, content type `application/json`, secret = the pipeline's
webhook secret, trigger = *Just the push event*.

**GitLab** — Project → Settings → Webhooks. URL as above, secret token =
webhook secret, trigger = *Push events*.

**Forgejo / Gitea** — Repository → Settings → Webhooks → Add webhook.
Target URL as above, secret = webhook secret, trigger = *Push events*.

### Package sources

Add a package from the **Sources** page or `POST /api/sources`. The catalog is
stored in PostgreSQL: package identities remain stable, edits append immutable
revisions, and optimistic revision checks reject conflicting updates. Saving an
enabled package queues that exact revision for fetching by default.

Git sources require an explicit branch, tag, or commit. Archive sources require
an expected SHA-256. Fetches record the resolved Git commit or final download
URL and store the result under a content-addressed object key. Git submodules,
embedded credentials, private-network targets, and unauthenticated transports
are rejected at the trust boundary.

---

## Build security model

The build container (`rpm-build` / `dotnet-build`) runs `dnf builddep` and
`rpmbuild` over **untrusted `.spec` files** — effectively arbitrary shell as
root inside the container. Several layers turn that from a host-escape primitive
into a constrained sandbox:

- **No host Docker socket in build-service.** Instead of mounting
  `/var/run/docker.sock` (which with `user: root` was trivial host root),
  build-service runs as a non-privileged `app` user and reaches the daemon
  through `docker-socket-proxy` (tecnativa/docker-socket-proxy). The proxy
  forwards only what build-service needs — `containers` create/start/logs/wait/
  stop/remove and `images` list/pull — and denies `exec`, `networks`,
  `volumes`, `build`, etc.
- **Isolated build network.** Build containers attach to `lumina-buildnet`,
  separate from `lumina-network`. They have outbound internet (for
  `dnf builddep`, `spectool`, `git clone`) but **no path** to PostgreSQL,
  RabbitMQ, MinIO, Trivy, or the app services. (Previously this used
  `NetworkMode: host`.)
- **Container restrictions** — `--cap-drop=ALL` (plus the minimal set rpmbuild
  needs), `--pids-limit`, `--memory` with `--memory-swap` pinned equal (no
  overcommit), CPU quota, `--security-opt=no-new-privileges`, `--init`. Tunable
  via the `BUILD_*` env vars.
- **Unprivileged build images.** `rpm-build.Dockerfile` /
  `dotnet-build.Dockerfile` create a `rpmbuilder` user (uid/gid 1000) and run
  `rpmbuild` as that user. `sudo` is not present in either image.

### Recommended for production: user-namespace remapping

To map root inside build containers to an **unprivileged uid on the host**,
enable userns-remap at the Docker-daemon level:

```bash
# 1. Create the remap identity (Docker uses its subuid/subgid ranges)
sudo groupadd -r dockremap
sudo useradd -r -g dockremap -d /nonexistent -s /usr/sbin/nologin dockremap
sudo usermod --add-subuids 100000-165535 --add-subgids 100000-165535 dockremap

# 2. Install the reference daemon.json (merge with your existing one — don't
#    overwrite blindly)
sudo install -m 644 deploy/docker/daemon.json /etc/docker/daemon.json

# 3. Restart the daemon
sudo systemctl restart docker
```

After this, **all** containers on the host (including build containers) are
remapped; a container-root escape lands on host uid `100000+`, not host root.
The compose stack works unchanged — services still talk over `lumina-network`.

> ⚠️ If other services on the same host genuinely need real host root (e.g.
> another CI mounting the socket), userns-remap will break them. Run Lumina CI
> on a dedicated host/daemon, or scope `--userns-host` to those services only.

---

## API reference

All routes are exposed through the gateway at `https://<host>/api/*`. Browser
sessions authenticate via the `lumina_access` HttpOnly cookie automatically;
API clients should use the cookie jar from `/api/auth/login` (recommended) or
send `Authorization: Bearer <access-token>`.

### Authentication

```bash
# Log in — credentials live in cookies (use a jar for subsequent calls)
curl -skc cookies.txt -X POST https://localhost/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"admin","password":"<ADMIN_PASSWORD>"}'

# Who am I? (reads the access cookie)
curl -skb cookies.txt https://localhost/api/auth/me
```

| Method | Endpoint | Description |
|---|---|---|
| `POST` | `/api/auth/login` | Log in; sets `lumina_access` + `lumina_refresh` cookies |
| `POST` | `/api/auth/refresh` | Rotate refresh token, issue a new access cookie |
| `POST` | `/api/auth/logout` | Revoke refresh token server-side, clear cookies |
| `GET` | `/api/auth/me` | Current user (`{ username, role }`) |

### Pipelines

| Method | Endpoint | Description |
|---|---|---|
| `GET` | `/api/pipelines?page=&pageSize=` | List pipelines |
| `GET` | `/api/pipelines/{id}` | Pipeline details |
| `POST` | `/api/pipelines` | Create a pipeline |
| `PUT` | `/api/pipelines/{id}` | Update a pipeline |
| `DELETE` | `/api/pipelines/{id}` | Delete a pipeline |
| `POST` | `/api/pipelines/{id}/trigger` | Trigger a manual build |
| `POST` | `/api/pipelines/{id}/trigger-auto` | Trigger an automatic (webhook-style) build |

Pipeline definitions are executed in `Build → Scan → Sign → Publish` order.
Each build snapshots the declared steps and records independent per-run state;
the Publish step requires a `repositoryId` configuration value.

### Builds

| Method | Endpoint | Description |
|---|---|---|
| `GET` | `/api/builds?page=&pageSize=` | List builds |
| `GET` | `/api/builds/{id}` | Build details (artifacts, status) |
| `GET` | `/api/builds/{id}/artifacts` | List artifacts for a build |
| `GET` | `/api/builds/{id}/artifacts/{fileName}` | Download an artifact |
| `GET` | `/api/builds/{id}/spec` | Download the `.spec` file |
| `GET` | `/api/builds/{id}/logs` | Build logs (plain) |
| `GET` | `/api/builds/{id}/logs/stream` | Build logs (SSE stream) |
| `POST` | `/api/builds/{id}/cancel` | Cancel a running/queued build |
| `GET` | `/api/builds/queue` | Current build queue |
| `DELETE` | `/api/builds/queue/clear` | Clear the queue |
| `PUT` | `/api/builds/artifacts/{artifactId}/scan-status` | Record scan result |
| `PUT` | `/api/builds/artifacts/{artifactId}/pgp-signature` | Record signature |
| `GET` | `/api/builds/failed-logs` | List failed-build logs |
| `GET` | `/api/builds/failed-logs/{fileName}` | Download a failed-build log |

### Extra sources (per-pipeline)

| Method | Endpoint | Description |
|---|---|---|
| `GET` | `/api/extra-sources/pipeline/{pipelineId}` | List extra sources |
| `POST` | `/api/extra-sources/pipeline/{pipelineId}` | Add extra sources |
| `DELETE` | `/api/extra-sources/pipeline/{pipelineId}` | Remove all extra sources |
| `DELETE` | `/api/extra-sources/pipeline/{pipelineId}/{filePath}` | Remove one extra source |

### Webhooks

| Method | Endpoint | Description |
|---|---|---|
| `POST` | `/api/webhooks/{pipelineId}` | Git push webhook (verified via webhook secret) |

### Security (PGP & hashing)

| Method | Endpoint | Description |
|---|---|---|
| `GET` | `/api/security/keys` | List signing keys |
| `POST` | `/api/security/keys/generate` | Generate a new signing key |
| `POST` | `/api/security/sign` | Sign a package |
| `GET` | `/api/security/signing/history` | Signing history |
| `POST` | `/api/security/hash/compute` | Compute SHA-256/MD5 hashes |
| `POST` | `/api/security/hash/verify` | Verify a hash |
| `POST` | `/api/security/hash/store` | Store a hash for an artifact |
| `GET` | `/api/security/hash/{artifactId}` | Get stored hash for an artifact |
| `GET` | `/api/security/hashes` | List all stored hashes |

### Scanner (CVE)

| Method | Endpoint | Description |
|---|---|---|
| `POST` | `/api/scanner/scan` | Scan an artifact with Trivy |
| `GET` | `/api/scanner/scans` | List past scans |

### Repository

| Method | Endpoint | Description |
|---|---|---|
| `GET` | `/api/repository` | List repositories |
| `POST` | `/api/repository` | Create a repository |
| `POST` | `/api/repository/publish` | Publish a package to a repository |
| `POST` | `/api/repository/upload` | Upload a package (≤ 500 MiB) |
| `POST` | `/api/repository/sync` | Re-sync repository metadata |
| `GET` | `/api/repository/{repositoryId}/packages` | List packages in a repository |

### Sources

| Method | Endpoint | Description |
|---|---|---|
| `GET` | `/api/sources` | List configured sources |
| `GET` | `/api/sources/{name}` | Get a source by name |
| `POST` | `/api/sources` | Create a revisioned package and optionally fetch it |
| `PUT` | `/api/sources/{name}` | Append a package revision with optimistic concurrency |
| `DELETE` | `/api/sources/{name}?expectedRevision=` | Disable a package while retaining history |
| `POST` | `/api/sources/{name}/fetch` | Fetch one source |
| `POST` | `/api/sources/fetch-all` | Fetch all sources |
| `POST` | `/api/sources/jobs/{jobId}/cancel` | Cancel a pending or running fetch attempt |
| `POST` | `/api/sources/{name}/build` | Build a source |
| `GET` | `/api/sources/{name}/status` | Fetch status for a source |
| `GET` | `/api/sources/{name}/download` | Download a fetched source |

> Interactive docs (Swagger) are available at `https://localhost/swagger` when
> the gateway runs in Development mode.

---

## Development

The solution targets **.NET 10** (`Lumina CI.sln`). Layout:

```
src/
  Services/
    Lumina.ApiGateway/        YARP gateway + auth
    Lumina.BuildService/      build orchestration
    Lumina.SecurityService/   PGP signing / hashing
    Lumina.ScannerService/    Trivy scanning
    Lumina.RepositoryService/ RPM repo management
    Lumina.SourceService/     source fetching
    Lumina.WebApp/            Blazor WASM UI
  Shared/
    Lumina.Shared/            shared models, DTOs, JWT/auth wiring
    Lumina.Web.Shared/        shared web concerns (authorization policies)
tests/
    Lumina.Shared.Tests/
    Lumina.SourceService.Tests/
    Lumina.BuildService.Tests/
deploy/
    docker-compose.yml        full stack
    docker/                   service + build Dockerfiles
    nginx/                    nginx config + certs/
    .env.example              canonical env reference
scripts/
    build-rpm.sh              local/standalone RPM build driver
    sign-package.sh           standalone PGP signing helper
    smoke-test.sh             end-to-end API smoke test
```

### Build & test locally

```bash
dotnet restore "Lumina CI.sln"
dotnet build  "Lumina CI.sln" -c Release
dotnet test   "Lumina CI.sln" -c Release --no-build
```

The unit tests are infrastructure-free (no Docker/RabbitMQ/Redis/Postgres) and
run identically in CI and locally.

### CI

GitHub Actions (`.github/workflows/`) builds and tests the whole solution on
every push/PR to `staging` and `main`, and publishes the `tests.trx` results as
an artifact.

### Smoke test

With the stack running, exercise the full API surface:

```bash
./scripts/smoke-test.sh        # honors BUILD_URL, GATEWAY_URL, … env overrides
```

Production backup, restore, and recovery procedures are in
[`deploy/OPERATIONS.md`](deploy/OPERATIONS.md).

---

## Service management

```bash
# Start / stop
docker compose up -d
docker compose down

# Rebuild after code changes (whole stack, or one service)
docker compose up -d --build
docker compose up -d --build build-service

# Restart a single service
docker compose restart build-service

# Tail logs
docker compose logs -f api-gateway
docker compose logs -f build-service

# Inspect infrastructure without publishing its port
docker compose exec postgres psql -U lumina -d lumina_ci -c "SELECT 1"
docker compose exec redis redis-cli ping
```

### Full reset (destroys all data)

```bash
# ⚠️ Deletes the database, object store, queues, and repo contents.
docker compose down -v
docker compose up -d --build
```

---

## Troubleshooting

### 502 Bad Gateway

```bash
docker compose ps                       # is everything Up/Healthy?
docker compose restart nginx api-gateway
docker compose logs api-gateway --tail 30
docker compose logs nginx      --tail 30
```

### A service won't start

```bash
docker compose logs build-service --tail 50
docker compose logs postgres      --tail 20
docker compose up -d --build      # rebuild images
```

Most startup failures are a missing required env var — the compose file marks
each one `:?…` so the error message names exactly what to set in `.env`.

### PostgreSQL connection errors

```bash
docker compose ps postgres            # must be healthy
docker compose restart postgres
docker compose exec postgres psql -U lumina -d lumina_ci -c "SELECT 1"
```

### Build stuck in `Building`

```bash
# List in-flight builds
curl -skb cookies.txt https://localhost/api/builds?page=1 \
  | jq '.data.builds[] | select(.status==2)'

# Inspect/cleanup stray build containers
docker ps --filter "name=rpm-build-"
docker rm -f <container-id>
```

### Webhook not firing

1. Confirm you're using the pipeline **id** (a GUID), not its name.
2. Confirm the webhook secret matches the pipeline's configured secret.
3. Check `docker compose logs build-service --tail 50`.

---

## Credentials & secrets

Lumina CI ships with **no default credentials**. Before first start, set every
required secret in `deploy/.env` (copy `deploy/.env.example`):

- `ADMIN_PASSWORD` — seeds the initial admin (created only when the users table
  is empty; without it the api-gateway refuses to start). `ADMIN_USERNAME`
  defaults to `admin`; `DEVELOPER_USERNAME` / `DEVELOPER_PASSWORD` seed an
  optional developer account only when the password is set.
- `JWT_SECRET` — signs access and refresh JWTs (min 32 chars).
- `SECRETS_MASTER_KEY` — AES-256-GCM master key for pipeline-secret encryption
  at rest. Generate a strong random value (e.g. `openssl rand -base64 48`) and
  **never rotate it** after secrets are written.
- `SECRETS_MASTER_KEY_FINGERPRINT_FILE` — mode-0600 file containing the
  SHA-256 fingerprint recorded before the first deployment. The production
  preflight rejects accidental master-key rotation.
- `GPG_PASSPHRASE_FILE` — path to the Docker-secret source file for the RPM signing key.
- `POSTGRES_PASSWORD`, `RABBITMQ_PASSWORD`, `MINIO_PASSWORD` (and `MINIO_USER`).

User passwords are stored in the database as BCrypt hashes. If a required
variable is missing, `docker compose up` fails with a clear error rather than
falling back to an insecure dev default.

> ⚠️ **For production, rotate every password in `.env` and keep `.env` out of
> version control.** TLS certificates under `deploy/nginx/certs/` must also be
> real (e.g. Let's Encrypt), not the self-signed dev pair.

Before every production deployment, run:

```bash
PRODUCTION_HOST=console.example.com \
EXPECTED_PUBLIC_IP=203.0.113.10 \
./scripts/production-preflight.sh
```

The preflight fails on weak or reused secrets, unsafe permissions, signing
secret errors, secrets-master-key rotation, invalid TLS, DNS mismatch, or
direct application-port publication.
