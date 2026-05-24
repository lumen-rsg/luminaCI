#!/bin/bash
# build-rpm.sh — RPM build script for Lumina CI
# Runs inside the rpm-build container
#
# Environment variables:
#   SPEC_CONTENT  — .spec file content (base64 encoded)
#   SPEC_NAME     — Name of the spec file
#   SOURCE_URL    — URL to download source, or git://... for git clone
#   SOURCE_DIR    — Directory with pre-fetched sources (mounted by SourceService)
#   ARTIFACTS_DIR — Output directory for built RPMs
#   BUILD_JOB_ID  — Build job ID for tracking
#   AUTO_DOWNLOAD — "true" to run spectool for missing sources (default: true)
#   GIT_USERNAME  — Username for private git repositories
#   GIT_TOKEN     — PAT / password for git auth

set -euo pipefail

SPEC_NAME="${SPEC_NAME:-package.spec}"
ARTIFACTS_DIR="${ARTIFACTS_DIR:-/artifacts}"
BUILD_DIR="/root/rpmbuild"
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
    # Also copy dotfiles (e.g. .gitmodules)
    cp -a "${src_dir}"/.* "${tmp_dir}/${tarball_stem}/" 2>/dev/null || true

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
            FOUND_SPEC=$(find "${REPO_DIR}" -maxdepth 3 -name "*.spec" -type f 2>/dev/null | head -1)
            if [ -n "${FOUND_SPEC}" ]; then
                cp "${FOUND_SPEC}" "${BUILD_DIR}/SPECS/${SPEC_NAME}"
                echo "  Auto-found spec: ${FOUND_SPEC}"
                SPEC_DEFERRED=false
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
            git submodule update --init --recursive 2>/dev/null || echo "Warning: submodule init failed"
            cd /
        fi

        # Find and copy spec file
        if [ -n "${SPEC_PATH_IN_REPO}" ] && [ -f "${CLONE_DIR}/repo/${SPEC_PATH_IN_REPO}" ]; then
            cp "${CLONE_DIR}/repo/${SPEC_PATH_IN_REPO}" "${BUILD_DIR}/SPECS/${SPEC_NAME}"
            echo "Spec file copied from ${SPEC_PATH_IN_REPO}"
            SPEC_DEFERRED=false
        elif $SPEC_DEFERRED; then
            FOUND_SPEC=$(find "${CLONE_DIR}/repo" -maxdepth 3 -name "*.spec" -type f | head -1)
            if [ -n "${FOUND_SPEC}" ]; then
                cp "${FOUND_SPEC}" "${BUILD_DIR}/SPECS/${SPEC_NAME}"
                echo "Auto-found spec: ${FOUND_SPEC}"
                SPEC_DEFERRED=false
            else
                echo "ERROR: No .spec file found in repository"
                exit 1
            fi
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
    spectool -g -R -a "${BUILD_DIR}/SPECS/${SPEC_NAME}" 2>&1 || {
        echo "WARNING: spectool reported failures (some sources may already be present)"
    }
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
echo "Installing build dependencies..."
sudo dnf builddep -y "${BUILD_DIR}/SPECS/${SPEC_NAME}" 2>/dev/null || echo "Warning: Some build dependencies may be missing"

echo "Building RPM..."
rpmbuild -bb "${BUILD_DIR}/SPECS/${SPEC_NAME}" \
    --define "_topdir ${BUILD_DIR}" \
    --define "debug_package %{nil}" \
    2>&1 | tee /tmp/build.log

BUILD_EXIT=${PIPESTATUS[0]}

if [ ${BUILD_EXIT} -ne 0 ]; then
    echo "ERROR: RPM build failed with exit code ${BUILD_EXIT}!"
    exit ${BUILD_EXIT}
fi

# ═══════════════════════════════════════════════════════════
# Step 7: Collect artifacts
# ═══════════════════════════════════════════════════════════
sudo mkdir -p "${ARTIFACTS_DIR}" 2>/dev/null || true
sudo chmod 777 "${ARTIFACTS_DIR}" 2>/dev/null || true
sudo cp -v "${BUILD_DIR}"/RPMS/*/*.rpm "${ARTIFACTS_DIR}/" 2>/dev/null || true
sudo cp -v "${BUILD_DIR}"/SRPMS/*.rpm "${ARTIFACTS_DIR}/" 2>/dev/null || true

echo "=== Build completed successfully ==="
echo "Artifacts:"
ls -la "${ARTIFACTS_DIR}/"

echo "=== ARTIFACTS_JSON ==="
ARTIFACTS_ARRAY="["
FIRST=true
for f in "${ARTIFACTS_DIR}"/*.rpm; do
    if [ -f "$f" ]; then
        FILENAME=$(basename "$f")
        FILESIZE=$(stat -c%s "$f" 2>/dev/null || echo "0")
        HASH=$(sha256sum "$f" | cut -d' ' -f1)
        if [ "$FIRST" = true ]; then
            FIRST=false
        else
            ARTIFACTS_ARRAY+=","
        fi
        ARTIFACTS_ARRAY+="{\"fileName\":\"${FILENAME}\",\"fileSize\":${FILESIZE},\"hashSha256\":\"${HASH}\"}"
    fi
done
ARTIFACTS_ARRAY+="]"
echo "${ARTIFACTS_ARRAY}"
