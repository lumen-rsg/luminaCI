#!/usr/bin/env bash

set -euo pipefail

readonly DEFAULT_SOURCE_IMAGE="registry.lumina.1t.ru/lumina-rpm-build:fedora44-arm64-d0728bc"
readonly DEFAULT_DIGEST_IMAGE="registry.lumina.1t.ru/lumina-rpm-build@sha256:72a1e05e7a9b13a421e4ff8090c3fbaf1762148fa8073d9fface726826d32a97"

if [[ "${EUID}" -ne 0 ]]; then
    printf 'Usage: sudo %s WIREGUARD_CONFIG K3S_AGENT_TOKEN\n' "$0" >&2
    exit 1
fi
if [[ "$#" -ne 2 ]]; then
    printf 'Usage: sudo %s WIREGUARD_CONFIG K3S_AGENT_TOKEN\n' "$0" >&2
    exit 1
fi

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
wireguard_config="$(realpath "$1")"
agent_token="$(realpath "$2")"
source_image="${K3S_RUNNER_IMAGE_SOURCE_TAG:-${DEFAULT_SOURCE_IMAGE}}"
digest_image="${K3S_RUNNER_IMAGE_DIGEST_REF:-${DEFAULT_DIGEST_IMAGE}}"

if [[ ! -s "${wireguard_config}" || ! -s "${agent_token}" ]]; then
    printf '%s\n' 'WireGuard configuration and K3s token must be non-empty files.' >&2
    exit 1
fi
for secret_path in "${wireguard_config}" "${agent_token}"; do
    if (( 8#$(stat -c '%a' "${secret_path}") & 077 )); then
        printf 'Secret is accessible to group or other users: %s\n' "${secret_path}" >&2
        exit 1
    fi
done
if ! grep -Eq '^Address = 10\.77\.0\.2/24$' "${wireguard_config}" \
    || ! grep -Eq '^AllowedIPs = 10\.77\.0\.1/32$' "${wireguard_config}"; then
    printf '%s\n' 'WireGuard configuration does not match the bounded worker mesh.' >&2
    exit 1
fi
if [[ ! "${digest_image}" =~ @sha256:[0-9a-f]{64}$ ]]; then
    printf '%s\n' 'K3s runner image must be pinned by a full SHA-256 digest.' >&2
    exit 1
fi

if command -v dnf >/dev/null; then
    dnf install -y wireguard-tools
elif command -v apt-get >/dev/null; then
    apt-get update -qq
    DEBIAN_FRONTEND=noninteractive apt-get install -y -qq wireguard-tools
else
    printf '%s\n' 'Unsupported package manager; install wireguard-tools manually.' >&2
    exit 1
fi

install -d -o root -g root -m 0700 /etc/wireguard
install -o root -g root -m 0600 \
    "${wireguard_config}" /etc/wireguard/wg-lumina.conf
install -d -o root -g root -m 0755 /etc/rancher/k3s
install -o root -g root -m 0600 \
    "${agent_token}" /etc/rancher/k3s/agent-token
systemctl enable --now wg-quick@wg-lumina.service

if ! ping -c 1 -W 5 10.77.0.1 >/dev/null; then
    printf '%s\n' 'The private K3s server address did not answer through WireGuard.' >&2
    exit 1
fi
"${repository_root}/scripts/bootstrap-k3s-agent.sh"

if ! command -v docker >/dev/null \
    || ! docker image inspect "${source_image}" >/dev/null 2>&1; then
    printf 'Native runner image is unavailable in Docker: %s\n' "${source_image}" >&2
    exit 1
fi
docker save "${source_image}" | k3s ctr images import -
if ! k3s ctr images list | awk '{ print $1 }' | grep -Fxq "${digest_image}"; then
    k3s ctr images tag "${source_image}" "${digest_image}"
fi
k3s ctr images list | grep -F 'registry.lumina.1t.ru/lumina-rpm-build'
wg show wg-lumina
printf '%s\n' 'Worker enrollment and digest-pinned runner preload completed.'
