#!/usr/bin/env bash

set -euo pipefail

readonly RUNNER_IMAGE="${1:?usage: untrusted-spec-boundary.sh <runner-image> <target-architecture>}"
readonly TARGET_ARCHITECTURE="${2:?usage: untrusted-spec-boundary.sh <runner-image> <target-architecture>}"

temporary_directory="$(mktemp -d)"
cleanup() {
    rm -rf "$temporary_directory"
}
trap cleanup EXIT

spec_directory="${temporary_directory}/specs"
artifact_directory="${temporary_directory}/artifacts"
credential_artifact_directory="${temporary_directory}/credential-artifacts"
mkdir -p "$spec_directory" "$artifact_directory" "$credential_artifact_directory"
chmod 0777 "$artifact_directory" "$credential_artifact_directory"

cat > "${spec_directory}/hostile.spec" <<'EOF'
%global record_parser_uid %(id -u >> /artifacts/parser-uids)
%global detect_credentials %(if env | grep -q '^GIT_TOKEN='; then touch /artifacts/credential-leak; fi)

Name:           hostile-parser-probe
Version:        1.0
Release:        1
Summary:        Verify parser privilege isolation %{record_parser_uid}%{detect_credentials}
License:        MIT
BuildArch:      noarch

%description
Regression fixture for RPM macro privilege isolation.

%prep

%build

%install
install -d %{buildroot}%{_datadir}/hostile-parser-probe
printf '%s\n' isolated > %{buildroot}%{_datadir}/hostile-parser-probe/result

%files
%{_datadir}/hostile-parser-probe/result
EOF

run_probe() {
    local artifacts="$1"
    shift
    docker run --rm \
        --security-opt no-new-privileges \
        --network bridge \
        --memory 1g \
        --memory-swap 1g \
        --pids-limit 256 \
        --cpus 1 \
        --env SPEC_NAME=hostile.spec \
        --env AUTO_DOWNLOAD=false \
        --env TARGET_DISTRIBUTION=fedora \
        --env TARGET_RELEASE=44 \
        --env TARGET_ARCHITECTURE="$TARGET_ARCHITECTURE" \
        --env BUILD_PROFILE="fedora-44-${TARGET_ARCHITECTURE}" \
        --volume "${spec_directory}:/specs:ro,z" \
        --volume "${artifacts}:/artifacts:z" \
        "$@" \
        "$RUNNER_IMAGE"
}

run_probe "$artifact_directory"

test -s "${artifact_directory}/parser-uids"
if grep -vxq '1000' "${artifact_directory}/parser-uids"; then
    printf '%s\n' "Untrusted spec parsing escaped uid 1000:" >&2
    cat "${artifact_directory}/parser-uids" >&2
    exit 1
fi
test ! -e "${artifact_directory}/credential-leak"

if run_probe "$credential_artifact_directory" \
    --env GIT_USERNAME=ci-user \
    --env GIT_TOKEN=ci-secret \
    >"${temporary_directory}/credential-rejection.log" 2>&1; then
    printf '%s\n' "Credential-bearing build container unexpectedly started." >&2
    exit 1
fi
grep -q 'Git credentials are forbidden' "${temporary_directory}/credential-rejection.log"
test ! -e "${credential_artifact_directory}/parser-uids"
test ! -e "${credential_artifact_directory}/credential-leak"

printf '%s\n' "Raw spec parsing stayed at uid 1000 and credentials were rejected before parsing."
