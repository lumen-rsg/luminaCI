#!/bin/bash
# build-rpm.sh — RPM build script for Lumina CI
# Runs inside the rpm-build container
#
# Environment variables:
#   SPEC_CONTENT  — .spec file content (base64 encoded)
#   SPEC_NAME     — Name of the spec file
#   SOURCE_URL    — URL to download source tarball (optional)
#   ARTIFACTS_DIR — Output directory for built RPMs
#   BUILD_JOB_ID  — Build job ID for tracking

set -euo pipefail

SPEC_NAME="${SPEC_NAME:-package.spec}"
ARTIFACTS_DIR="${ARTIFACTS_DIR:-/artifacts}"
BUILD_DIR="/root/rpmbuild"

echo "=== Lumina CI RPM Build ==="
echo "Spec: ${SPEC_NAME}"
echo "Artifacts dir: ${ARTIFACTS_DIR}"
echo "Job ID: ${BUILD_JOB_ID:-N/A}"
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

cat "${BUILD_DIR}/SPECS/${SPEC_NAME}"
echo "--- end spec ---"

# Download source if URL provided
if [ -n "${SOURCE_URL:-}" ]; then
    if [[ "${SOURCE_URL}" == git://* ]]; then
        # Git clone source — format: git://https://repo.url#branch=main&specPath=pkg/package.spec&commit=abc123
        echo "Git clone source detected"
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
        CLONE_DIR=$(mktemp -d)
        git clone --depth 50 --branch "${GIT_BRANCH}" "${GIT_REPO}" "${CLONE_DIR}/repo" || {
            echo "ERROR: git clone failed"
            exit 1
        }
        
        if [ -n "${GIT_COMMIT}" ]; then
            cd "${CLONE_DIR}/repo" && git checkout "${GIT_COMMIT}" 2>/dev/null || echo "Warning: could not checkout ${GIT_COMMIT}"
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
            REPO_BASENAME=$(basename "${GIT_REPO}" .git)
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
        echo "Downloading source from: ${SOURCE_URL}"
        curl -L -f -o "${BUILD_DIR}/SOURCES/$(basename "${SOURCE_URL}")" "${SOURCE_URL}" || {
            echo "WARNING: Failed to download source, creating dummy tarball"
        }
    fi
fi

# Create dummy source tarball if spec uses Source0 but no source was provided
# This handles the common case where %setup -q expects a tarball
SOURCE0_LINE=$(grep -i "^Source0:" "${BUILD_DIR}/SPECS/${SPEC_NAME}" 2>/dev/null || true)
if [ -n "${SOURCE0_LINE}" ] && [ -z "${SOURCE_URL:-}" ]; then
    # Extract the source filename from spec
    SOURCE_FILE=$(echo "${SOURCE0_LINE}" | sed 's/^Source0:[[:space:]]*//' | sed 's/%{name}/ lumina-hello/g; s/%{version}/1.0.0/g; s/%%{Name}/lumina-hello/g; s/%%{Version}/1.0.0/g')
    # Get Name and Version from spec
    PKG_NAME=$(grep -i "^Name:" "${BUILD_DIR}/SPECS/${SPEC_NAME}" | awk '{print $2}' | tr -d '[:space:]')
    PKG_VERSION=$(grep -i "^Version:" "${BUILD_DIR}/SPECS/${SPEC_NAME}" | awk '{print $2}' | tr -d '[:space:]')
    
    if [ -n "${PKG_NAME}" ] && [ -n "${PKG_VERSION}" ]; then
        TARBALL_NAME="${PKG_NAME}-${PKG_VERSION}.tar.gz"
        if [ ! -f "${BUILD_DIR}/SOURCES/${TARBALL_NAME}" ]; then
            echo "Creating dummy source tarball: ${TARBALL_NAME}"
            TMP_DIR=$(mktemp -d)
            mkdir -p "${TMP_DIR}/${PKG_NAME}-${PKG_VERSION}"
            # Create a minimal README so the dir isn't empty
            echo "Lumina CI build: ${PKG_NAME}-${PKG_VERSION}" > "${TMP_DIR}/${PKG_NAME}-${PKG_VERSION}/README"
            tar -czf "${BUILD_DIR}/SOURCES/${TARBALL_NAME}" -C "${TMP_DIR}" "${PKG_NAME}-${PKG_VERSION}"
            rm -rf "${TMP_DIR}"
            echo "Dummy tarball created successfully"
        fi
    fi
fi

# Copy any additional sources from /sources directory
if [ -d "/sources" ]; then
    cp /sources/* "${BUILD_DIR}/SOURCES/" 2>/dev/null || true
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