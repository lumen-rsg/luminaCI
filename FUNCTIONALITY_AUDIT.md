# Lumina CI — Functionality Audit Report

**Date:** 2026-07-04
**Scope:** RPM build pipeline — `scripts/build-rpm.sh`, `deploy/docker/rpm-build.Dockerfile`, and the downstream "load" path (`DockerBuildService` → `ScannerService` → `MinioStorageService` / `RepositoryManagerService`).
**Auditor:** Automated functionality audit
**Trigger:** Concern that `build-rpm.sh` is fragile and that multi-output specs (subpackages such as `-devel`, `noarch` data, `systemd` units) "fail to load."

---

## Executive Summary

The concern is well-founded, but the failure mode is **not** where it was suspected.
`rpmbuild -bb` itself produces subpackages correctly, and the artifact-collection
glob copies every `RPMS/*/*.rpm` to `/artifacts`. The breakage is upstream and
downstream of that:

- The build image **cannot build any real spec** — it ships no toolchain and runs
  as a user that cannot install build dependencies. The only specs that survive are
  no-compile, no-`BuildRequires` fixtures (the two in `Test/`).
- The repository publish path **misroutes subpackages by architecture and destroys
  their filenames**, so a multi-arch / multi-subpackage build cannot be correctly
  loaded into a single repository.

Identified **9 functionality defects**:

- 🔴 **2 Critical** — the build image can't build real specs; builddep failure is silenced
- 🟠 **2 High** — subpackage arch misrouting on publish; nondeterministic spec selection
- 🟡 **3 Medium** — swallowed failures hide root causes; noarch subpackages land in wrong repodata
- 🟢 **2 Low** — dead `ARTIFACTS_JSON`; `SRPMS` copy that can never produce output

Findings are labeled `FUNC-NNN` to distinguish them from the closed `SEC-*` / `CVE-*` set.

---

## 🔴 CRITICAL Findings

### FUNC-001: Build image ships no compiler / toolchain — only script-only specs can build

- **File:** `deploy/docker/rpm-build.Dockerfile:11-24`
- **Symptom:** Any spec whose `%build` actually compiles (C/C++ via `gcc`/`make`, C# via
  `dotnet`, autotools, rust, go…) fails with "command not found."
- **Detail:** The image installs only:
  `rpm-build, rpmdevtools, curl, wget, git-core, rsync, subversion, mercurial, tar, gzip, bzip2, xz`.
  There is no `gcc`, `make`, `cmake`, `autoconf`/`automake`, `dotnet`, `rustc`, or `go`.
  The project's own `Test/aurora.spec` runs `dotnet publish Aurora.CLI/Aurora.CLI.csproj`
  in `%build` — it **cannot succeed** in this image. The two test fixtures (`aurora.spec`,
  `test_package.spec`) both have **zero** `BuildRequires`, which is why the pipeline has
  ever appeared green: nothing in the test path exercises a real toolchain.
- **Impact:** The service is, in practice, a noarch-script-package builder. The flagship
  example spec in the repo cannot build. Anything users bring that compiles will fail at
  `%build` with a misleading error, not at image setup.
- **Fix:** Either (a) install a base build toolchain in the image (`gcc gcc-c++ make cmake
  autoconf automake libtool` plus language runtimes as needed), or (b) make the image a
  *base* and require pipelines to supply a `buildImage` that carries their toolchain
  (the `buildImage` parameter already exists in `DockerBuildService.StartBuildAsync` —
  document it as required for compiling specs and stop advertising the stock image as
  general-purpose). Add at least one compiling test fixture to CI so this regresses loudly.

### FUNC-002: `dnf builddep` runs as non-root and is silently forced to "warning"

- **Files:** `scripts/build-rpm.sh:424` (invocation), `deploy/docker/rpm-build.Dockerfile:33`
  (`USER rpmbuilder`, uid 1000), and the misleading comment at `build-rpm.sh:422-423`.
- **Symptom:** Specs with `BuildRequires:` never get those dependencies installed; the
  subsequent `rpmbuild` fails with a downstream error that does not mention builddep.
