#!/usr/bin/env bash
# Exercise the same renderer used by lab up without changing local or cluster configuration.
set -Eeuo pipefail
source "$(dirname -- "${BASH_SOURCE[0]}")/../lab.sh"

DNS_TEST_TMP="$(mktemp -d)"
export DNS_TEST_TMP
trap 'rm -rf -- "${DNS_TEST_TMP}"' EXIT
PROFILE=full
PROFILE_CLUSTER_NAMES=(pole-lab-default pole-lab-workload-a)
KUBECTL_BIN="${DNS_TEST_TMP}/kubectl"
getent() { printf '127.0.0.1 STREAM harbor.pole.localhost\n'; }
harbor_registry_ip() { printf '10.10.0.4\n'; }
run_container() { [[ "$1" == ps ]]; }

cat >"${KUBECTL_BIN}" <<'PROBE'
#!/usr/bin/env bash
set -Eeuo pipefail
[[ "$1" == --kubeconfig && "$3" == --context && "$4" == k3d-pole-lab-* ]]
[[ "${*:5}" == '--namespace kube-system apply --server-side --field-manager=pole-lab-dns --filename=-' ]]
cat >"${DNS_TEST_TMP}/$4.manifest"
PROBE
chmod +x "${KUBECTL_BIN}"
configure_harbor_registry_resolution
for cluster in "${PROFILE_CLUSTER_NAMES[@]}"; do
    manifest="${DNS_TEST_TMP}/k3d-${cluster}.manifest"
    grep -Fxq '  name: coredns-custom' "${manifest}"
    grep -Fxq '  pole-lab-harbor.server: |' "${manifest}"
    grep -Fxq '    harbor.pole.localhost:53 {' "${manifest}"
    grep -Fxq '            10.10.0.4 harbor.pole.localhost' "${manifest}"
done
printf 'Harbor custom DNS configuration passed on both targets.\n'
