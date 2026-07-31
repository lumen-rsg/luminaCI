#!/usr/bin/env bash

set -euo pipefail

readonly K3S_VERSION="v1.36.1+k3s1"
readonly K3S_AMD64_SHA256="a443db3fe9820cd93617ae67e4386d87c1514c1e96ceb30f4c2791c39065653c"
readonly K3S_ARM64_SHA256="2240e9f7fd8cf36e9998d7ef18781975780d724b59408174618ec15b410891dc"

if [[ "${EUID}" -ne 0 ]]; then
    printf '%s\n' 'Run this bootstrap as root.' >&2
    exit 1
fi

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
operator_user="${K3S_OPERATOR_USER:-${SUDO_USER:-}}"
if [[ -z "${operator_user}" || "${operator_user}" == "root" ]] \
    || ! id "${operator_user}" >/dev/null 2>&1; then
    printf '%s\n' 'K3S_OPERATOR_USER must name the non-root cluster operator.' >&2
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
        printf 'Unsupported server architecture: %s\n' "$(uname -m)" >&2
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

declare -A managed_files=(
    ["${repository_root}/deploy/kubernetes/k3s-server-config.yaml"]="/etc/rancher/k3s/config.yaml"
    ["${repository_root}/deploy/kubernetes/k3s.service"]="/etc/systemd/system/k3s.service"
    ["${repository_root}/deploy/kubernetes/k3s-api-firewall.nft"]="/etc/rancher/k3s/api-firewall.nft"
    ["${repository_root}/deploy/kubernetes/lumina-k3s-api-firewall.service"]="/etc/systemd/system/lumina-k3s-api-firewall.service"
)
for source_path in "${!managed_files[@]}"; do
    destination_path="${managed_files[${source_path}]}"
    if [[ -e "${destination_path}" ]] && ! cmp --silent "${source_path}" "${destination_path}"; then
        printf 'Refusing to overwrite unmanaged or modified file: %s\n' "${destination_path}" >&2
        exit 1
    fi
done
if [[ -e /usr/local/bin/k3s ]] \
    && ! printf '%s  %s\n' "${binary_sha256}" /usr/local/bin/k3s | sha256sum --check --status; then
    printf '%s\n' 'Refusing to replace an unexpected /usr/local/bin/k3s binary.' >&2
    exit 1
fi

temporary_directory="$(mktemp -d /tmp/lumina-k3s.XXXXXX)"
cleanup() {
    rm -rf -- "${temporary_directory}"
}
trap cleanup EXIT

download_url="https://github.com/k3s-io/k3s/releases/download/${K3S_VERSION/+/%2B}/${binary_name}"
curl --fail --location --proto '=https' --tlsv1.2 \
    --output "${temporary_directory}/k3s" \
    "${download_url}"
printf '%s  %s\n' "${binary_sha256}" "${temporary_directory}/k3s" \
    | sha256sum --check --status

getent group lumina-kube >/dev/null || groupadd --system lumina-kube
usermod --append --groups lumina-kube "${operator_user}"
install -o root -g root -m 0755 "${temporary_directory}/k3s" /usr/local/bin/k3s
install -d -o root -g root -m 0755 /etc/rancher/k3s
install -o root -g root -m 0644 \
    "${repository_root}/deploy/kubernetes/k3s-server-config.yaml" \
    /etc/rancher/k3s/config.yaml
install -o root -g root -m 0644 \
    "${repository_root}/deploy/kubernetes/k3s.service" \
    /etc/systemd/system/k3s.service
install -o root -g root -m 0600 \
    "${repository_root}/deploy/kubernetes/k3s-api-firewall.nft" \
    /etc/rancher/k3s/api-firewall.nft
install -o root -g root -m 0644 \
    "${repository_root}/deploy/kubernetes/lumina-k3s-api-firewall.service" \
    /etc/systemd/system/lumina-k3s-api-firewall.service

systemctl daemon-reload
systemctl enable --now lumina-k3s-api-firewall.service
systemctl enable --now k3s.service

for _ in {1..60}; do
    if /usr/local/bin/k3s kubectl get --raw=/readyz >/dev/null 2>&1; then
        /usr/local/bin/k3s kubectl get nodes -o wide
        printf 'K3s %s control plane is ready. Re-login to use the lumina-kube group.\n' \
            "${K3S_VERSION}"
        exit 0
    fi
    sleep 2
done

systemctl status --no-pager k3s.service >&2 || true
journalctl --no-pager --unit k3s.service --lines 100 >&2 || true
printf '%s\n' 'K3s did not become ready within 120 seconds.' >&2
exit 1
