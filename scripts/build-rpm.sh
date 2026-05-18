#!/bin/bash
# build-rpm.sh — RPM build script for Lumina CI
# Runs inside the rpm-build container
#
# Environment variables:
#   SPEC_CONTENT  — .spec file content (base64 encoded)
#   SPEC_NAME     — Name of the spec file
#   SOURCE_URL    — URL to download source tarball (optional, or git://... format)
#   SOURCE_DIR    — Directory with pre-fetched sources (mounted by SourceService, optional)
#   ARTIFACTS_DIR — Output directory for built RPMs
#   BUILD_JOB_ID  — Build job ID for tracking
#   AUTO_DOWNLOAD — Set to "true" to auto-download Source0/Source1 from spec (default: true)
#   GIT_USERNAME  — Username for private git repositories (optional)
#   GIT_TOKEN     — Personal Access Token / password for git auth (optional)

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

# Decode spec file if base64 encoded
if [ -n "${SPEC_CONTENT:-}" ]; then
    echo "${SPEC_CONTENT}" | base64 -d > "${BUILD_DIR}/SPECS/${SPEC_NAME}"
    echo "Spec file decoded and placed in SPECS/"
elif [ -f "/specs/${SPEC_NAME}" ]; then
    cp "/specs/${SPEC_NAME}" "${BUILD_DIR}/SPECS/${SPEC_NAME}"
    echo "Spec file copied from /specs/"
else
    echo "ERROR: No spec file provided!"
    exit 1
fi

echo "--- spec content ---"
cat "${BUILD_DIR}/SPECS/${SPEC_NAME}"
echo "--- end spec ---"

