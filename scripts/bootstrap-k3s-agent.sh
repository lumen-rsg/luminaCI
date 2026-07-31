#!/usr/bin/env bash

set -euo pipefail

readonly K3S_VERSION="v1.36.1+k3s1"
readonly K3S_AMD64_SHA256="a443db3fe9820cd93617ae67e4386d87c1514c1e96ceb30f4c2791c39065653c"
readonly K3S_ARM64_SHA256="2240e9f7fd8cf36e9998d7ef18781975780d724b59408174618ec15b410891dc"
readonly K3S_SELINUX_URL="https://rpm.rancher.io/k3s/latest/common/centos/9/noarch/k3s-selinux-1.6-1.el9.noarch.rpm"
readonly K3S_SELINUX_SHA256="1115a1223b0db998c7977c879f2a3a6f07a0c63240f68f6e3b665a7ae27c38f7"

if [[ "${EUID}" -ne 0 ]]; then
    printf '%s\n' 'Run this bootstrap as root on the worker.' >&2
    exit 1
fi

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
server_url="${K3S_SERVER_URL:-https://10.77.0.1:6443}"
node_ip="${K3S_NODE_IP:-10.77.0.2}"
token_file="${K3S_TOKEN_FILE:-/etc/rancher/k3s/agent-token}"
mesh_interface="${K3S_MESH_INTERFACE:-wg-lumina}"

if [[ ! "${server_url}" =~ ^https://[a-zA-Z0-9.-]+:6443$ ]]; then
    printf '%s\n' 'K3S_SERVER_URL must be an HTTPS host on port 6443.' >&2
    exit 1
fi
if [[ ! "${node_ip}" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    printf '%s\n' 'K3S_NODE_IP must be one IPv4 address.' >&2
    exit 1
fi
if [[ ! -s "${token_file}" ]] \
    || (( 8#$(stat -c '%a' "${token_file}") & 077 )); then
    printf 'K3s token file is missing, empty, or accessible to other users: %s\n' \
        "${token_file}" >&2
    exit 1
fi
if ! ip link show "${mesh_interface}" >/dev/null 2>&1 \
    || ! ip address show dev "${mesh_interface}" | grep -Fq "${node_ip}/"; then
    printf 'Private worker mesh interface %s is not ready with %s.\n' \
        "${mesh_interface}" "${node_ip}" >&2
    exit 1
fi
server_authority="${server_url#https://}"
server_host="${server_authority%:*}"
server_port="${server_authority##*:}"
if ! timeout 5 bash -c "</dev/tcp/${server_host}/${server_port}"; then
    printf 'K3s server is unreachable through the private worker mesh: %s\n' \
        "${server_url}" >&2
    exit 1
fi

case "$(uname -m)" in
    x86_64)
        binary_name="k3s"
        binary_sha256="${K3S_AMD64_SHA256}"
        ;;
    aarch64 | arm64)
        binary_name="k3s-arm64"
        binary_sha256="${K3S_ARM64_SHA256}"
        ;;
    *)
        printf 'Unsupported worker architecture: %s\n' "$(uname -m)" >&2
        exit 1
        ;;
esac
if [[ "$(stat -fc %T /sys/fs/cgroup)" != "cgroup2fs" ]]; then
    printf '%s\n' 'Kubernetes build workers require cgroup v2.' >&2
    exit 1
fi
kernel_major="$(uname -r | cut -d. -f1)"
kernel_minor="$(uname -r | cut -d. -f2)"
if (( kernel_major < 6 || (kernel_major == 6 && kernel_minor < 3) )); then
    printf '%s\n' 'Kubernetes user namespaces require Linux 6.3 or newer.' >&2
    exit 1
fi

target_config="/etc/rancher/k3s/config.yaml"
target_service="/etc/systemd/system/k3s-agent.service"
if [[ -e "${target_service}" ]] \
    && ! cmp --silent "${repository_root}/deploy/kubernetes/k3s-agent.service" "${target_service}"; then
    printf 'Refusing to overwrite modified service: %s\n' "${target_service}" >&2
    exit 1
fi
if [[ -e /usr/local/bin/k3s ]] \
    && ! printf '%s  %s\n' "${binary_sha256}" /usr/local/bin/k3s \
        | sha256sum --check --status; then
    printf '%s\n' 'Refusing to replace an unexpected /usr/local/bin/k3s binary.' >&2
    exit 1
fi

temporary_directory="$(mktemp -d /tmp/lumina-k3s-agent.XXXXXX)"
cleanup() {
    rm -rf -- "${temporary_directory}"
}
trap cleanup EXIT
download_url="https://github.com/k3s-io/k3s/releases/download/${K3S_VERSION/+/%2B}/${binary_name}"
curl --fail --location --proto '=https' --tlsv1.2 \
    --output "${temporary_directory}/k3s" "${download_url}"
printf '%s  %s\n' "${binary_sha256}" "${temporary_directory}/k3s" \
    | sha256sum --check --status

selinux_enabled=false
if command -v getenforce >/dev/null \
    && [[ "$(getenforce)" != 'Disabled' ]]; then
    selinux_enabled=true
    dnf install -y container-selinux selinux-policy-base
    curl --fail --location --proto '=https' --tlsv1.2 \
        --output "${temporary_directory}/k3s-selinux.rpm" "${K3S_SELINUX_URL}"
    printf '%s  %s\n' "${K3S_SELINUX_SHA256}" \
        "${temporary_directory}/k3s-selinux.rpm" | sha256sum --check --status
    dnf install -y "${temporary_directory}/k3s-selinux.rpm"
fi

cat > "${temporary_directory}/config.yaml" <<EOF
server: ${server_url}
token-file: ${token_file}
node-ip: ${node_ip}
flannel-iface: ${mesh_interface}
node-label:
  - lumina.1t.ru/build-worker=true
node-taint:
  - lumina.1t.ru/build-worker=true:NoSchedule
protect-kernel-defaults: false
selinux: ${selinux_enabled}
EOF
if [[ -e "${target_config}" ]] \
    && ! cmp --silent "${temporary_directory}/config.yaml" "${target_config}"; then
    printf 'Refusing to overwrite modified worker configuration: %s\n' \
        "${target_config}" >&2
    exit 1
fi

install -o root -g root -m 0755 "${temporary_directory}/k3s" /usr/local/bin/k3s
install -d -o root -g root -m 0755 /etc/rancher/k3s
install -o root -g root -m 0600 "${temporary_directory}/config.yaml" "${target_config}"
install -o root -g root -m 0644 \
    "${repository_root}/deploy/kubernetes/k3s-agent.service" "${target_service}"
systemctl daemon-reload
systemctl enable --now k3s-agent.service

for _ in {1..60}; do
    if systemctl is-active --quiet k3s-agent.service \
        && [[ -S /run/k3s/containerd/containerd.sock ]]; then
        printf 'K3s %s worker is running on %s (%s).\n' \
            "${K3S_VERSION}" "$(hostname)" "$(uname -m)"
        exit 0
    fi
    sleep 2
done

systemctl status --no-pager k3s-agent.service >&2 || true
journalctl --no-pager --unit k3s-agent.service --lines 100 >&2 || true
printf '%s\n' 'K3s agent did not become ready within 120 seconds.' >&2
exit 1