- **Detail:** `dnf builddep` installs packages into the **system filesystem**
  (`/usr/lib`, `/usr/include`, …) and writes the rpmdb at `/var/lib/rpm` — none of which
  are writable by uid 1000. The image sets `USER rpmbuilder` and intentionally ships no
  `sudo`. The script's own comment claims *"dnf builddep installs into the container's own
  writable rpmdb"* — this is incorrect: builddep's whole purpose is to install the deps the
  spec asks for, not merely touch the rpmdb. The invocation then doubles down:
  ```bash
  dnf builddep -y "${BUILD_DIR}/SPECS/${SPEC_NAME}" 2>/dev/null || echo "Warning: Some build dependencies may be missing"
  ```
  `2>/dev/null` discards the real error and `|| echo` downgrades a hard failure to a hint.
  So `set -euo pipefail` is defeated at exactly the step that prepares the build environment.
- **Impact:** Combined with FUNC-001, this guarantees no real spec builds. Worse, the silent
  downgrade means operators chase `rpmbuild` errors instead of the missing-deps root cause.
- **Fix:** Run dependency installation as root at image-**build** time for the base
  toolchain, and run per-spec `dnf builddep` from a root context that is still inside the
  untrusted container (the container is already sandboxed via the dropped caps / isolated
  network / non-root *host* mapping via `userns-remap` — see `daemon.json`). Concretely:
  drop `USER rpmbuilder` from the Dockerfile for the install phase, run `dnf builddep` as
  root, then drop privileges (`su rpmbuilder -c` or `runuser`) for the `rpmbuild` itself,
  which is the part that runs spec-supplied shell. And **stop swallowing the exit code** —
  if builddep fails, the build must fail with that reason.

---

## 🟠 HIGH Findings

### FUNC-003: Subpackages are misrouted by architecture on publish

- **File:** `src/Services/Lumina.RepositoryService/Services/MinioStorageService.cs:141, 163, 182, 198`
- **Symptom:** A build that produces e.g. `foo-1.0-1.x86_64.rpm` + `foo-data-1.0-1.noarch.rpm`
  + `foo-devel-1.0-1.noarch.rpm` cannot be correctly loaded into one repository; the
  noarch subpackages are written into the parent arch's directory and only that arch's
  repodata is regenerated.
- **Detail:** `PublishPackageAsync` resolves the destination from the **repository's**
  configured arch, not the **package's** arch:
  ```csharp
  // line 141 — writes BEFORE metadata is known, using repo.Arch
  savedPath = _repoManager.SaveRpm(repo.BasePath, repo.Arch, fileName, rpmData);
  ...
  // line 163 — the real arch is recovered from the RPM header...
  pkgArch = metadata.Arch;
  ...
  // line 182 — ...but StoragePath is still hardcoded to repo.Arch
  StoragePath = $"{repo.BasePath}/{repo.Arch}/{fileName}",
  ...
  // line 198 — and createrepo_c runs only on repo.Arch
  var result = await _repoManager.RunCreaterepoAsync(repo.BasePath, repo.Arch);
  ```
  So the DB `Package.Arch` column is correct, but the file on disk, the recorded
  `StoragePath`, and the generated `repodata/` all declare the wrong arch. `dnf install
  foo-data` on an aarch64 client queries `aarch64/repodata` and will not find a noarch
  subpackage that was shoved into `x86_64/`.
- **Impact:** This is the concrete instantiation of "split packages fail to load." Multi-
  output specs (the systemd-style case in the original concern: main package + `-devel` +
  noarch data + units) are unloadable as a coherent set.
- **Fix:** Extract metadata **first**, then route by the package's own arch:
  save to `repo.BasePath/<pkgArch>/`, set `StoragePath` from `pkgArch`, and run
  `createrepo_c` on `repo.BasePath/<pkgArch>`. If the repository is single-arch by policy,
  reject artifacts whose real arch doesn't match rather than silently misfiling them.