# ─── Helper: expand RPM macros in a string ───
# Reads Name, Version, URL, Epoch from spec and substitutes %{name}, %{version}, etc.
expand_spec_macros() {
    local input="$1"
    local spec_file="${BUILD_DIR}/SPECS/${SPEC_NAME}"

    # Extract key values from spec
    local pkg_name pkg_version pkg_url pkg_epoch
    pkg_name=$(grep -i "^Name:" "$spec_file" 2>/dev/null | head -1 | sed 's/^Name:[[:space:]]*//' | tr -d '[:space:]')
    pkg_version=$(grep -i "^Version:" "$spec_file" 2>/dev/null | head -1 | sed 's/^Version:[[:space:]]*//' | tr -d '[:space:]')
    pkg_url=$(grep -i "^URL:" "$spec_file" 2>/dev/null | head -1 | sed 's/^URL:[[:space:]]*//' | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')
    pkg_epoch=$(grep -i "^Epoch:" "$spec_file" 2>/dev/null | head -1 | sed 's/^Epoch:[[:space:]]*//' | tr -d '[:space:]')

    # Expand macros (case-insensitive, handle both %{macro} and %%{macro})
    local result="$input"
    result="${result//\%\{name\}/$pkg_name}"
    result="${result//\%\{Name\}/$pkg_name}"
    result="${result//\%\{NAME\}/$pkg_name}"
    result="${result//\%\{version\}/$pkg_version}"
    result="${result//\%\{Version\}/$pkg_version}"
    result="${result//\%\{VERSION\}/$pkg_version}"
    result="${result//\%\{url\}/$pkg_url}"
    result="${result//\%\{URL\}/$pkg_url}"
    result="${result//\%\{epoch\}/$pkg_epoch}"
    result="${result//\%\{dist\}/}"
    result="${result//\%\{?dist\}/}"

    echo "$result"
}

# ─── Helper: download a source URL to SOURCES with correct filename ───
download_source() {
    local source_tag="$1"  # e.g. "Source0: https://..."
    local spec_file="${BUILD_DIR}/SPECS/${SPEC_NAME}"

    # Extract the value after SourceN:
    local source_value
    source_value=$(echo "$source_tag" | sed 's/^Source[0-9]*:[[:space:]]*//')

    # Expand macros
    local expanded_url
    expanded_url=$(expand_spec_macros "$source_value")

    echo "  Source reference: ${source_value}"
    echo "  Expanded:         ${expanded_url}"

    # Skip if it's not a URL (just a filename — we'll handle it later)
    if [[ ! "${expanded_url}" =~ ^https?:// ]] && [[ ! "${expanded_url}" =~ ^ftp:// ]]; then
        echo "  → Not a URL, skipping download (will look for local file)"
        return
    fi

    # Determine the target filename — use the basename portion of the expanded value
    # For URLs like .../archive/v2.1/aurora.net-2.1.tar.gz → aurora.net-2.1.tar.gz
    local target_filename
    target_filename=$(basename "${expanded_url}")

    # Also check if spec references a specific name pattern
    # e.g. Source0: %{name}-%{version}.tar.gz → myapp-1.0.tar.gz
    local spec_filename
    spec_filename=$(expand_spec_macros "$(echo "$source_value" | sed 's/.*\///')")

    # Use the expanded spec filename if it looks reasonable
    if [ -n "$spec_filename" ] && [[ "$spec_filename" == *.* ]]; then
        target_filename="$spec_filename"
    fi

    echo "  → Downloading to SOURCES/${target_filename}"

    curl -L -f -o "${BUILD_DIR}/SOURCES/${target_filename}" "${expanded_url}" 2>&1 || {
        echo "  WARNING: Failed to download ${expanded_url}"
        return 1
    }

    echo "  ✓ Downloaded: ${target_filename} ($(stat -c%s "${BUILD_DIR}/SOURCES/${target_filename}" 2>/dev/null || echo '?') bytes)"
}

# ─── Step 1: Download source if SOURCE_URL is provided ───
if [ -n "${SOURCE_URL:-}" ]; then
    if [[ "${SOURCE_URL}" == git://* ]]; then
        # Git clone source — format: git://https://repo.url#branch=main&specPath=pkg/package.spec&commit=abc123
        echo "=== Git Clone Source ==="
        GIT_FULL="${SOURCE_URL#git://}"
        GIT_REPO="${GIT_FULL%%#*}"
        GIT_PARAMS="${GIT_FULL#*#}"

        # Parse parameters
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

        # Inject credentials into URL for private repositories
        AUTH_REPO="${GIT_REPO}"
        if [ -n "${GIT_USERNAME:-}" ] && [ -n "${GIT_TOKEN:-}" ]; then
            # Handle various URL formats: https://host/path, https://host:port/path, http://...
            if [[ "${GIT_REPO}" =~ ^https://([^/]+)(/.*)$ ]]; then
                AUTH_REPO="https://${GIT_USERNAME}:${GIT_TOKEN}@${BASH_REMATCH[1]}${BASH_REMATCH[2]}"
                echo "Using authenticated git URL (username: ${GIT_USERNAME})"
            elif [[ "${GIT_REPO}" =~ ^http://([^/]+)(/.*)$ ]]; then
                AUTH_REPO="http://${GIT_USERNAME}:${GIT_TOKEN}@${BASH_REMATCH[1]}${BASH_REMATCH[2]}"
                echo "Using authenticated git URL (username: ${GIT_USERNAME})"
            else
                echo "Warning: Cannot inject credentials for non-HTTPS URL: ${GIT_REPO}"
            fi
        fi

        CLONE_DIR=$(mktemp -d)
        git clone --depth 50 --branch "${GIT_BRANCH}" "${AUTH_REPO}" "${CLONE_DIR}/repo" || {
            echo "ERROR: git clone failed"
            exit 1
        }

        if [ -n "${GIT_COMMIT}" ]; then
            cd "${CLONE_DIR}/repo" && git checkout "${GIT_COMMIT}" 2>/dev/null || echo "Warning: could not checkout ${GIT_COMMIT}"
            cd /
        fi

        # Initialize git submodules if present
        if [ -f "${CLONE_DIR}/repo/.gitmodules" ]; then
            echo "Initializing git submodules..."
            cd "${CLONE_DIR}/repo"
            git submodule update --init --recursive 2>/dev/null || echo "Warning: submodule initialization failed"
            cd /
        fi

        # Find and copy spec file from cloned repo
        if [ -n "${SPEC_PATH_IN_REPO}" ] && [ -f "${CLONE_DIR}/repo/${SPEC_PATH_IN_REPO}" ]; then
            cp "${CLONE_DIR}/repo/${SPEC_PATH_IN_REPO}" "${BUILD_DIR}/SPECS/${SPEC_NAME}"
            echo "Spec file copied from ${SPEC_PATH_IN_REPO}"
        elif [ -z "${SPEC_CONTENT:-}" ]; then
            # Auto-find .spec file in repo
            FOUND_SPEC=$(find "${CLONE_DIR}/repo" -maxdepth 3 -name "*.spec" -type f | head -1)
            if [ -n "${FOUND_SPEC}" ]; then
                cp "${FOUND_SPEC}" "${BUILD_DIR}/SPECS/${SPEC_NAME}"
                echo "Auto-found spec: ${FOUND_SPEC}"
            else
                echo "ERROR: No .spec file found in repository"
                exit 1
            fi
        fi

        # Copy all source files from repo to SOURCES
        if [ -d "${CLONE_DIR}/repo" ]; then
            # Copy tarballs, patches, and other source files
            find "${CLONE_DIR}/repo" -maxdepth 1 \( -name "*.tar.gz" -o -name "*.tar.bz2" -o -name "*.tar.xz" -o -name "*.patch" -o -name "*.diff" \) -exec cp {} "${BUILD_DIR}/SOURCES/" \;

            # Also check for a sources/ or SOURCES/ directory in repo
            for srcdir in "${CLONE_DIR}/repo/sources" "${CLONE_DIR}/repo/SOURCES" "${CLONE_DIR}/repo/dist"; do
                if [ -d "$srcdir" ]; then
                    cp "$srcdir"/* "${BUILD_DIR}/SOURCES/" 2>/dev/null || true
                    echo "Copied sources from ${srcdir}"
                fi
            done

            # Create source tarball from repo if Source0 expects one
            PKG_NAME_FROM_SPEC=$(grep -i "^Name:" "${BUILD_DIR}/SPECS/${SPEC_NAME}" 2>/dev/null | awk '{print $2}' | tr -d '[:space:]')
            PKG_VERSION_FROM_SPEC=$(grep -i "^Version:" "${BUILD_DIR}/SPECS/${SPEC_NAME}" 2>/dev/null | awk '{print $2}' | tr -d '[:space:]')
            if [ -n "${PKG_NAME_FROM_SPEC}" ] && [ -n "${PKG_VERSION_FROM_SPEC}" ]; then
                TARBALL_NAME="${PKG_NAME_FROM_SPEC}-${PKG_VERSION_FROM_SPEC}.tar.gz"
                if [ ! -f "${BUILD_DIR}/SOURCES/${TARBALL_NAME}" ]; then
                    echo "Creating source tarball: ${TARBALL_NAME}"
                    mkdir -p "${CLONE_DIR}/tardir/${PKG_NAME_FROM_SPEC}-${PKG_VERSION_FROM_SPEC}"
                    cp -r "${CLONE_DIR}/repo"/* "${CLONE_DIR}/tardir/${PKG_NAME_FROM_SPEC}-${PKG_VERSION_FROM_SPEC}/" 2>/dev/null || true
                    tar -czf "${BUILD_DIR}/SOURCES/${TARBALL_NAME}" -C "${CLONE_DIR}/tardir" "${PKG_NAME_FROM_SPEC}-${PKG_VERSION_FROM_SPEC}"
                fi
            fi
        fi

        rm -rf "${CLONE_DIR}"
        echo "Git clone source preparation completed"
    else
        echo "=== Downloading source from URL ==="
        curl -L -f -o "${BUILD_DIR}/SOURCES/$(basename "${SOURCE_URL}")" "${SOURCE_URL}" || {
            echo "WARNING: Failed to download source"
        }
    fi
fi

# ─── Step 1b: Copy pre-fetched sources from SOURCE_DIR if mounted ───
if [ -n "${SOURCE_DIR:-}" ] && [ -d "${SOURCE_DIR}" ]; then
    echo "=== Using pre-fetched sources from ${SOURCE_DIR} ==="
    
    # Check if SOURCE_DIR contains a git repo (has .git directory)
    if [ -d "${SOURCE_DIR}/repo/.git" ] || [ -d "${SOURCE_DIR}/.git" ]; then
        REPO_DIR="${SOURCE_DIR}"
        [ -d "${SOURCE_DIR}/repo" ] && REPO_DIR="${SOURCE_DIR}/repo"
        
        echo "Source is a git repository: ${REPO_DIR}"
        
        # Auto-find spec file in repo if not already provided
        if [ -z "${SPEC_CONTENT:-}" ]; then
            FOUND_SPEC=$(find "${REPO_DIR}" -maxdepth 3 -name "*.spec" -type f 2>/dev/null | head -1)
            if [ -n "${FOUND_SPEC}" ]; then
                cp "${FOUND_SPEC}" "${BUILD_DIR}/SPECS/${SPEC_NAME}"
                echo "Auto-found spec in pre-fetched repo: ${FOUND_SPEC}"
            fi
        fi
        
        # Copy source files from repo
        find "${REPO_DIR}" -maxdepth 1 \( -name "*.tar.gz" -o -name "*.tar.bz2" -o -name "*.tar.xz" -o -name "*.patch" -o -name "*.diff" \) -exec cp {} "${BUILD_DIR}/SOURCES/" \;
        
        for srcdir in "${REPO_DIR}/sources" "${REPO_DIR}/SOURCES" "${REPO_DIR}/dist"; do
            if [ -d "$srcdir" ]; then
                cp "$srcdir"/* "${BUILD_DIR}/SOURCES/" 2>/dev/null || true
                echo "Copied sources from ${srcdir}"
            fi
        done
        
        # Create source tarball from repo content
        PKG_NAME_FROM_SPEC=$(grep -i "^Name:" "${BUILD_DIR}/SPECS/${SPEC_NAME}" 2>/dev/null | awk '{print $2}' | tr -d '[:space:]')
        PKG_VERSION_FROM_SPEC=$(grep -i "^Version:" "${BUILD_DIR}/SPECS/${SPEC_NAME}" 2>/dev/null | awk '{print $2}' | tr -d '[:space:]')
        if [ -n "${PKG_NAME_FROM_SPEC}" ] && [ -n "${PKG_VERSION_FROM_SPEC}" ]; then
            TARBALL_NAME="${PKG_NAME_FROM_SPEC}-${PKG_VERSION_FROM_SPEC}.tar.gz"
            if [ ! -f "${BUILD_DIR}/SOURCES/${TARBALL_NAME}" ]; then
                echo "Creating source tarball from pre-fetched repo: ${TARBALL_NAME}"
                TMP_TARDIR=$(mktemp -d)
                mkdir -p "${TMP_TARDIR}/${PKG_NAME_FROM_SPEC}-${PKG_VERSION_FROM_SPEC}"
                cp -r "${REPO_DIR}"/* "${TMP_TARDIR}/${PKG_NAME_FROM_SPEC}-${PKG_VERSION_FROM_SPEC}/" 2>/dev/null || true
                tar -czf "${BUILD_DIR}/SOURCES/${TARBALL_NAME}" -C "${TMP_TARDIR}" "${PKG_NAME_FROM_SPEC}-${PKG_VERSION_FROM_SPEC}"
                rm -rf "${TMP_TARDIR}"
            fi
        fi
    else
        # SOURCE_DIR contains tarballs or other files — copy directly to SOURCES
        echo "Copying pre-fetched source files from ${SOURCE_DIR}"
        cp -v "${SOURCE_DIR}"/* "${BUILD_DIR}/SOURCES/" 2>/dev/null || true
        
        # If there's an "extracted" subdirectory, handle it
        if [ -d "${SOURCE_DIR}/extracted" ]; then
            echo "Found extracted sources in ${SOURCE_DIR}/extracted"
            PKG_NAME_FROM_SPEC=$(grep -i "^Name:" "${BUILD_DIR}/SPECS/${SPEC_NAME}" 2>/dev/null | awk '{print $2}' | tr -d '[:space:]')
            PKG_VERSION_FROM_SPEC=$(grep -i "^Version:" "${BUILD_DIR}/SPECS/${SPEC_NAME}" 2>/dev/null | awk '{print $2}' | tr -d '[:space:]')
            if [ -n "${PKG_NAME_FROM_SPEC}" ] && [ -n "${PKG_VERSION_FROM_SPEC}" ]; then
                TARBALL_NAME="${PKG_NAME_FROM_SPEC}-${PKG_VERSION_FROM_SPEC}.tar.gz"
                if [ ! -f "${BUILD_DIR}/SOURCES/${TARBALL_NAME}" ]; then
                    echo "Creating tarball from extracted sources: ${TARBALL_NAME}"
                    tar -czf "${BUILD_DIR}/SOURCES/${TARBALL_NAME}" -C "${SOURCE_DIR}/extracted" .
                fi
            fi
        fi
    fi
    
    echo "Pre-fetched sources preparation completed"
    echo "SOURCES directory contents:"
    ls -la "${BUILD_DIR}/SOURCES/"
    
    # Skip auto-download since sources are pre-fetched
    AUTO_DOWNLOAD="false"
fi

# ─── Step 2: Auto-download Source0/Source1/... from spec ───
if [ "${AUTO_DOWNLOAD}" = "true" ]; then
    echo "=== Auto-downloading sources from spec ==="
    SPEC_FILE="${BUILD_DIR}/SPECS/${SPEC_NAME}"

    # Find all Source lines (Source0, Source1, ..., Source)
    SOURCE_LINES=$(grep -iE "^Source[0-9]*:" "$SPEC_FILE" 2>/dev/null || true)
    if [ -n "${SOURCE_LINES}" ]; then
        echo "$SOURCE_LINES" | while IFS= read -r line; do
            echo "Processing: $line"
            download_source "$line" || true
        done
    else
        echo "No Source entries found in spec"
    fi
fi

# ─── Step 3: Create dummy tarball if needed ───
# If Source0 references a file that doesn't exist yet, create a dummy
SOURCE0_LINE=$(grep -i "^Source0:" "${BUILD_DIR}/SPECS/${SPEC_NAME}" 2>/dev/null || true)
if [ -n "${SOURCE0_LINE}" ]; then
    SOURCE0_FILENAME=$(expand_spec_macros "$(echo "$SOURCE0_LINE" | sed 's/^Source0:[[:space:]]*//' | sed 's/.*\///')")
    if [ -n "${SOURCE0_FILENAME}" ] && [ ! -f "${BUILD_DIR}/SOURCES/${SOURCE0_FILENAME}" ]; then
        PKG_NAME=$(grep -i "^Name:" "${BUILD_DIR}/SPECS/${SPEC_NAME}" | awk '{print $2}' | tr -d '[:space:]')
        PKG_VERSION=$(grep -i "^Version:" "${BUILD_DIR}/SPECS/${SPEC_NAME}" | awk '{print $2}' | tr -d '[:space:]')

        if [ -n "${PKG_NAME}" ] && [ -n "${PKG_VERSION}" ]; then
            TARBALL_NAME="${SOURCE0_FILENAME}"
            echo "Creating dummy source tarball: ${TARBALL_NAME}"
            TMP_DIR=$(mktemp -d)
            mkdir -p "${TMP_DIR}/${PKG_NAME}-${PKG_VERSION}"
            echo "Lumina CI build: ${PKG_NAME}-${PKG_VERSION}" > "${TMP_DIR}/${PKG_NAME}-${PKG_VERSION}/README"
            tar -czf "${BUILD_DIR}/SOURCES/${TARBALL_NAME}" -C "${TMP_DIR}" "${PKG_NAME}-${PKG_VERSION}"
            rm -rf "${TMP_DIR}"
            echo "Dummy tarball created successfully"
        fi
    fi
fi

# Install build dependencies (best-effort)
echo "Installing build dependencies..."
sudo dnf builddep -y "${BUILD_DIR}/SPECS/${SPEC_NAME}" 2>/dev/null || echo "Warning: Some build dependencies may be missing"

# Build the RPM (disable debug packages for simple/script packages)
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

# Copy artifacts
sudo mkdir -p "${ARTIFACTS_DIR}" 2>/dev/null || true
sudo chmod 777 "${ARTIFACTS_DIR}" 2>/dev/null || true
sudo cp -v "${BUILD_DIR}"/RPMS/*/*.rpm "${ARTIFACTS_DIR}/" 2>/dev/null || true
sudo cp -v "${BUILD_DIR}"/SRPMS/*.rpm "${ARTIFACTS_DIR}/" 2>/dev/null || true

echo "=== Build completed successfully ==="
echo "Artifacts:"
ls -la "${ARTIFACTS_DIR}/"

# Output artifact list as JSON for parsing
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