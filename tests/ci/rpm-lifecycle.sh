#!/usr/bin/env bash

set -euo pipefail

REPOSITORY_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
readonly REPOSITORY_ROOT
readonly RUNNER_IMAGE="${RPM_RUNNER_IMAGE:-lumina-rpm-build:ci}"
readonly TEST_IMAGE="${RPM_TEST_IMAGE:-lumina-rpm-lifecycle:ci}"

case "$(uname -m)" in
    x86_64) target_architecture="x86_64" ;;
    aarch64 | arm64) target_architecture="aarch64" ;;
    *)
        printf 'Unsupported CI architecture: %s\n' "$(uname -m)" >&2
        exit 1
        ;;
esac

temporary_directory="$(mktemp -d)"
cleanup() {
    rm -rf "$temporary_directory"
}
trap cleanup EXIT

source_directory="${temporary_directory}/sources"
artifact_directory="${temporary_directory}/artifacts"
package_root="${temporary_directory}/test_package-1.0"
mkdir -p "$source_directory" "$artifact_directory" "$package_root"
printf '%s\n' 'Lumina CI RPM lifecycle fixture' > "${package_root}/README"
tar -czf "${source_directory}/test_package-1.0.tar.gz" \
    --directory "$temporary_directory" \
    test_package-1.0
chmod 0777 "$artifact_directory"

docker build \
    --tag "$RUNNER_IMAGE" \
    --file "${REPOSITORY_ROOT}/deploy/docker/rpm-build.Dockerfile" \
    "$REPOSITORY_ROOT"

runner_identity="$(docker image inspect "$RUNNER_IMAGE" --format '{{.Id}}')"
docker run --rm \
    --security-opt no-new-privileges \
    --network bridge \
    --memory 2g \
    --memory-swap 2g \
    --pids-limit 512 \
    --cpus 1.5 \
    --env SPEC_NAME=test_package.spec \
    --env SOURCE_DIR=/sources \
    --env AUTO_DOWNLOAD=false \
    --env TARGET_DISTRIBUTION=fedora \
    --env TARGET_RELEASE=44 \
    --env TARGET_ARCHITECTURE="$target_architecture" \
    --env BUILD_PROFILE="fedora-44-${target_architecture}" \
    --env BUILD_JOB_ID=ci-rpm-lifecycle \
    --env COMMIT_SHA="${GITHUB_SHA:-local}" \
    --env RUNNER_IMAGE_REFERENCE="$RUNNER_IMAGE" \
    --env RUNNER_IMAGE_IDENTITY="$runner_identity" \
    --volume "${REPOSITORY_ROOT}/Test:/specs:ro,z" \
    --volume "${source_directory}:/sources:ro,z" \
    --volume "${artifact_directory}:/artifacts:z" \
    "$RUNNER_IMAGE"

mapfile -t binary_rpms < <(find "$artifact_directory" -maxdepth 1 -type f -name '*.rpm' ! -name '*.src.rpm')
[[ "${#binary_rpms[@]}" -eq 1 ]]
test -s "${artifact_directory}/build-provenance.json"
test -s "${artifact_directory}/build-inputs.sha256"
test -s "${artifact_directory}/rpm-artifacts.sha256"

cat > "${temporary_directory}/Dockerfile" <<'EOF'
FROM fedora:44
RUN dnf --disablerepo='*' --enablerepo=fedora \
    --setopt=install_weak_deps=False install -y \
    createrepo_c \
    gnupg2 \
    rpmlint \
    rpm-sign \
    && dnf clean all
EOF
docker build \
    --tag "$TEST_IMAGE" \
    --file "${temporary_directory}/Dockerfile" \
    "$REPOSITORY_ROOT"

docker run --rm \
    --volume "${artifact_directory}:/artifacts:z" \
    --volume "${REPOSITORY_ROOT}/scripts/sign-package.sh:/usr/local/bin/sign-package.sh:ro,z" \
    "$TEST_IMAGE" \
    bash -euo pipefail -c '
        rpm_file="$(find /artifacts -maxdepth 1 -type f -name "*.rpm" ! -name "*.src.rpm" -print -quit)"
        rpmlint "$rpm_file"

        export GNUPGHOME=/tmp/gnupg
        mkdir -m 0700 "$GNUPGHOME"
        gpg --batch --passphrase "" --quick-generate-key "Lumina CI Test <ci@lumina.invalid>" rsa2048 sign 1d
        key_id="$(gpg --batch --with-colons --list-secret-keys | awk -F: '"'"'$1 == "sec" { print $5; exit }'"'"')"
        gpg --batch --armor --export "$key_id" > /tmp/lumina-ci-public.asc
        rpm --import /tmp/lumina-ci-public.asc
        GPG_HOME="$GNUPGHOME" /usr/local/bin/sign-package.sh "$rpm_file" "$key_id"

        mkdir -p /tmp/repository
        cp "$rpm_file" /tmp/repository/
        createrepo_c /tmp/repository
        cat > /etc/yum.repos.d/lumina-ci.repo <<REPO
[lumina-ci]
name=Lumina CI lifecycle repository
baseurl=file:///tmp/repository
enabled=1
gpgcheck=1
gpgkey=file:///tmp/lumina-ci-public.asc
REPO
        dnf --disablerepo="*" --enablerepo=lumina-ci install -y test_package
        installed_output="$(test_package)"
        grep -q "build verified successfully" <<<"$installed_output"
    '

printf '%s\n' "RPM build, lint, signing, publication, installation, and execution passed."
