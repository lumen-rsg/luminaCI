#!/usr/bin/env bash

set -euo pipefail

readonly INTERFACE_NAME="wg-lumina"
readonly SERVER_ADDRESS="10.77.0.1/24"
readonly CLIENT_ADDRESS="10.77.0.2/32"
readonly LISTEN_PORT="51821"
readonly SERVER_KEY_PATH="/etc/wireguard/lumina-build-server.key"
readonly CONFIG_PATH="/etc/wireguard/${INTERFACE_NAME}.conf"

if [[ "${EUID}" -ne 0 ]]; then
    printf '%s\n' 'Run this command as root on the K3s server.' >&2
    exit 1
fi
if [[ "$#" -ne 1 || ! "$1" =~ ^[A-Za-z0-9+/]{43}=$ ]]; then
    printf 'Usage: %s CLIENT_WIREGUARD_PUBLIC_KEY\n' "$0" >&2
    exit 1
fi
if ! command -v wg >/dev/null || ! command -v wg-quick >/dev/null; then
    printf '%s\n' 'Install wireguard-tools before configuring the worker mesh.' >&2
    exit 1
fi

client_public_key="$1"
temporary_directory="$(mktemp -d /tmp/lumina-worker-mesh.XXXXXX)"
cleanup() {
    rm -rf -- "${temporary_directory}"
}
trap cleanup EXIT
umask 077

install -d -o root -g root -m 0700 /etc/wireguard
if [[ ! -e "${SERVER_KEY_PATH}" ]]; then
    wg genkey > "${temporary_directory}/server.key"
    install -o root -g root -m 0600 \
        "${temporary_directory}/server.key" "${SERVER_KEY_PATH}"
fi
if [[ ! -f "${SERVER_KEY_PATH}" ]] \
    || [[ "$(stat -c '%a:%U:%G' "${SERVER_KEY_PATH}")" != '600:root:root' ]]; then
    printf 'Unsafe WireGuard server key: %s\n' "${SERVER_KEY_PATH}" >&2
    exit 1
fi

server_private_key="$(<"${SERVER_KEY_PATH}")"
server_public_key="$(printf '%s\n' "${server_private_key}" | wg pubkey)"
cat > "${temporary_directory}/${INTERFACE_NAME}.conf" <<EOF
[Interface]
Address = ${SERVER_ADDRESS}
ListenPort = ${LISTEN_PORT}
PrivateKey = ${server_private_key}

[Peer]
PublicKey = ${client_public_key}
AllowedIPs = ${CLIENT_ADDRESS}
EOF

if [[ -e "${CONFIG_PATH}" ]] \
    && ! cmp --silent "${temporary_directory}/${INTERFACE_NAME}.conf" "${CONFIG_PATH}"; then
    printf 'Refusing to overwrite modified worker mesh configuration: %s\n' \
        "${CONFIG_PATH}" >&2
    exit 1
fi
install -o root -g root -m 0600 \
    "${temporary_directory}/${INTERFACE_NAME}.conf" "${CONFIG_PATH}"
systemctl enable --now "wg-quick@${INTERFACE_NAME}.service"

wg show "${INTERFACE_NAME}"
printf 'ServerPublicKey=%s\n' "${server_public_key}"
printf 'ServerEndpoint=%s\n' "146.120.224.52:${LISTEN_PORT}"
