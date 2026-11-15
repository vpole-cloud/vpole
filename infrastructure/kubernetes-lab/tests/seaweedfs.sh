#!/usr/bin/env bash
# Real operator and S3 acceptance on its own disposable k3d cluster, never the shared lab.
set -Eeuo pipefail
source "$(dirname -- "${BASH_SOURCE[0]}")/../lab.sh"
nodes="${1:-1}"
[[ "${nodes}" == 1 || "${nodes}" == 3 ]] || die 'Usage: seaweedfs.sh 1|3'
[[ "$#" -le 1 ]] || die 'Usage: seaweedfs.sh 1|3'
run_id="$(generate_lab_instance_id)"
cluster="pole-seaweed-${run_id:0:12}"
# A child state directory gives downloads/logs their own paths; no state.json is written.
LAB_STATE_ROOT="${LAB_ALLOWED_STATE_ROOT}/seaweedfs-${run_id}"
mkdir -p "${LAB_STATE_ROOT}"
# Shared tools are immutable checked downloads; this cluster keeps its own kubeconfigs, Helm and chart paths.
ln -s "${LAB_ALLOWED_STATE_ROOT}/bin" "${LAB_STATE_ROOT}/bin"
prepare_runtime
need python3
need openssl
worktree_id="$(resolve_worktree_id)"
LAB_INSTANCE_ID="${run_id}"
PROFILE=minimal
PROFILE_CLUSTER_NAMES=("${cluster}")
PROFILE_TARGET_KEYS=(default)
work="${LAB_STATE_ROOT}/acceptance"
mkdir -p "${work}" "${LAB_KUBECONFIG_DIR}"
namespace=acceptance
forward_pid=''
lost_node=''
cluster_created=false
k() { "${KUBECTL_BIN}" --kubeconfig "$(kubeconfig_path_for "${cluster}")" --context "$(expected_context_for "${cluster}")" --namespace "${namespace}" "$@"; }
cleanup() {
    local status=$?
    if [[ -n "${forward_pid}" ]]; then
        kill "${forward_pid}" 2>/dev/null || true
        wait "${forward_pid}" 2>/dev/null || true
    fi
    if [[ "${cluster_created}" == true ]]; then
        if [[ "${status}" -ne 0 ]]; then
            k get pods -o wide >&2 || true
            k get seaweeds,buckets,s3identities,s3credentials,s3policies,s3policybindings -o json 2>/dev/null \
                | jq 'del(.items[].status.accessKey)' >"${work}/failure-resources.json" || true
            k logs -n pole-lab-seaweedfs deploy/pole-lab-seaweedfs --tail=150 >"${work}/operator.log" 2>/dev/null || true
        fi
        validate_cluster_container_ownership "${cluster}"
        run_k3d cluster delete "${cluster}"
    fi
    # Credentials are always removed, including failed runs; diagnostics contain no Secret objects.
    rm -f "${work}"/*.conf "${work}/s3.json" "${work}/filer.toml" "${work}/desired-key.json" "${work}/observed-key.json" "${work}/owner-secret.json" "${work}/metadata-user" "${work}/metadata-password" "${work}/accessKey" "${work}/secretKey"
    return "${status}"
}
trap cleanup EXIT
trap 'exit 130' INT TERM
agents=0
[[ "${nodes}" != 3 ]] || agents=3
args=(cluster create "${cluster}" --image "${K3S_IMAGE}" --servers 1 --agents "${agents}"
    --api-port 127.0.0.1:0 --kubeconfig-update-default=false --kubeconfig-switch-context=false
    --runtime-label 'pole.dev/owned-by=kubernetes-lab@server:*;agent:*;loadbalancer:*'
    --runtime-label "pole.dev/lab-instance-id=${run_id}@server:*;agent:*;loadbalancer:*" --wait)
if [[ "${CONTAINER_RUNTIME}" == podman && "${CONTAINER_RUNTIME_ROOTLESS}" == true ]]; then
    args+=(--k3s-arg '--kubelet-arg=feature-gates=KubeletInUserNamespace=true@server:*;agent:*'
        --k3s-arg '--kube-proxy-arg=conntrack-max-per-core=0@server:*;agent:*')
fi
if [[ "${nodes}" == 3 ]]; then
    args+=(--k3s-arg '--node-taint=pole.dev/control-plane=true:NoSchedule@server:*')
fi
# The unique cluster/network and API-assigned port are independent of shared state and ports.
cluster_created=true
run_k3d "${args[@]}"
run_k3d kubeconfig get "${cluster}" >"$(kubeconfig_path_for "${cluster}")"
# k3d 5.9.0 leaves the requested :0 in kubeconfig; use the actual runtime binding.
api_binding="$(run_container port "k3d-${cluster}-serverlb" 6443/tcp)"
[[ "${api_binding}" =~ ^127\.0\.0\.1:[0-9]+$ && "${api_binding}" != 127.0.0.1:0 ]] \
    || die 'The isolated API server did not receive a loopback-only runtime port.'
"${KUBECTL_BIN}" --kubeconfig "$(kubeconfig_path_for "${cluster}")" config set-cluster \
    "$(expected_context_for "${cluster}")" --server="https://${api_binding}" >/dev/null

chmod 0600 "$(kubeconfig_path_for "${cluster}")"
install_seaweedfs_overlay 0
if [[ "${nodes}" == 3 ]]; then
    PROFILE_OVERLAYS=(cnpg)
    install_cnpg_overlay 0
fi
k create namespace "${namespace}" >/dev/null
k label namespace "${namespace}" "pole.dev/owned-by=pole-tests" "pole.dev/worktree-id=${worktree_id}" "pole.dev/test-run-id=${run_id}" >/dev/null
python3 - "${work}/s3.json" <<'PY'
import json, secrets, sys
json.dump({"identities": [{"name": "bootstrap-admin", "credentials": [{"accessKey": secrets.token_hex(12), "secretKey": secrets.token_hex(24)}], "actions": ["Admin"]}]}, open(sys.argv[1], "w"))
PY
k create secret generic bootstrap --from-file="s3.json=${work}/s3.json" >/dev/null
render() { python3 "${LAB_SCRIPT_DIR}/tests/seaweedfs-manifests.py" "$1" --nodes "${nodes}" --image "${SEAWEEDFS_IMAGE}" --data-node "${data_node:-}" | k apply -f -; }
if [[ "${nodes}" == 3 ]]; then
    metadata_password="$(openssl rand -hex 24)"
    printf '%s' seaweedfs >"${work}/metadata-user"
    printf '%s' "${metadata_password}" >"${work}/metadata-password"
    k create secret generic metadata-credentials --type=kubernetes.io/basic-auth \
        --from-file="username=${work}/metadata-user" --from-file="password=${work}/metadata-password" >/dev/null
    rm -f "${work}/metadata-user" "${work}/metadata-password"
    python3 - "${work}/filer.toml" "${metadata_password}" <<'PYCONFIG'
import json, sys
open(sys.argv[1], "w").write('[postgres]\nenabled = true\ncreateTable = true\nhostname = "store-metadata-rw"\nport = 5432\nusername = "seaweedfs"\ndatabase = "seaweedfs"\nsslmode = "require"\nconnection_max_open = 0\npassword = ' + json.dumps(sys.argv[2]) + '\n')
PYCONFIG
    unset metadata_password
    k create secret generic filer-store --from-file="filer.toml=${work}/filer.toml" >/dev/null
    render metadata
    k wait --for=condition=Ready cluster/store-metadata --timeout=8m
fi
render cluster
k wait --for=condition=Ready seaweed/store --timeout=8m
# Check labels on actual generated gateway pods, not merely the CR.
k get deployment store-s3 -o json | jq -e '.spec.template.metadata.labels["pole.io/resource-deployment-id"] == "seaweedfs-acceptance"' >/dev/null
# First object placement is pinned to the metadata primary worker, then the hint is removed.
# This makes the lost-data-replica assertion deterministic while retaining ordinary surviving writes.
data_node=''
if [[ "${nodes}" == 3 ]]; then
    initial_primary="$(k get cluster/store-metadata -o jsonpath='{.status.currentPrimary}')"
    initial_primary_node="$(k get pod "${initial_primary}" -o jsonpath='{.spec.nodeName}')"
    primary_volume="$(k get pods -l app.kubernetes.io/component=volume -o json | jq -r --arg node "${initial_primary_node}" '.items[] | select(.spec.nodeName == $node) | .metadata.name')"
    volume_peer="$(k get statefulset/store-volume -o jsonpath='{.spec.serviceName}')"
    data_node="${primary_volume}.${volume_peer}.${namespace}:8444"
fi
# Model Pole's complete externally owned projected Secret, adopted read-only by S3Credentials.
project_owner() {
    local existing
    existing="$(k get secret owner --ignore-not-found -o json)"
    printf '%s' "${existing}" | python3 -c '
import base64,json,secrets,sys
raw=sys.stdin.read(); d=json.loads(raw) if raw.strip() else {"apiVersion":"v1","kind":"Secret","metadata":{"name":"owner","labels":{"pole.io/resource-deployment-id":"seaweedfs-acceptance"}},"type":"Opaque"}
d["data"]={k:base64.b64encode(secrets.token_hex(n).encode()).decode() for k,n in (("accessKey",12),("secretKey",24))}
print(json.dumps(d))' >"${work}/owner-secret.json"
    if [[ -n "${existing}" ]]; then k replace -f "${work}/owner-secret.json" >/dev/null
    else k create -f "${work}/owner-secret.json" >/dev/null; fi
    rm -f "${work}/owner-secret.json"
}
project_owner
render iam
for resource in bucket/retired-bucket bucket/sibling-bucket s3identity/owner s3identity/extra s3credentials/owner s3credentials/extra s3policybinding/owner s3policybinding/extra; do
    k wait --for=condition=Ready "${resource}" --timeout=180s
done
k port-forward --address 127.0.0.1 service/store-s3 0:8333 >"${work}/forward.log" 2>&1 &
forward_pid=$!
port=''
for _ in {1..30}; do
    kill -0 "${forward_pid}" 2>/dev/null || die 'S3 loopback forwarding exited.'
    port="$(sed -nE 's/^Forwarding from 127\.0\.0\.1:([0-9]+) -> 8333$/\1/p' "${work}/forward.log" | head -1)"
    [[ -z "${port}" ]] || break
    sleep 1
done
[[ -n "${port}" ]] || die 'Timed out starting S3 loopback forwarding.'
credential() {
    k get secret "$1" -o json | python3 -c 'import json,base64,sys; d=json.load(sys.stdin)["data"]; print("user = \"%s:%s\"" % tuple(base64.b64decode(d[k]).decode() for k in ("accessKey","secretKey")))' >"${work}/$1.conf"
}
credential owner
credential extra
python3 - "${work}/s3.json" "${work}/admin.conf" <<'PYADMIN'
import json,sys
credential=json.load(open(sys.argv[1]))["identities"][0]["credentials"][0]
open(sys.argv[2],"w").write('user = "' + credential["accessKey"] + ':' + credential["secretKey"] + '"\n')
PYADMIN
s3() {
    local identity="$1"; shift
    curl --silent --show-error --noproxy '*' --connect-timeout 5 --max-time 20 \
        --aws-sigv4 aws:amz:us-east-1:s3 --config "${work}/${identity}.conf" \
        --output "${work}/response" --write-out '%{http_code}' "$@"
}
expect() {
    local expected="$1" identity="$2"; shift 2
    local actual
    actual="$(s3 "${identity}" "$@")"
    [[ "${actual}" == "${expected}" ]] || die "S3 ${identity} ${*: -1}: expected HTTP ${expected}, got ${actual}."
}
endpoint="http://127.0.0.1:${port}"
printf 'operator-acceptance-%s' "${run_id}" >"${work}/payload"
expect 200 owner -X PUT --data-binary "@${work}/payload" "${endpoint}/retired-bucket/object"
expect 200 extra "${endpoint}/retired-bucket/object"
cmp "${work}/payload" "${work}/response"
expect 200 extra -X PUT --data-binary "@${work}/payload" "${endpoint}/sibling-bucket/object"
expect 403 owner "${endpoint}/sibling-bucket/object"
# Rotate the same adopted Secret in place; Ready alone may describe the old key.
cp "${work}/owner.conf" "${work}/owner-old.conf"
project_owner
converged=false
for _ in {1..60}; do
    k get secret owner -o json >"${work}/desired-key.json"
    k get s3credentials owner -o json >"${work}/observed-key.json"
    if python3 - "${work}/desired-key.json" "${work}/observed-key.json" <<'PYKEY'
import base64,json,sys
secret=json.load(open(sys.argv[1])); cred=json.load(open(sys.argv[2]))
expected=base64.b64decode(secret["data"]["accessKey"]).decode()
sys.exit(0 if cred.get("status",{}).get("accessKey")==expected else 1)
PYKEY
    then converged=true; break; fi
    sleep 1
done
rm -f "${work}/desired-key.json" "${work}/observed-key.json"
[[ "${converged}" == true ]] || die 'Projected credential rotation did not converge to its current access key.'
credential owner
cp "${work}/owner.conf" "${work}/owner-new.conf"
expect 200 owner-new "${endpoint}/retired-bucket/object"
revoked=false
for _ in {1..30}; do
    if [[ "$(s3 owner-old "${endpoint}/retired-bucket/object")" == 403 ]]; then revoked=true; break; fi
    sleep 1
done
[[ "${revoked}" == true ]] || die 'Rotated key retained access after 30 seconds.'
expect 200 owner-new "${endpoint}/retired-bucket/object"
# Native Admin may create/manage a separate bucket; the new managed key stays bucket-scoped.
expect 200 admin -X PUT "${endpoint}/admin-bucket"
expect 200 admin -X PUT --data-binary "@${work}/payload" "${endpoint}/admin-bucket/object"
expect 403 owner-new "${endpoint}/admin-bucket/object"
expect 403 owner-new -X PUT --data-binary "@${work}/payload" "${endpoint}/admin-bucket/blocked"
expect 403 owner-new "${endpoint}/admin-bucket?list-type=2"
printf '%s' '{"Version":"2012-10-17","Statement":[{"Effect":"Deny","Principal":"*","Action":"s3:DeleteObject","Resource":["arn:aws:s3:::admin-bucket","arn:aws:s3:::admin-bucket/*"]}]}' >"${work}/admin-policy.json"
expect 204 admin -X PUT --data-binary "@${work}/admin-policy.json" "${endpoint}/admin-bucket?policy"
expect 200 admin "${endpoint}/admin-bucket?policy"
expect 200 admin "${endpoint}/admin-bucket/object"
if [[ "${nodes}" == 3 ]]; then
    printf 'fs.configure -locationPrefix=/buckets/retired-bucket/ -delete -apply\n' \
        | k exec -i store-master-0 -- weed shell -master=store-master:9333 -filer=store-filer:8888 \
        >"${work}/remove-placement-hint.log"
    k patch bucket retired-bucket --type=json -p='[{"op":"remove","path":"/spec/placement/dataNode"}]' >/dev/null
    placement_ready=false
    for _ in {1..60}; do
        if k get bucket retired-bucket -o json | jq -e '.status.observedGeneration == .metadata.generation and any(.status.conditions[]?; .type == "Ready" and .status == "True")' >/dev/null; then placement_ready=true; break; fi
        sleep 1
    done
    [[ "${placement_ready}" == true ]] || die 'The ordinary unpinned bucket placement did not converge.'

    # Capture physical volume placements from each actual volume server, tied to Kubernetes nodes.
    snapshot_volumes() {
        local output="$1"
        : >"${output}"
        local pod node
        while read -r pod node; do
            [[ "${node}" != "${lost_node}" ]] || continue
            k --request-timeout=10s exec "${pod}" -- wget -T 5 -qO- http://127.0.0.1:8444/status \
                | jq -c --arg node "${node}" '{node:$node,volumes:.Volumes}' >>"${output}" || return 1
        done < <(k get pods -l app.kubernetes.io/component=volume -o json | jq -r '.items[] | select(.status.phase == "Running" and .metadata.deletionTimestamp == null and .spec.nodeName != null) | [.metadata.name,.spec.nodeName] | @tsv')
    }
    check_copies() {
        python3 - "$1" "${work}/before-volumes.jsonl" <<'PYCOPIES'
import collections,json,sys
copies=collections.defaultdict(set)
for line in open(sys.argv[1]):
    row=json.loads(line)
    for volume in row["volumes"] or []:
        if volume.get("Collection")=="retired-bucket": copies[volume["Id"]].add(row["node"])
assert copies, "No object collection volumes observed"
original=set()
for line in open(sys.argv[2]):
    row=json.loads(line)
    original.update(v["Id"] for v in row["volumes"] or [] if v.get("Collection")=="retired-bucket" and v.get("FileCount",0)>0)
assert original.issubset(copies), "An original data-bearing volume disappeared"
assert all(len(nodes)>=2 for nodes in copies.values()), "Object volume lacks two independent node copies"
PYCOPIES
    }
    k get pods -l app.kubernetes.io/instance=store -o json >"${work}/before-pods.json"
    k get pods -l cnpg.io/cluster=store-metadata -o json >"${work}/before-metadata-pods.json"
    python3 - "${work}/before-pods.json" "${work}/before-metadata-pods.json" <<'PYNODES'
import json,sys
pods=json.load(open(sys.argv[1]))["items"]
for component in ("master","volume","filer","s3"):
    nodes={p["spec"]["nodeName"] for p in pods if p["metadata"]["labels"].get("app.kubernetes.io/component")==component}
    assert len(nodes)==3, (component,"not on three independent nodes")
metadata=json.load(open(sys.argv[2]))["items"]
assert len({p["spec"]["nodeName"] for p in metadata})==3, "Metadata quorum is not on three independent nodes"
PYNODES
    primary="$(k get cluster/store-metadata -o jsonpath='{.status.currentPrimary}')"
    candidate_node="$(k get pod "${primary}" -o jsonpath='{.spec.nodeName}')"
    # Exercise enough assignments to prove an acknowledged data-bearing replica is on the lost node.
    for sample in {1..32}; do
        expect 200 owner-new -X PUT --data-binary "@${work}/payload" "${endpoint}/retired-bucket/sample-${sample}"
    done
    snapshot_volumes "${work}/before-volumes.jsonl"
    check_copies "${work}/before-volumes.jsonl"
    python3 - "${work}/before-volumes.jsonl" "${candidate_node}" <<'PYBEFORE'
import json,sys
assert any(row["node"]==sys.argv[2] and any(v.get("Collection")=="retired-bucket" and v.get("FileCount",0)>0 for v in row["volumes"] or []) for row in map(json.loads,open(sys.argv[1]))), "No acknowledged data replica on selected lost worker"
PYBEFORE
    lost_node="${candidate_node}"
    [[ "${lost_node}" == "k3d-${cluster}-agent-"* ]] || die 'Refusing to power off a node outside the isolated worker set.'
    validate_cluster_container_ownership "${cluster}"
    info "Simulating abrupt loss of the isolated metadata primary worker."
    run_container kill --signal KILL "${lost_node}" >/dev/null
    # Route directly to a surviving gateway; port-forward is only transport, not isolation proof.
    kill "${forward_pid}" 2>/dev/null || true
    wait "${forward_pid}" 2>/dev/null || true
    gateway="$(k get pods -l app.kubernetes.io/component=s3 -o json | jq -r --arg node "${lost_node}" '.items[] | select(.spec.nodeName != $node) | .metadata.name' | head -1)"
    k port-forward --address 127.0.0.1 "pod/${gateway}" 0:8333 >"${work}/survivor-forward.log" 2>&1 &
    forward_pid=$!
    port=''
    for _ in {1..30}; do
        port="$(sed -nE 's/^Forwarding from 127\.0\.0\.1:([0-9]+) -> 8333$/\1/p' "${work}/survivor-forward.log" | head -1)"
        [[ -z "${port}" ]] || break
        sleep 1
    done
    [[ -n "${port}" ]] || die 'Survivor gateway transport failed.'
    endpoint="http://127.0.0.1:${port}"
    failover=false
    for _ in {1..150}; do
        observed_primary="$(k get cluster/store-metadata -o jsonpath='{.status.currentPrimary}')"
        if [[ "${observed_primary}" != "${primary}" && -n "${observed_primary}" ]]; then failover=true; break; fi
        sleep 2
    done
    [[ "${failover}" == true ]] || die 'Synchronous metadata failed to elect a new primary within five minutes.'
    available=false
    for _ in {1..60}; do
        if [[ "$(s3 owner-new "${endpoint}/retired-bucket/object")" == 200 ]]; then available=true; break; fi
        sleep 2
    done
    [[ "${available}" == true ]] || die 'Acknowledged object was unavailable after metadata failover.'
    cmp "${work}/payload" "${work}/response"
    for sample in {1..32}; do
        expect 200 owner-new "${endpoint}/retired-bucket/sample-${sample}"
        cmp "${work}/payload" "${work}/response"
    done
    writable=false
    for _ in {1..60}; do
        if [[ "$(s3 owner-new -X PUT --data-binary "@${work}/payload" "${endpoint}/retired-bucket/during-loss")" == 200 ]]; then writable=true; break; fi
        sleep 2
    done
    [[ "${writable}" == true ]] || die 'New S3 writes unavailable with one worker lost.'
    expect 200 owner-new "${endpoint}/retired-bucket/during-loss"
    cmp "${work}/payload" "${work}/response"
    repaired=false
    for _ in {1..240}; do
        if snapshot_volumes "${work}/survivor-volumes.jsonl" && check_copies "${work}/survivor-volumes.jsonl" 2>/dev/null; then repaired=true; break; fi
        sleep 2
    done
    repair_recovery=unassisted
    if [[ "${repaired}" != true ]]; then
        # A fully powered-off worker can strand a Job's terminating Pod. Production keeps Forbid;
        # infrastructure fencing/removal is deliberately an operator action, not Pole automation.
        k get jobs -o json >"${work}/unassisted-jobs.json"
        k get pods -o json >"${work}/unassisted-pods.json"
        validate_cluster_container_ownership "${cluster}"
        [[ "$(run_container inspect "${lost_node}" --format '{{.State.Running}}')" == false ]] \
            || die 'Refusing Node removal because the isolated worker is not confirmed powered off.'
        repair_recovery=power-fenced-node-removal
        info 'Unassisted repair window elapsed; removing only the confirmed powered-off isolated Node.'
        k delete node "${lost_node}" --wait --timeout=30s
        for _ in {1..90}; do
            if snapshot_volumes "${work}/fenced-survivor-volumes.jsonl" && check_copies "${work}/fenced-survivor-volumes.jsonl" 2>/dev/null; then repaired=true; break; fi
            sleep 2
        done
    fi
    [[ "${repaired}" == true ]] || die 'Scheduled repair did not restore two copies on surviving workers after confirmed power fencing.'
    printf '%s\n' "${repair_recovery}" >"${work}/recovery-mode"
    info "Two-copy restoration verified before worker restart (${repair_recovery})."
    # Only after the two-worker repair proof, return the same old runtime for restoration/prune tests.
    run_container start "${lost_node}" >/dev/null
    lost_node=''
    k wait --for=condition=Ready cluster/store-metadata --timeout=5m
    k wait --for=condition=Ready seaweed/store --timeout=5m
    snapshot_volumes "${work}/restored-volumes.jsonl"
    check_copies "${work}/restored-volumes.jsonl"
    expect 200 owner-new "${endpoint}/retired-bucket/during-loss"
    info 'Three-node placement, synchronous metadata primary loss, surviving reads/writes and redundancy repair passed.'
fi
# Instance retirement omits the standalone S3 spec. The native operator must prune its gateway.
k get seaweed/store -o json | jq '.spec.s3' >"${work}/gateway-spec.json"
k patch seaweed/store --type=json -p='[{"op":"remove","path":"/spec/s3"}]' >/dev/null
k wait --for=delete deployment/store-s3 service/store-s3 --timeout=180s
k wait --for=delete pod -l app.kubernetes.io/component=s3 --timeout=180s
kill "${forward_pid}" 2>/dev/null || true
wait "${forward_pid}" 2>/dev/null || true
forward_pid=''
jq -n --slurpfile gateway "${work}/gateway-spec.json" '{spec:{s3:$gateway[0]}}' >"${work}/restore-gateway.json"
k patch seaweed/store --type=merge --patch-file "${work}/restore-gateway.json" >/dev/null
for _ in {1..60}; do
    if k get deployment/store-s3 >/dev/null 2>&1; then break; fi
    sleep 1
done
k rollout status deployment/store-s3 --timeout=180s
k port-forward --address 127.0.0.1 service/store-s3 0:8333 >"${work}/restored-forward.log" 2>&1 &
forward_pid=$!
port=''
for _ in {1..30}; do
    port="$(sed -nE 's/^Forwarding from 127\.0\.0\.1:([0-9]+) -> 8333$/\1/p' "${work}/restored-forward.log" | head -1)"
    [[ -z "${port}" ]] || break
    sleep 1
done
[[ -n "${port}" ]] || die 'Restored gateway transport failed.'
endpoint="http://127.0.0.1:${port}"
expect 200 owner-new "${endpoint}/retired-bucket/object"
cmp "${work}/payload" "${work}/response"
if [[ "${nodes}" == 3 ]]; then
    for sample in {1..32}; do
        expect 200 owner-new "${endpoint}/retired-bucket/sample-${sample}"
        cmp "${work}/payload" "${work}/response"
    done
fi
expect 403 owner-old "${endpoint}/retired-bucket/object"
expect 200 extra "${endpoint}/sibling-bucket/object"
info 'Native gateway retirement pruned Deployment/Service/pods and restoration preserved keys/data.'
render retire
k wait --for=condition=Ready s3policybinding/retire --timeout=180s
for identity in owner-new extra; do
    expect 403 "${identity}" "${endpoint}/retired-bucket/object"
    expect 403 "${identity}" -X PUT --data-binary "@${work}/payload" "${endpoint}/retired-bucket/blocked"
    expect 403 "${identity}" -X DELETE "${endpoint}/retired-bucket/object"
    expect 403 "${identity}" "${endpoint}/retired-bucket?list-type=2"
done
expect 200 extra "${endpoint}/sibling-bucket/object"
cmp "${work}/payload" "${work}/response"
expect 403 admin "${endpoint}/retired-bucket/object"
# A trusted local provider acceptance can reuse this exact owned cluster after the HA proof.
# Its failure still reaches our ownership-checked cleanup; no keep-cluster or shared-lab bypass exists.
if [[ -n "${POLE_SEAWEEDFS_POST_HA_HOOK:-}" ]]; then
    [[ "${nodes}" == 3 && "${POLE_SEAWEEDFS_POST_HA_HOOK}" == /* && -x "${POLE_SEAWEEDFS_POST_HA_HOOK}" ]] \
        || die 'The post-HA hook requires three-node mode and an absolute executable local path.'
    POLE_SEAWEEDFS_TEST_KUBECONFIG="$(kubeconfig_path_for "${cluster}")" \
    POLE_SEAWEEDFS_TEST_CONTEXT="$(expected_context_for "${cluster}")" \
    POLE_SEAWEEDFS_TEST_KUBECTL="${KUBECTL_BIN}" \
    POLE_SEAWEEDFS_TEST_NAMESPACE="${namespace}" \
    POLE_SEAWEEDFS_TEST_METADATA_CLUSTER=store-metadata \
    POLE_SEAWEEDFS_TEST_NATIVE_CLUSTER=store \
    POLE_SEAWEEDFS_TEST_WORK="${work}" \
        "${POLE_SEAWEEDFS_POST_HA_HOOK}"
fi
info "SeaweedFS ${nodes}-node S3, bucket scope, key rotation, additional-identity retirement and sibling checks passed."
