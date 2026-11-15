#!/usr/bin/env bash
# Infrastructure-free checks for pinned operator selection and fail-closed archive verification.
set -Eeuo pipefail
source "$(dirname -- "${BASH_SOURCE[0]}")/../lab.sh"
work="$(mktemp -d)"
trap 'rm -rf -- "${work}"' EXIT
parse_profile_and_overlays full
[[ " ${PROFILE_OVERLAYS[*]} " == *' seaweedfs '* ]]
target_has_overlay 0 seaweedfs
! target_has_overlay 1 seaweedfs
parse_profile_and_overlays minimal --overlay seaweedfs
[[ "${PROFILE_OVERLAYS[*]}" == seaweedfs ]]
if (parse_profile_and_overlays minimal --overlay seaweedfs --overlay seaweedfs) >"${work}/rejected" 2>&1; then
    die 'Duplicate overlay accepted.'
fi
LAB_STATE_ROOT="${work}/state"
mkdir -p "${LAB_STATE_ROOT}/charts"
printf corrupted >"${LAB_STATE_ROOT}/charts/seaweedfs-operator-${SEAWEEDFS_OPERATOR_CHART_VERSION}.tgz"
curl() {
    local destination=''
    while [[ "$#" -gt 0 ]]; do
        if [[ "$1" == --output ]]; then destination="$2"; shift 2; else shift; fi
    done
    printf untrusted >"${destination}"
}
if (ensure_seaweedfs_chart) >"${work}/rejected" 2>&1; then
    die 'An invalid pinned archive was accepted.'
fi
[[ "$(cat "${LAB_STATE_ROOT}/charts/seaweedfs-operator-${SEAWEEDFS_OPERATOR_CHART_VERSION}.tgz")" == corrupted ]]
ensure_seaweedfs_chart() { printf '%s' /verified/operator.tgz; }
run_helm_for_target() { printf '%s\n' "$@" >"${work}/helm-arguments"; }
install_seaweedfs_overlay 0
grep -Fx 'image.tag=1.0.44' "${work}/helm-arguments" >/dev/null
grep -Fx /verified/operator.tgz "${work}/helm-arguments" >/dev/null
grep -Fx fullnameOverride=pole-lab-seaweedfs "${work}/helm-arguments" >/dev/null
# The current full state has six overlays; removing SeaweedFS must fail validation.
LAB_STATE_FILE="${work}/state/state.json"
LAB_KUBECONFIG_DIR="${work}/state/kubeconfigs"
LAB_INSTANCE_ID=0123456789abcdef0123456789abcdef
LAB_REGISTRY_CONTAINER_ID="$(printf '%064d' 1)"
CONTAINER_RUNTIME=docker
CONTAINER_RUNTIME_ROOTLESS=false
parse_profile_and_overlays full
write_state ready
validate_state_contract
PROFILE_OVERLAYS=(monitoring cert-manager cnpg harbor buildkit)
write_state ready
if (validate_state_contract) >"${work}/rejected" 2>&1; then
    die 'A full contract missing SeaweedFS was accepted.'
fi
printf 'SeaweedFS overlay regression checks passed (8 cases).\n'
