#!/bin/bash
# build-rpm.sh — RPM build script for Lumina CI
# Runs inside the rpm-build container
#
# Environment variables:
#   SPEC_CONTENT       — .spec file content (base64 encoded)
#   SPEC_NAME          — Name of the spec file
#   SOURCE_URL         — Credential-free URL to download, or git://... for clone
#   SOURCE_DIR         — Directory with pre-fetched sources (mounted by SourceService)
#   SPEC_PATH_IN_REPO  — Repo-relative path to the .spec, used to disambiguate
#                        when a repo contains more than one spec (FUNC-004).
#   ARTIFACTS_DIR      — Output directory for built RPMs
#   BUILD_JOB_ID       — Build job ID for tracking
#   AUTO_DOWNLOAD      — "true" to run spectool as uid 1000 (default: true)
#   TARGET_DISTRIBUTION — Reviewed target distribution (fedora)
#   TARGET_RELEASE      — Reviewed distribution release (44)
#   TARGET_ARCHITECTURE — Native RPM architecture (x86_64 or aarch64)
#   BUILD_PROFILE       — Canonical profile (for example fedora-44-aarch64)

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
TARGET_DISTRIBUTION="${TARGET_DISTRIBUTION:?TARGET_DISTRIBUTION is required}"
TARGET_RELEASE="${TARGET_RELEASE:?TARGET_RELEASE is required}"
TARGET_ARCHITECTURE="${TARGET_ARCHITECTURE:?TARGET_ARCHITECTURE is required}"
BUILD_PROFILE="${BUILD_PROFILE:?BUILD_PROFILE is required}"

runner_distribution=$(. /etc/os-release && printf '%s' "${ID:-unknown}")
runner_release=$(. /etc/os-release && printf '%s' "${VERSION_ID:-unknown}")
runner_architecture=$(rpm --eval '%{_target_cpu}')
expected_profile="${TARGET_DISTRIBUTION}-${TARGET_RELEASE}-${TARGET_ARCHITECTURE}"
if [ "${runner_distribution}" != "${TARGET_DISTRIBUTION}" ] \
    || [ "${runner_release}" != "${TARGET_RELEASE}" ] \
    || [ "${runner_architecture}" != "${TARGET_ARCHITECTURE}" ] \
    || [ "${BUILD_PROFILE}" != "${expected_profile}" ]; then
    echo "ERROR: runner target mismatch"
    echo "Requested: ${BUILD_PROFILE}"
    echo "Runner: ${runner_distribution}-${runner_release}-${runner_architecture}"
    exit 1
fi

# Credentials belong in a trusted fetcher which mounts an immutable snapshot at
# SOURCE_DIR. They must never be visible to spec macro expansion or scriptlets.
if [ -n "${GIT_USERNAME:-}" ] || [ -n "${GIT_TOKEN:-}" ]; then
    echo "ERROR: Git credentials are forbidden in the RPM build container; mount a credential-free source snapshot via SOURCE_DIR." >&2
    exit 1