### FUNC-004: Nondeterministic spec selection when a repo contains multiple specs

- **Files:** `scripts/build-rpm.sh:162` (pre-fetched), `scripts/build-rpm.sh:326` (git clone)
- **Symptom:** Building a repo that legitimately contains more than one `.spec` picks an
  arbitrary one; re-runs can pick a different one.
- **Detail:** Both auto-discovery paths use:
  ```bash
  FOUND_SPEC=$(find "${REPO_DIR}" -maxdepth 3 -name "*.spec" -type f | head -1)
  ```
  `find`'s order is filesystem-dependent and unstable across clone/checkouts. A multi-
  package monorepo (very common for the "several packages in one repo" pattern) will build
  whichever spec happens to sort first — and `SPEC_PATH_IN_REPO` is only honored for the
  git path, not for `SOURCE_DIR`.
- **Impact:** Wrong package built; results look flaky across hosts.
- **Fix:** When more than one spec is found and no explicit `SPEC_PATH_IN_REPO` /
  `SPEC_NAME` disambiguates, **fail loudly** with the list of candidates instead of
  silently picking one. Apply the same `SPEC_PATH_IN_REPO` resolution to the `SOURCE_DIR`
  branch that the git branch already has.

---

## 🟡 MEDIUM Findings

### FUNC-005: Critical failures are downgraded to warnings throughout the script

- **File:** `scripts/build-rpm.sh` — `dnf builddep` (424), `spectool -g` (367), `git submodule update` (316), multiple `cp … 2>/dev/null || true`.
- **Symptom:** `set -euo pipefail` gives the impression of fail-fast, but every step that
  materially affects buildability is wrapped in `|| echo "Warning…"` or `|| true`.
- **Detail:** The script is fail-fast in declaration and fail-quiet in practice at exactly
  the steps (dependency install, source fetch, submodule init) whose failure makes the
  later `rpmbuild` fail with a confusing, unrelated error. The result is the fragility the
  original report flagged: builds fail for "mysterious" reasons because the real cause was
  printed as a warning (or to `/dev/null`) ten screens earlier.
- **Impact:** Poor diagnosability; operators cannot trust the exit reason.
- **Fix:** Distinguish hard failures from soft ones explicitly. Source/submodule/builddep
  failures should be fatal with the original stderr preserved in the build log. Reserve
  `|| true` for genuinely optional copies (e.g. "copy dotfiles if any exist").

### FUNC-006: `noarch` and cross-arch subpackages are invisible to per-arch repodata

- **File:** `src/Services/Lumina.RepositoryService/Services/RepositoryManagerService.cs:164-228` (`RunCreaterepoAsync`), `MinioStorageService.cs:198`
- **Symptom:** Even if FUNC-003 is fixed at the file-placement level, the repository model
  assumes one arch per repo and runs `createrepo_c` only on that one arch dir.
- **Detail:** `createrepo_c` is invoked per-publish on `repo.BasePath/repo.Arch`. There is
  no notion of regenerating `noarch/repodata` (where noarch subpackages belong for a
  multi-arch repo) or of refreshing all arches after a multi-RPM publish. `SyncAllArchAsync`
  exists but is a separate manual `sync` endpoint, not part of the publish flow.
- **Impact:** `dnf` on any client cannot resolve a full subpackage set because the noarch
  pieces are not in any `noarch/repodata` the client consults.
- **Fix:** After publishing one or more RPMs, regenerate metadata for **each** arch dir
  that received a file (or call `SyncAllArchAsync` as the final publish step). Model a
  repository as multi-arch rather than carrying a single `Arch` column.

### FUNC-007: Artifact filename is destroyed on publish, losing NEVRA from the on-disk name

- **File:** `src/Services/Lumina.RepositoryService/Services/MinioStorageService.cs:117, 136, 168`
- **Symptom:** Built `foo-1.0-1.x86_64.rpm` becomes `package-<guid>.rpm` in the repository
  filesystem; only the DB record keeps the real name.
