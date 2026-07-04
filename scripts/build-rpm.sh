#!/bin/bash
# build-rpm.sh — RPM build script for Lumina CI
# Runs inside the rpm-build container
#
# Environment variables:
#   SPEC_CONTENT       — .spec file content (base64 encoded)
#   SPEC_NAME          — Name of the spec file
#   SOURCE_URL         — URL to download source, or git://... for git clone
#   SOURCE_DIR         — Directory with pre-fetched sources (mounted by SourceService)
#   SPEC_PATH_IN_REPO  — Repo-relative path to the .spec, used to disambiguate
#                        when a repo contains more than one spec (FUNC-004).
#   ARTIFACTS_DIR      — Output directory for built RPMs
#   BUILD_JOB_ID       — Build job ID for tracking
#   AUTO_DOWNLOAD      — "true" to run spectool for missing sources (default: true)
#   GIT_USERNAME       — Username for private git repositories
#   GIT_TOKEN          — PAT / password for git auth

set -euo pipefail

SPEC_NAME="${SPEC_NAME:-package.spec}"
ARTIFACTS_DIR="${ARTIFACTS_DIR:-/artifacts}"
# Build tree is the rpmbuilder user's rpmbuild tree, created at image-build
# time by `rpmdev-setuptree`. Anchored to the fixed rpmbuilder home rather than
# $HOME because this entrypoint runs as root (so $HOME=/root), while the tree
# lives under /home/rpmbuilder (uid/gid 1000) — see rpm-build.Dockerfile.
RPMBUILDER_HOME="/home/rpmbuilder"
BUILD_DIR="${RPMBUILDER_HOME}/rpmbuild"
AUTO_DOWNLOAD="${AUTO_DOWNLOAD:-true}"

echo "=== Lumina CI RPM Build ==="
echo "Spec: ${SPEC_NAME}"
echo "Artifacts dir: ${ARTIFACTS_DIR}"
echo "Job ID: ${BUILD_JOB_ID:-N/A}"
echo "Auto-download sources: ${AUTO_DOWNLOAD}"
echo "============================"

# ─── Helper: get expected Source0 filename using spectool ───
get_source0_filename() {
    local expanded
    expanded=$(spectool -l -S "${BUILD_DIR}/SPECS/${SPEC_NAME}" 2>/dev/null \
        | grep "^Source0:" | head -1 | sed 's/^Source0:[[:space:]]*//' || true)
    if [ -n "$expanded" ]; then
        basename "$expanded"
    fi
}

# ─── Helper: get %setup -n dirname from spec using rpmspec ───
get_setup_dirname() {
    local setup_line
    setup_line=$(rpmspec -P "${BUILD_DIR}/SPECS/${SPEC_NAME}" 2>/dev/null \
        | grep -E '^%(setup|autosetup)' | head -1 || true)
    if [ -n "$setup_line" ]; then
        local n_value
        n_value=$(echo "$setup_line" | sed -n 's/.*-n[[:space:]]\+\([^[:space:]]\+\).*/\1/p' || true)
        if [ -n "$n_value" ]; then
            echo "$n_value"
            return
        fi
    fi

    # Default: Name-Version
    local pkg_name pkg_version
    pkg_name=$(rpmspec -P "${BUILD_DIR}/SPECS/${SPEC_NAME}" 2>/dev/null \
        | grep -i "^Name:" | head -1 | sed 's/^Name:[[:space:]]*//' | tr -d '[:space:]' || true)
    pkg_version=$(rpmspec -P "${BUILD_DIR}/SPECS/${SPEC_NAME}" 2>/dev/null \
        | grep -i "^Version:" | head -1 | sed 's/^Version:[[:space:]]*//' | tr -d '[:space:]' || true)
    if [ -n "$pkg_name" ] && [ -n "$pkg_version" ]; then
        echo "${pkg_name}-${pkg_version}"
    fi
}