fi
if [[ "${SOURCE_URL:-}" =~ ^https?://[^/@]+@ ]] \
    || [[ "${SOURCE_URL:-}" =~ ^git://https?://[^/@]+@ ]]; then
    echo "ERROR: Source URLs containing credentials are forbidden in the RPM build container." >&2
    exit 1
fi
unset GIT_USERNAME GIT_TOKEN

echo "=== Lumina CI RPM Build ==="
echo "Spec: ${SPEC_NAME}"
echo "Artifacts dir: ${ARTIFACTS_DIR}"
echo "Job ID: ${BUILD_JOB_ID:-N/A}"
echo "Target profile: ${BUILD_PROFILE}"
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

# Copy regular files shipped beside a nested spec into SOURCES. Monorepos
# commonly keep Source/Patch inputs in the package directory or in a child
# files/, sources/, SOURCES/, or dist/ directory. Flattening matches rpmbuild's
# SOURCES lookup while duplicate basenames fail closed.
copy_companion_sources() {
    local spec_file="$1"
    local spec_dir
    spec_dir=$(dirname "$spec_file")

    while IFS= read -r -d '' file; do
        [ "$file" = "$spec_file" ] && continue
        local base destination
        base=$(basename "$file")
        destination="${BUILD_DIR}/SOURCES/${base}"
        if [ -e "$destination" ]; then
            if ! cmp -s "$file" "$destination"; then
                echo "ERROR: duplicate companion source basename with different content: ${base}" >&2
                return 1
            fi
            continue
        fi
        cp "$file" "$destination"
        echo "  Copied companion source: ${file}"
    done < <(
        find "$spec_dir" -maxdepth 1 -type f -print0
        for child in files sources SOURCES dist; do
            [ -d "${spec_dir}/${child}" ] && find "${spec_dir}/${child}" -type f -print0
        done
    )
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

preparation_phase() {
# Everything in this function handles or expands attacker-controlled input. The
# caller invokes it only after dropping to uid 1000.

# ═══════════════════════════════════════════════════════════
# Step 0: Spec file placement (unprivileged)
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
                copy_companion_sources "${FOUND_SPEC}"
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

        CLONE_DIR=$(mktemp -d)
        git clone --depth 1000 --no-single-branch --branch "${GIT_BRANCH}" "${GIT_REPO}" "${CLONE_DIR}/repo" || {
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
            copy_companion_sources "${FOUND_SPEC}"
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

echo "Creating source RPM from untrusted spec (as uid $(id -u))..."
rpmbuild -bs "${BUILD_DIR}/SPECS/${SPEC_NAME}" \
    --target "${TARGET_ARCHITECTURE}" \
    --define "_topdir ${BUILD_DIR}" \
    2>&1 | tee /tmp/build.log
rpmbuild_exit=${PIPESTATUS[0]}
if [ "${rpmbuild_exit}" -ne 0 ]; then
    echo "ERROR: source RPM creation failed with exit code ${rpmbuild_exit}!"
    return "${rpmbuild_exit}"
fi

shopt -s nullglob
source_rpms=("${BUILD_DIR}"/SRPMS/*.src.rpm)
if [ "${#source_rpms[@]}" -ne 1 ]; then
    echo "ERROR: expected exactly one source RPM, found ${#source_rpms[@]}"
    return 1
fi
printf '%s\n' "${source_rpms[0]}" > /tmp/lumina-source-rpm-path
}

# ═══════════════════════════════════════════════════════════
# Step 6: Install dependencies and rebuild the prepared SRPM
# ═══════════════════════════════════════════════════════════
# builder_phase rebuilds the prepared SRPM as uid 1000.
builder_phase() {
    local rebuild_dir="${RPMBUILDER_HOME}/rebuild"
    local rpmbuild_exit
    local source_rpm="${SOURCE_RPM:?SOURCE_RPM is required}"

    rm -rf "${rebuild_dir}"
    mkdir -p "${rebuild_dir}"/{BUILD,BUILDROOT,RPMS,SOURCES,SPECS,SRPMS}
    echo "Rebuilding immutable source RPM in clean topdir: $(basename "${source_rpm}")"
    rpmbuild --rebuild "${source_rpm}" \
        --target "${TARGET_ARCHITECTURE}" \
        --define "_topdir ${rebuild_dir}" \
        2>&1 | tee -a /tmp/build.log
    rpmbuild_exit=${PIPESTATUS[0]}
    if [ "${rpmbuild_exit}" -ne 0 ]; then
        echo "ERROR: source RPM rebuild failed with exit code ${rpmbuild_exit}!"
        return "${rpmbuild_exit}"
    fi

    mkdir -p "${ARTIFACTS_DIR}"
    cp -v "${source_rpm}" "${ARTIFACTS_DIR}/"
    cp -v "${rebuild_dir}"/RPMS/*/*.rpm "${ARTIFACTS_DIR}/" || {
        echo "ERROR: SRPM rebuild succeeded but produced no binary RPMs"
        return 1
    }

    # Retain the evidence needed to explain and independently reproduce the
    # build. Manifests deliberately exclude source URLs/credentials.
    (
        cd "${BUILD_DIR}"
        find SOURCES SPECS -type f -print0 \
            | sort -z \
            | xargs -0 sha256sum
    ) > "${ARTIFACTS_DIR}/build-inputs.sha256"
    rpm -qa --qf '%{NAME}\t%{EPOCHNUM}:%{VERSION}-%{RELEASE}.%{ARCH}\n' \
        | sort > "${ARTIFACTS_DIR}/installed-packages.txt"
    (
        cd "${ARTIFACTS_DIR}"
        sha256sum -- ./*.rpm
    ) > "${ARTIFACTS_DIR}/rpm-artifacts.sha256"
    cp /tmp/build.log "${ARTIFACTS_DIR}/build.log"

    local source_rpm_sha spec_sha inputs_sha packages_sha artifacts_sha
    local runner_reference runner_identity target_cpu target_os os_id os_version
    source_rpm_sha=$(sha256sum "${source_rpm}" | awk '{print $1}')
    spec_sha=$(sha256sum "${BUILD_DIR}/SPECS/${SPEC_NAME}" | awk '{print $1}')
    inputs_sha=$(sha256sum "${ARTIFACTS_DIR}/build-inputs.sha256" | awk '{print $1}')
    packages_sha=$(sha256sum "${ARTIFACTS_DIR}/installed-packages.txt" | awk '{print $1}')
    artifacts_sha=$(sha256sum "${ARTIFACTS_DIR}/rpm-artifacts.sha256" | awk '{print $1}')
    runner_reference="${RUNNER_IMAGE_REFERENCE:-unknown}"
    runner_identity="${RUNNER_IMAGE_IDENTITY:-unknown}"
    target_cpu=$(rpm --eval '%{_target_cpu}')
    target_os=$(rpm --eval '%{_target_os}')
    os_id=$(. /etc/os-release && printf '%s' "${ID:-unknown}")
    os_version=$(. /etc/os-release && printf '%s' "${VERSION_ID:-unknown}")

    json_escape() {
        local value="$1"
        value=${value//\\/\\\\}
        value=${value//\"/\\\"}
        value=${value//$'\n'/\\n}
        value=${value//$'\r'/\\r}
        value=${value//$'\t'/\\t}
        printf '%s' "${value}"
    }

    cat > "${ARTIFACTS_DIR}/build-provenance.json" <<EOF
{
  "schemaVersion": 1,
  "buildJobId": "$(json_escape "${BUILD_JOB_ID:-unknown}")",
  "createdAtUtc": "$(date -u +'%Y-%m-%dT%H:%M:%SZ')",
  "sourceRpm": "$(json_escape "$(basename "${source_rpm}")")",
  "sourceRpmSha256": "${source_rpm_sha}",
  "specFile": "$(json_escape "${SPEC_NAME}")",
  "specSha256": "${spec_sha}",
  "inputsManifestSha256": "${inputs_sha}",
  "installedPackagesManifestSha256": "${packages_sha}",
  "rpmArtifactsManifestSha256": "${artifacts_sha}",
  "runnerImageReference": "$(json_escape "${runner_reference}")",
  "runnerImageIdentity": "$(json_escape "${runner_identity}")",
  "targetDistribution": "$(json_escape "${TARGET_DISTRIBUTION}")",
  "targetRelease": "$(json_escape "${TARGET_RELEASE}")",
  "targetArchitecture": "$(json_escape "${TARGET_ARCHITECTURE}")",
  "buildProfile": "$(json_escape "${BUILD_PROFILE}")",
  "targetCpu": "$(json_escape "${target_cpu}")",
  "targetOs": "$(json_escape "${target_os}")",
  "runnerOs": "$(json_escape "${os_id}")",
  "runnerOsVersion": "$(json_escape "${os_version}")",
  "sourceCommit": "$(json_escape "${COMMIT_SHA:-unknown}")"
}
EOF

    return 0
}

rm -f /tmp/lumina-source-rpm-path /tmp/build.log
chown -R rpmbuilder:lumina-build "${BUILD_DIR}"

# Drop privileges before the first parse or macro expansion of the raw spec.
export -f get_source0_filename get_setup_dirname resolve_spec_file copy_companion_sources create_tarball
export -f preparation_phase
export BUILD_DIR SPEC_NAME ARTIFACTS_DIR RPMBUILDER_HOME AUTO_DOWNLOAD
export SOURCE_DIR SOURCE_URL SPEC_CONTENT SPEC_PATH_IN_REPO
export TARGET_ARCHITECTURE
export HOME="${RPMBUILDER_HOME}"
setpriv --reuid 1000 --regid 1654 --clear-groups -- bash -c 'preparation_phase'

SOURCE_RPM="$(< /tmp/lumina-source-rpm-path)"
case "${SOURCE_RPM}" in
    "${BUILD_DIR}"/SRPMS/*.src.rpm) ;;
    *)
        echo "ERROR: unprivileged preparation returned an invalid SRPM path." >&2
        exit 1
        ;;
esac
if [ ! -f "${SOURCE_RPM}" ] || [ -L "${SOURCE_RPM}" ]; then
    echo "ERROR: prepared SRPM is missing or is not a regular file." >&2
    exit 1
fi
if [ "$(stat -c '%u' "${SOURCE_RPM}")" != "1000" ]; then
    echo "ERROR: prepared SRPM was not created by the unprivileged builder." >&2
    exit 1
fi

# Snapshot the unprivileged output into a root-owned, read-only location before
# the privileged package manager opens it. The later rebuild reads this same
# immutable copy.
ROOT_SRPM_DIR="/tmp/lumina-prepared-srpm"
rm -rf "${ROOT_SRPM_DIR}"
install -d -o root -g root -m 0755 "${ROOT_SRPM_DIR}"
ROOT_SRPM="${ROOT_SRPM_DIR}/$(basename "${SOURCE_RPM}")"
install -o root -g root -m 0444 "${SOURCE_RPM}" "${ROOT_SRPM}"
SOURCE_RPM="${ROOT_SRPM}"

# Root consumes only dependency tags from the already-created SRPM. It never
# invokes RPM's macro engine on the raw attacker-controlled spec. Avoid loading
# repository metadata when the SRPM has no external BuildRequires; rpmlib(...)
# entries describe RPM format capabilities and are already satisfied by rpm.
echo "Inspecting build dependencies from SRPM metadata..."
if ! srpm_requirements="$(rpm -qp --requires "${SOURCE_RPM}")"; then
    echo "ERROR: could not read build dependencies from the prepared SRPM."
    exit 1
fi
external_build_requirements="$({
    printf '%s\n' "${srpm_requirements}" | grep -v '^rpmlib(' || true
} | sed '/^[[:space:]]*$/d')"

if [ -n "${external_build_requirements}" ]; then
    echo "Installing build dependencies from SRPM metadata (as root)..."
    if ! dnf --disablerepo='*' --enablerepo=fedora builddep -y \
        "${SOURCE_RPM}"; then
        echo "ERROR: dnf builddep failed — see stderr above for the unresolvable/missing dependencies."
        exit 1
    fi
else
    echo "No external build dependencies declared; skipping repository metadata load."
fi

# Run the untrusted build phase as uid 1000.
export -f builder_phase
export BUILD_DIR SPEC_NAME ARTIFACTS_DIR RPMBUILDER_HOME SOURCE_RPM
export BUILD_JOB_ID COMMIT_SHA RUNNER_IMAGE_REFERENCE RUNNER_IMAGE_IDENTITY
export TARGET_DISTRIBUTION TARGET_RELEASE TARGET_ARCHITECTURE BUILD_PROFILE
setpriv --reuid 1000 --regid 1654 --clear-groups -- bash -c 'builder_phase'
BUILD_EXIT=$?

if [ ${BUILD_EXIT} -ne 0 ]; then
    exit ${BUILD_EXIT}
fi

echo "=== Build completed successfully ==="
echo "Artifacts:"
ls -la "${ARTIFACTS_DIR}/"
