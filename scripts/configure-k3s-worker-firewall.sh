#!/usr/bin/env bash

set -euo pipefail

readonly POD_CIDR="10.42.0.0/16"
readonly SERVICE_CIDR="10.43.0.0/16"

if [[ "${EUID}" -ne 0 ]]; then
    printf 'Usage: sudo %s\n' "$0" >&2
    exit 1
fi
if ! command -v firewall-cmd >/dev/null \
    || ! firewall-cmd --state >/dev/null 2>&1; then
    printf '%s\n' 'Firewalld is not active; no worker firewall change is required.'
    exit 0
fi

# Kube-router still enforces each Kubernetes NetworkPolicy first. Firewalld
# must then permit forwarding for only the cluster-owned address ranges; the
# Fedora Workstation default zone otherwise rejects allowed pod traffic with
# ICMP administratively prohibited (reported by curl as "No route to host").
for cluster_cidr in "${POD_CIDR}" "${SERVICE_CIDR}"; do
    firewall-cmd --permanent --zone=trusted --add-source="${cluster_cidr}"
done
firewall-cmd --reload

for cluster_cidr in "${POD_CIDR}" "${SERVICE_CIDR}"; do
    firewall-cmd --zone=trusted --query-source="${cluster_cidr}" >/dev/null
done
printf 'Firewalld permits K3s Pod %s and Service %s forwarding.\n' \
    "${POD_CIDR}" "${SERVICE_CIDR}"