# ─── Helper: resolve a single .spec file from a source tree ───
# Args: $1 = directory to search, $2 = search label for messages.
# Honors SPEC_PATH_IN_REPO (a repo-relative path) if set. When more than one
# spec is found and no SPEC_PATH_IN_REPO disambiguates, fails loudly with the
# candidate list (FUNC-004) instead of picking one nondeterministically.
# Echoes the resolved spec path (absolute) and returns 0 on success, 1 on
# ambiguous/missing.
resolve_spec_file() {
    local search_dir="$1"
    local label="$2"

    # Explicit path wins.
    if [ -n "${SPEC_PATH_IN_REPO:-}" ] && [ -f "${search_dir}/${SPEC_PATH_IN_REPO}" ]; then
        echo "${search_dir}/${SPEC_PATH_IN_REPO}"
        return 0
    fi

    local specs
    # Sort for a stable candidate listing if we have to fail.
    specs=$(find "${search_dir}" -maxdepth 3 -name "*.spec" -type f 2>/dev/null | sort || true)

    local count
    count=$(printf '%s\n' "${specs}" | grep -c . || true)

    if [ "${count}" -eq 1 ]; then
        printf '%s\n' "${specs}"
        return 0
    fi

    if [ "${count}" -eq 0 ]; then
        echo "ERROR: No .spec file found under ${label} (${search_dir})." >&2
        return 1
    fi

    # Ambiguous: fail with the candidate list rather than silently picking one.
    echo "ERROR: Multiple .spec files found under ${label}; set SPEC_PATH_IN_REPO to disambiguate:" >&2
    printf '  - %s\n' ${specs} >&2
    return 1
}