- **Detail:** The object name in MinIO is `$"{artifactId:N}.rpm"` and the published filename
  is `$"package-{artifactId:N}.rpm"`. The real filename is recovered only inside the
  metadata-extraction branch (`fileName = Path.GetFileName(savedPath)`) — but `savedPath`
  was itself produced from the renamed file, so this "recovery" just re-reads the placeholder.
  `createrepo_c` reads RPM headers, so the repo technically works, but every diagnostic,
  mirror listing, and `ls` of the repo root is opaque.
- **Impact:** Operability and debugging suffer; any tool that keys off filename (rather than
  RPM header) breaks.
- **Fix:** Preserve the original `BuildArtifact.FileName` through the publish path; name the
  MinIO object and the saved file from the artifact's real filename (validated against path
  traversal), not from the guid.

---

## 🟢 LOW Findings

### FUNC-008: `ARTIFACTS_JSON` block is dead code

- **File:** `scripts/build-rpm.sh:451-468`
- **Detail:** The script computes a per-file `{fileName, fileSize, hashSha256}` JSON blob and
  echoes it under an `=== ARTIFACTS_JSON ===` marker. No consumer parses container stdout
  for it — `DockerBuildService.ScanArtifactsAsync` (`DockerBuildService.cs:820`) discovers
  artifacts by scanning the filesystem and recomputes the SHA-256 itself. The blob (and its
  redundant hash work) is discarded.
- **Fix:** Delete the block, or — if a structured manifest is desired — write it to a file
  in `/artifacts/` and have `ScanArtifactsAsync` consume it to avoid the double hash.

### FUNC-009: `SRPMS` copy can never succeed

- **File:** `scripts/build-rpm.sh:427` (`rpmbuild -bb`), `scripts/build-rpm.sh:445` (`cp …/SRPMS/*.rpm`)
- **Detail:** `-bb` means "build binary only" and produces no source RPMs, so the SRPMS copy
  at line 445 always matches nothing and no-ops via `|| true`. If source RPMs are intended
  to be artifacts, switch to `-ba`; if not, remove the dead copy to avoid implying they are.
- **Fix:** Pick one: `rpmbuild -ba` (and keep the copy) or drop the SRPMS copy.

---

## Notes — things checked and found NOT to be bugs

- **`cp -a "${src_dir}"/.*` in `create_tarball` (line 95).** POSIX expands `.*` to include
  `.` and `..`, which is a classic footgun. Verified empirically on this build host: GNU
  `cp -a` refuses to recurse into `..`, so only the real dotfiles (`.gitmodules`, etc.) are
  copied. Latent and implementation-dependent, but not currently fatal. The defensive form
  `cp -a "${src_dir}"/.[!.]* "${tmp_dir}/${tarball_stem}/` is still recommended.
- **Subpackage *production* by `rpmbuild`.** This part is fine. `rpmbuild -bb` emits every
  `%package` subpackage into `RPMS/<arch>/`, the collection glob `RPMS/*/*.rpm` gathers all
  arches, and `ScanArtifactsAsync` registers each `.rpm` as its own `BuildArtifact`. The
  user's specific worry — "split packages inside one spec will fail to load" — is confirmed,
  but the failure is at publish time (FUNC-003/006/007), not at build time.
- **`spectool`/`rpmspec` macro expansion in the helpers** (`get_source0_filename`,
  `get_setup_dirname`) handles both URL and `%{name}-%{version}` `Source0` forms and falls
  back to `Name-Version` when `%setup -n` is absent. Correct.

---

## Recommended fix order

1. **FUNC-002** then **FUNC-001** — make the image actually able to build a real spec, and
   add a compiling test fixture. Nothing else can be honestly validated until this is true.
2. **FUNC-005** — stop masking the failure modes; this makes 1 and everything after diagnosable.
3. **FUNC-003 / FUNC-006 / FUNC-007** — fix the publish path so multi-output specs load.
4. **FUNC-004** — kill the nondeterministic spec pick.
5. **FUNC-008 / FUNC-009** — dead-code cleanup.
