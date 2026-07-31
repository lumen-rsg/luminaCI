#!/usr/bin/env bash

set -euo pipefail

if [[ "${EUID}" -ne 0 ]]; then
    printf '%s\n' 'Run this command as root on the K3s server.' >&2
    exit 1
fi

readonly CONTROLLER_NAME="lumina-build-controller"
readonly SERVER_CA_CERTIFICATE="/var/lib/rancher/k3s/server/tls/server-ca.crt"
readonly CLIENT_CA_CERTIFICATE="/var/lib/rancher/k3s/server/tls/client-ca.crt"
readonly CLIENT_CA_KEY="/var/lib/rancher/k3s/server/tls/client-ca.key"
output_path="${1:-/opt/lumina-ci/app/deploy/secrets/kubernetes-build-controller.kubeconfig}"
api_server="${KUBERNETES_API_SERVER:-https://host.docker.internal:6443}"
output_group="${KUBERNETES_KUBECONFIG_GROUP:-1654}"

if [[ ! "${api_server}" =~ ^https://[a-zA-Z0-9.-]+:6443$ ]]; then
    printf '%s\n' 'KUBERNETES_API_SERVER must be an HTTPS host on port 6443.' >&2
    exit 1
fi
if [[ -e "${output_path}" ]]; then
    printf 'Refusing to overwrite existing controller kubeconfig: %s\n' "${output_path}" >&2
    exit 1
fi
if [[ ! -r "${SERVER_CA_CERTIFICATE}" ||
      ! -r "${CLIENT_CA_CERTIFICATE}" ||
      ! -r "${CLIENT_CA_KEY}" ]]; then
    printf '%s\n' 'K3s server or client CA files are unavailable.' >&2
    exit 1
fi

temporary_directory="$(mktemp -d /tmp/lumina-kubeconfig.XXXXXX)"
cleanup() {
    rm -rf -- "${temporary_directory}"
}
trap cleanup EXIT
umask 077

openssl req -new -newkey rsa:3072 -nodes \
    -subj "/CN=${CONTROLLER_NAME}" \
    -keyout "${temporary_directory}/client.key" \
    -out "${temporary_directory}/client.csr" >/dev/null 2>&1
printf '%s\n' 'basicConstraints=critical,CA:FALSE' \
    'keyUsage=critical,digitalSignature,keyEncipherment' \
    'extendedKeyUsage=clientAuth' > "${temporary_directory}/client.ext"
certificate_serial="0x$(openssl rand -hex 16)"
openssl x509 -req \
    -in "${temporary_directory}/client.csr" \
    -CA "${CLIENT_CA_CERTIFICATE}" \
    -CAkey "${CLIENT_CA_KEY}" \
    -set_serial "${certificate_serial}" \
    -days 365 \
    -sha256 \
    -extfile "${temporary_directory}/client.ext" \
    -out "${temporary_directory}/client.crt" >/dev/null 2>&1

ca_data="$(base64 -w 0 "${SERVER_CA_CERTIFICATE}")"
certificate_data="$(base64 -w 0 "${temporary_directory}/client.crt")"
key_data="$(base64 -w 0 "${temporary_directory}/client.key")"
cat > "${temporary_directory}/kubeconfig" <<EOF
apiVersion: v1
kind: Config
clusters:
  - name: lumina
    cluster:
      server: ${api_server}
      certificate-authority-data: ${ca_data}
users:
  - name: ${CONTROLLER_NAME}
    user:
      client-certificate-data: ${certificate_data}
      client-key-data: ${key_data}
contexts:
  - name: lumina-builds
    context:
      cluster: lumina
      namespace: lumina-builds
      user: ${CONTROLLER_NAME}
current-context: lumina-builds
EOF

install -d -o root -g "${output_group}" -m 0750 "$(dirname "${output_path}")"
install -o root -g "${output_group}" -m 0440 \
    "${temporary_directory}/kubeconfig" "${output_path}"
printf 'Created controller-only kubeconfig at %s (expires in 365 days).\n' "${output_path}"