# ─── Helper: create tarball from a directory ───
# Args: $1 = source directory to archive, $2 = expected tarball filename
create_tarball() {
    local src_dir="$1"
    local tarball_name="$2"
    local tarball_stem

    # Determine the internal directory name
    local setup_dirname
    setup_dirname=$(get_setup_dirname)
    if [ -n "$setup_dirname" ]; then
        tarball_stem="$setup_dirname"
    else
        tarball_stem="${tarball_name%.tar.xz}"
        tarball_stem="${tarball_stem%.tar.gz}"
        tarball_stem="${tarball_stem%.tar.bz2}"
        tarball_stem="${tarball_stem%.tgz}"
        tarball_stem="${tarball_stem%.tar}"
    fi

    echo "  Creating tarball: ${tarball_name} (internal dir: ${tarball_stem})"

    local tmp_dir
    tmp_dir=$(mktemp -d)
    mkdir -p "${tmp_dir}/${tarball_stem}"
    cp -a "${src_dir}"/* "${tmp_dir}/${tarball_stem}/" 2>/dev/null || true
    # Also copy dotfiles (e.g. .gitmodules). Use the [!.] glob so the pattern
    # cannot expand to `.` or `..` (the classic `cp -a src/.*` footgun, which
    # recurses into the parent directory and pollutes the tarball).
    cp -a "${src_dir}"/.[!.]* "${tmp_dir}/${tarball_stem}/" 2>/dev/null || true

    local file_count
    file_count=$(find "${tmp_dir}/${tarball_stem}" -type f 2>/dev/null | wc -l)
    if [ "$file_count" -eq 0 ]; then
        echo "  WARNING: No files in tarball directory"
    fi

    case "${tarball_name}" in
        *.tar.xz)  tar -cJf "${BUILD_DIR}/SOURCES/${tarball_name}" -C "${tmp_dir}" "${tarball_stem}" ;;
        *.tar.bz2) tar -cjf "${BUILD_DIR}/SOURCES/${tarball_name}" -C "${tmp_dir}" "${tarball_stem}" ;;
        *.tar.gz|*.tgz) tar -czf "${BUILD_DIR}/SOURCES/${tarball_name}" -C "${tmp_dir}" "${tarball_stem}" ;;
        *)         tar -czf "${BUILD_DIR}/SOURCES/${tarball_name}" -C "${tmp_dir}" "${tarball_stem}" ;;
    esac
    rm -rf "${tmp_dir}"
    echo "  Tarball created: ${tarball_name} ($(stat -c%s "${BUILD_DIR}/SOURCES/${tarball_name}" 2>/dev/null || echo '?') bytes)"
}

# ═══════════════════════════════════════════════════════════
# Step 0: Spec file placement
# ═══════════════════════════════════════════════════════════
SPEC_DEFERRED=false

if [ -n "${SPEC_CONTENT:-}" ]; then
    echo "${SPEC_CONTENT}" | base64 -d > "${BUILD_DIR}/SPECS/${SPEC_NAME}"
    echo "Spec file decoded from SPEC_CONTENT"
elif [ -f "/specs/${SPEC_NAME}" ]; then
    cp "/specs/${SPEC_NAME}" "${BUILD_DIR}/SPECS/${SPEC_NAME}"
    echo "Spec file copied from /specs/"
elif [[ "${SOURCE_URL:-}" == git://* ]] || [[ "${SOURCE_URL:-}" == git+* ]]; then
    echo "Spec will be found after git clone..."
    SPEC_DEFERRED=true
elif [ -n "${SOURCE_DIR:-}" ]; then
    echo "Spec will be found from pre-fetched sources..."
    SPEC_DEFERRED=true
else
    echo "ERROR: No spec file provided!"
    exit 1
fi

if [ -f "${BUILD_DIR}/SPECS/${SPEC_NAME}" ]; then
    echo "--- spec content ---"
    cat "${BUILD_DIR}/SPECS/${SPEC_NAME}"
    echo "--- end spec ---"
else
    echo "--- spec will be available after source fetch ---"
fi

# Track whether we cloned a git repo (for deferred tarball creation)
CLONE_DIR=""
SOURCE_DIR_REPO=""

# ═══════════════════════════════════════════════════════════
# Step 1: Copy pre-mounted sources
# ═══════════════════════════════════════════════════════════

# 1a: Pre-fetched sources from SourceService
if [ -n "${SOURCE_DIR:-}" ] && [ -d "${SOURCE_DIR}" ]; then
    echo "=== Using pre-fetched sources from ${SOURCE_DIR} ==="

    if [ -d "${SOURCE_DIR}/repo/.git" ] || [ -d "${SOURCE_DIR}/.git" ]; then
        # Pre-fetched git repo — copy patches/sources, defer tarball
        REPO_DIR="${SOURCE_DIR}"
        [ -d "${SOURCE_DIR}/repo" ] && REPO_DIR="${SOURCE_DIR}/repo"
        echo "  Source is a git repository: ${REPO_DIR}"

        if $SPEC_DEFERRED; then
            if FOUND_SPEC=$(resolve_spec_file "${REPO_DIR}" "pre-fetched sources"); then
                cp "${FOUND_SPEC}" "${BUILD_DIR}/SPECS/${SPEC_NAME}"
                echo "  Found spec: ${FOUND_SPEC}"
                SPEC_DEFERRED=false
            else
                exit 1
            fi
        fi

        # Copy tarballs and patches from repo root
        find "${REPO_DIR}" -maxdepth 1 \( -name "*.tar.gz" -o -name "*.tar.bz2" -o -name "*.tar.xz" -o -name "*.patch" -o -name "*.diff" \) -exec cp {} "${BUILD_DIR}/SOURCES/" \;

        # Copy from standard source directories
        for srcdir in "${REPO_DIR}/sources" "${REPO_DIR}/SOURCES" "${REPO_DIR}/dist"; do
            if [ -d "$srcdir" ]; then
                cp "$srcdir"/* "${BUILD_DIR}/SOURCES/" 2>/dev/null || true
                echo "  Copied sources from ${srcdir}"
            fi
        done

        SOURCE_DIR_REPO="${REPO_DIR}"
    else
        # Plain files — copy directly
        echo "  Copying pre-fetched source files"
        cp -v "${SOURCE_DIR}"/* "${BUILD_DIR}/SOURCES/" 2>/dev/null || true

        # Handle extracted/ subdirectory
        if [ -d "${SOURCE_DIR}/extracted" ]; then
            echo "  Found extracted sources in ${SOURCE_DIR}/extracted"
            SOURCE_DIR_REPO="${SOURCE_DIR}/extracted"
        fi
    fi

    AUTO_DOWNLOAD="false"
    echo "Pre-fetched sources preparation completed"
fi

# 1b: Extra uploaded sources
if [ -d "/extra-sources" ]; then
    echo "=== Copying extra uploaded sources ==="

    copy_extra_sources() {
        local src_dir="$1"
        local label="$2"
        [ ! -d "$src_dir" ] && return 0

        local file_count
        file_count=$(find "$src_dir" -type f 2>/dev/null | wc -l)
        echo "  ${label}: found ${file_count} file(s)"

        [ "$file_count" -eq 0 ] && return 0

        # Copy preserving directory structure
        cp -rv "$src_dir"/* "${BUILD_DIR}/SOURCES/" 2>&1 || true

        # Flatten: copy files from subdirectories to SOURCES/ root
        while IFS= read -r -d '' file; do
            local base
            base=$(basename "$file")
            [ ! -f "${BUILD_DIR}/SOURCES/${base}" ] && cp -v "$file" "${BUILD_DIR}/SOURCES/${base}" 2>&1
        done < <(find "$src_dir" -mindepth 2 -type f -print0 2>/dev/null)
    }

    copy_extra_sources "/extra-sources/pipeline" "pipeline-level"
    copy_extra_sources "/extra-sources/build" "build-level"

    # Auto-extract uploaded archives
    EXTRACT_DIR=$(mktemp -d)
    for archive in "${BUILD_DIR}/SOURCES/"*.tar "${BUILD_DIR}/SOURCES/"*.tar.gz "${BUILD_DIR}/SOURCES/"*.tar.bz2 "${BUILD_DIR}/SOURCES/"*.tar.xz "${BUILD_DIR}/SOURCES/"*.tgz "${BUILD_DIR}/SOURCES/"*.zip; do
        [ -f "$archive" ] || continue
        echo "  Extracting: $(basename "$archive")"
        rm -rf "${EXTRACT_DIR:?}"/*
        case "$archive" in
            *.tar.gz|*.tgz)  tar -xzf "$archive" -C "$EXTRACT_DIR" 2>&1 || true ;;
            *.tar.bz2)       tar -xjf "$archive" -C "$EXTRACT_DIR" 2>&1 || true ;;
            *.tar.xz)        tar -xJf "$archive" -C "$EXTRACT_DIR" 2>&1 || true ;;
            *.tar)            tar -xf  "$archive" -C "$EXTRACT_DIR" 2>&1 || true ;;
            *.zip)            unzip -o -q "$archive" -d "$EXTRACT_DIR" 2>&1 || true ;;
        esac
        while IFS= read -r -d '' file; do
            dest_name=$(basename "$file")
            [ ! -f "${BUILD_DIR}/SOURCES/${dest_name}" ] && cp -v "$file" "${BUILD_DIR}/SOURCES/${dest_name}" 2>&1
        done < <(find "$EXTRACT_DIR" -type f -print0 2>/dev/null)
    done
    rm -rf "$EXTRACT_DIR"

    echo "Extra sources copy completed"
    find "${BUILD_DIR}/SOURCES" -type f | sort | head -50
fi

# ═══════════════════════════════════════════════════════════
# Step 2: SOURCE_URL handling (git clone or URL download)
# ═══════════════════════════════════════════════════════════
if [ -n "${SOURCE_URL:-}" ]; then
    if [[ "${SOURCE_URL}" == git://* ]]; then
        echo "=== Git Clone Source ==="
        GIT_FULL="${SOURCE_URL#git://}"
        GIT_REPO="${GIT_FULL%%#*}"
        GIT_PARAMS="${GIT_FULL#*#}"

        GIT_BRANCH="main"
        SPEC_PATH_IN_REPO=""
        GIT_COMMIT=""

        IFS='&' read -ra PARAMS <<< "${GIT_PARAMS}"
        for param in "${PARAMS[@]}"; do
            KEY="${param%%=*}"
            VALUE="${param#*=}"
            case "$KEY" in
                branch) GIT_BRANCH="$VALUE" ;;
                specPath) SPEC_PATH_IN_REPO="$VALUE" ;;
                commit) GIT_COMMIT="$VALUE" ;;
            esac
        done

        echo "Cloning: ${GIT_REPO} (branch: ${GIT_BRANCH}, commit: ${GIT_COMMIT:-latest})"

        # Inject credentials for private repos
        AUTH_REPO="${GIT_REPO}"
        if [ -n "${GIT_USERNAME:-}" ] && [ -n "${GIT_TOKEN:-}" ]; then
            if [[ "${GIT_REPO}" =~ ^https://([^/]+)(/.*)$ ]]; then
                AUTH_REPO="https://${GIT_USERNAME}:${GIT_TOKEN}@${BASH_REMATCH[1]}${BASH_REMATCH[2]}"
                echo "Using authenticated git URL"
            elif [[ "${GIT_REPO}" =~ ^http://([^/]+)(/.*)$ ]]; then
                AUTH_REPO="http://${GIT_USERNAME}:${GIT_TOKEN}@${BASH_REMATCH[1]}${BASH_REMATCH[2]}"
                echo "Using authenticated git URL"
            else
                echo "Warning: Cannot inject credentials for non-HTTPS URL"
            fi
        fi

        CLONE_DIR=$(mktemp -d)
        git clone --depth 1000 --no-single-branch --branch "${GIT_BRANCH}" "${AUTH_REPO}" "${CLONE_DIR}/repo" || {
            echo "ERROR: git clone failed"
            exit 1
        }

        if [ -n "${GIT_COMMIT}" ]; then
            echo "Checking out commit: ${GIT_COMMIT}"
            cd "${CLONE_DIR}/repo"
            if ! git checkout "${GIT_COMMIT}" 2>&1; then
                echo "Commit not in shallow clone, fetching more history..."
                git fetch --unshallow 2>&1 || git fetch --depth=5000 2>&1 || true
                git checkout "${GIT_COMMIT}" 2>&1 || {
                    echo "ERROR: Could not checkout commit ${GIT_COMMIT}"
                    exit 1
                }
            fi
            cd /
        fi

        # Submodules
        if [ -f "${CLONE_DIR}/repo/.gitmodules" ]; then
            echo "Initializing git submodules..."
            cd "${CLONE_DIR}/repo"
            # FUNC-005: a failed submodule init (auth, missing repo, network)
            # breaks the build; fatal with stderr preserved rather than
            # silently warning and continuing.
            if ! git submodule update --init --recursive; then
                echo "ERROR: git submodule update failed — see stderr above."
                exit 1
            fi
            cd /
        fi

        # Find and copy spec file. SPEC_PATH_IN_REPO is honored first; an
        # ambiguous auto-discovery fails loudly with the candidate list
        # (FUNC-004) rather than picking one nondeterministically.
        if FOUND_SPEC=$(resolve_spec_file "${CLONE_DIR}/repo" "cloned repository"); then
            cp "${FOUND_SPEC}" "${BUILD_DIR}/SPECS/${SPEC_NAME}"
            echo "Spec file: ${FOUND_SPEC}"
            SPEC_DEFERRED=false
        else
            exit 1
        fi

        # Copy tarballs and patches from repo to SOURCES/
        find "${CLONE_DIR}/repo" -maxdepth 1 \( -name "*.tar.gz" -o -name "*.tar.bz2" -o -name "*.tar.xz" -o -name "*.patch" -o -name "*.diff" \) -exec cp {} "${BUILD_DIR}/SOURCES/" \;

        for srcdir in "${CLONE_DIR}/repo/sources" "${CLONE_DIR}/repo/SOURCES" "${CLONE_DIR}/repo/dist"; do
            if [ -d "$srcdir" ]; then
                cp "$srcdir"/* "${BUILD_DIR}/SOURCES/" 2>/dev/null || true
                echo "Copied sources from ${srcdir}"
            fi
        done

        echo "Git clone source preparation completed"
        # NOTE: tarball from repo is created in Step 4, AFTER spectool has a chance to download Source0
    else
        echo "=== Downloading source from URL ==="
        curl -L -f --connect-timeout 30 --max-time 300 -o "${BUILD_DIR}/SOURCES/$(basename "${SOURCE_URL}")" "${SOURCE_URL}" || {
            echo "WARNING: Failed to download source from ${SOURCE_URL}"
        }
    fi
fi

# ═══════════════════════════════════════════════════════════
# Step 3: Spectool auto-download missing sources
# ═══════════════════════════════════════════════════════════
if [ ! -f "${BUILD_DIR}/SPECS/${SPEC_NAME}" ]; then
    echo "ERROR: No spec file available after source fetching"
    exit 1
fi

if [ "${AUTO_DOWNLOAD}" = "true" ]; then
    echo "=== Auto-downloading sources via spectool ==="
    # FUNC-005: a source-fetch failure (404, checksum mismatch, network) makes
    # the later rpmbuild fail with a confusing unrelated error, so it is fatal
    # with stderr preserved instead of being downgraded to a warning.
    if ! spectool -g -R -a "${BUILD_DIR}/SPECS/${SPEC_NAME}"; then
        echo "ERROR: spectool failed to download sources — see stderr above."
        exit 1
    fi
else
    echo "Skipping auto-download (AUTO_DOWNLOAD=false)"
fi

# ═══════════════════════════════════════════════════════════
# Step 4: Create tarball from git repo (only if Source0 is still missing)
# ═══════════════════════════════════════════════════════════
SOURCE0_FILENAME=$(get_source0_filename)

if [ -n "${SOURCE0_FILENAME}" ] && [ ! -f "${BUILD_DIR}/SOURCES/${SOURCE0_FILENAME}" ]; then
    # Try creating tarball from git clone
    if [ -n "${CLONE_DIR}" ] && [ -d "${CLONE_DIR}/repo" ]; then
        echo "Source0 (${SOURCE0_FILENAME}) not found after spectool — creating tarball from git repo"
        create_tarball "${CLONE_DIR}/repo" "${SOURCE0_FILENAME}"
    # Try creating tarball from pre-fetched SOURCE_DIR repo
    elif [ -n "${SOURCE_DIR_REPO}" ]; then
        echo "Source0 (${SOURCE0_FILENAME}) not found — creating tarball from pre-fetched sources"
        create_tarball "${SOURCE_DIR_REPO}" "${SOURCE0_FILENAME}"
    fi
fi

# Clean up git clone
if [ -n "${CLONE_DIR}" ]; then
    rm -rf "${CLONE_DIR}"
fi

# ═══════════════════════════════════════════════════════════
# Step 5: Verify Source0 exists
# ═══════════════════════════════════════════════════════════
echo "=== Verifying sources ==="
SOURCE0_FILENAME=$(get_source0_filename)

if [ -n "${SOURCE0_FILENAME}" ]; then
    if [ -f "${BUILD_DIR}/SOURCES/${SOURCE0_FILENAME}" ]; then
        echo "Source0 found: ${SOURCE0_FILENAME} ($(stat -c%s "${BUILD_DIR}/SOURCES/${SOURCE0_FILENAME}" 2>/dev/null || echo '?') bytes)"
    else
        echo "ERROR: Source0 '${SOURCE0_FILENAME}' not found in SOURCES/"
        echo "SOURCES/ contents:"
        ls -la "${BUILD_DIR}/SOURCES/"
        exit 1
    fi
else
    echo "No Source0 in spec (build may not require sources)"
fi

echo "SOURCES/ contents:"
ls -la "${BUILD_DIR}/SOURCES/"

# ═══════════════════════════════════════════════════════════
# Step 6: Build
# ═══════════════════════════════════════════════════════════
# PRIVILEGE SPLIT (FUNC-002): this script runs as root so that dnf builddep can
# install build dependencies into the writable overlay (/usr/lib, /var/lib/rpm
# are not writable by uid 1000). The dependency install stays root; the
# untrusted %build/%install shell in the spec then runs as the `rpmbuilder`
# user via the builder_phase drop below.
#
# The drop uses `setpriv` (direct syscalls), not su/sudo/runuser: the build
# container is launched with the per-container `no-new-privileges` security opt,
# which neutralizes setuid binaries, so a syscall-based drop is the only option.

# builder_phase: everything that runs as the unprivileged rpmbuilder user — the
# rpmbuild step (which executes spec-supplied %build/%install shell) and the
# artifact copy into /artifacts (owned by uid 1000).
builder_phase() {
    echo "Building RPM (as uid $(id -u))..."
    rpmbuild -bb "${BUILD_DIR}/SPECS/${SPEC_NAME}" \
        --define "_topdir ${BUILD_DIR}" \
        --define "debug_package %{nil}" \
        2>&1 | tee /tmp/build.log
    local rpmbuild_exit=${PIPESTATUS[0]}

    if [ "${rpmbuild_exit}" -ne 0 ]; then
        echo "ERROR: RPM build failed with exit code ${rpmbuild_exit}!"
        return "${rpmbuild_exit}"
    fi

    # Collect binary RPMs. -bb produces RPMS/<arch>/*.rpm only (no SRPMS); the
    # RPMS/*/*.rpm glob gathers every arch subpackage (FUNC-009: the previous
    # SRPMS copy was dead code under -bb and is removed).
    mkdir -p "${ARTIFACTS_DIR}"
    cp -v "${BUILD_DIR}"/RPMS/*/*.rpm "${ARTIFACTS_DIR}/" || {
        echo "ERROR: rpmbuild reported success but no RPMs were found under ${BUILD_DIR}/RPMS/"
        return 1
    }
    return 0
}

echo "Installing build dependencies (as root)..."
# FUNC-002: builddep failure is fatal with stderr preserved. The previous
# `2>/dev/null || echo "Warning..."` discarded the real error and downgraded a
# hard failure to a hint, so operators chased downstream rpmbuild errors
# instead of the missing-deps root cause.
if ! dnf builddep -y "${BUILD_DIR}/SPECS/${SPEC_NAME}"; then
    echo "ERROR: dnf builddep failed — see stderr above for the unresolvable/missing dependencies."
    exit 1
fi

# Hand the build tree to the unprivileged user so %build/%install can write to it.
chown -R rpmbuilder:rpmbuilder "${BUILD_DIR}"

# Run the untrusted build phase as rpmbuilder (uid/gid 1000).
export -f builder_phase
export BUILD_DIR SPEC_NAME ARTIFACTS_DIR
setpriv --reuid 1000 --regid 1000 --clear-groups -- bash -c 'builder_phase'
BUILD_EXIT=$?

if [ ${BUILD_EXIT} -ne 0 ]; then
    exit ${BUILD_EXIT}
fi

echo "=== Build completed successfully ==="
echo "Artifacts:"
ls -la "${ARTIFACTS_DIR}/"
