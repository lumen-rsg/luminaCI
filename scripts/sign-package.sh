#!/bin/bash
# sign-package.sh — PGP sign an RPM package
# Called by Lumina.SecurityService
#
# Arguments:
#   $1 — Path to RPM file
#   $2 — GPG Key ID
# Environment:
#   GPG_HOME — GPG home directory

set -euo pipefail

RPM_FILE="${1:?Usage: sign-package.sh <rpm-file> <key-id>}"
KEY_ID="${2:?Usage: sign-package.sh <rpm-file> <key-id>}"
GPG_HOME="${GPG_HOME:-/app/keys}"

echo "=== Signing RPM package ==="
echo "File: ${RPM_FILE}"
echo "Key ID: ${KEY_ID}"
echo "GPG Home: ${GPG_HOME}"

# Check file exists
if [ ! -f "${RPM_FILE}" ]; then
    echo "ERROR: RPM file not found: ${RPM_FILE}"
    exit 1
fi

# Sign the RPM
rpmsign --define "_gpg_name ${KEY_ID}" \
    --define "_gpg_path ${GPG_HOME}" \
    --addsign "${RPM_FILE}"

# Verify signature
rpm --define "_gpg_path ${GPG_HOME}" \
    --checksig "${RPM_FILE}"

echo "=== Package signed successfully ==="