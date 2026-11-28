#!/usr/bin/env bash
# Copyright (C) 2026 Victor Shnaider and vPole contributors
# SPDX-License-Identifier: AGPL-3.0-only

# Real Barman S3 and recovery contract, scoped to one disposable test namespace.
set -Eeuo pipefail
source "$(dirname -- "${BASH_SOURCE[0]}")/../lab.sh"
prepare_runtime
validate_state_contract
target_has_overlay 0 cnpg && target_has_overlay 0 cert-manager || die 'Select the CNPG and cert-manager overlays.'
lab_kube=("${KUBECTL_BIN}" --kubeconfig "$(kubeconfig_path_for "${PROFILE_CLUSTER_NAMES[0]}")" --context "$(expected_context_for "${PROFILE_CLUSTER_NAMES[0]}")")
"${lab_kube[@]}" get crd/objectstores.barmancloud.cnpg.io >/dev/null
worktree_id="$(resolve_worktree_id)"
run_id="$(generate_lab_instance_id)"
namespace="pole-${worktree_id}-backup-${run_id:0:12}"
work="$(mktemp -d)"
namespace_uid=''
forward_pid=''

k() { "${lab_kube[@]}" --namespace "${namespace}" "$@"; }
stop_forward() {
    if [[ -n "${forward_pid}" ]]; then
        kill "${forward_pid}" 2>/dev/null || true
        wait "${forward_pid}" 2>/dev/null || true
        forward_pid=''
    fi
}
cleanup() {
    stop_forward
    rm -rf -- "${work}"
    if [[ -n "${namespace_uid}" ]]; then
        local observed
        observed="$("${lab_kube[@]}" get namespace "${namespace}" --ignore-not-found -o json)"
        if jq -e --arg uid "${namespace_uid}" --arg run "${run_id}" --arg tree "${worktree_id}" \
            '.metadata.uid == $uid and .metadata.labels["pole.dev/test-run-id"] == $run and .metadata.labels["pole.dev/worktree-id"] == $tree and .metadata.labels["pole.dev/owned-by"] == "pole-tests"' <<<"${observed}" >/dev/null; then
            k delete clusters.postgresql.cnpg.io --all --cascade=foreground --wait --timeout=5m >/dev/null
            "${lab_kube[@]}" delete namespace "${namespace}" --wait --timeout=180s >/dev/null
        fi
    fi
}
trap cleanup EXIT
trap 'exit 130' INT TERM
namespace_uid="$(jq -n --arg name "${namespace}" --arg tree "${worktree_id}" --arg run "${run_id}" \
    '{apiVersion:"v1",kind:"Namespace",metadata:{name:$name,labels:{"pole.dev/owned-by":"pole-tests","pole.dev/worktree-id":$tree,"pole.dev/test-run-id":$run}}}' \
    | k create -f - -o jsonpath='{.metadata.uid}')"
info "Testing backups in ${namespace}"

# Grant the same two generated keys to this private S3 fixture and its Barman client.
openssl rand -hex 12 | tr -d '\n' >"${work}/access"
openssl rand -hex 24 | tr -d '\n' >"${work}/secret"
k create secret generic archive-access --from-file=access="${work}/access" --from-file=secret="${work}/secret" >/dev/null
printf 'user = "%s:%s"\n' "$(<"${work}/access")" "$(<"${work}/secret")" >"${work}/curl.conf"
k create -f - >/dev/null <<YAML
apiVersion: v1
kind: Pod
metadata: {name: s3, labels: {app: s3}}
spec:
  automountServiceAccountToken: false
  securityContext: {fsGroup: 10001}
  containers:
    - name: s3
      image: ${BACKUP_S3_IMAGE}
      args: [/data]
      env:
        - {name: RUSTFS_ACCESS_KEY, valueFrom: {secretKeyRef: {name: archive-access, key: access}}}
        - {name: RUSTFS_SECRET_KEY, valueFrom: {secretKeyRef: {name: archive-access, key: secret}}}
        - {name: RUSTFS_ADDRESS, value: ':9000'}
        - {name: RUSTFS_CONSOLE_ENABLE, value: 'false'}
      volumeMounts:
        - {name: data, mountPath: /data}
      ports: [{containerPort: 9000}]
      readinessProbe: {tcpSocket: {port: 9000}, periodSeconds: 2}
  volumes:
    - {name: data, emptyDir: {}}
---
apiVersion: v1
kind: Service
metadata: {name: s3}
spec: {selector: {app: s3}, ports: [{port: 9000, targetPort: 9000}]}
---
apiVersion: barmancloud.cnpg.io/v1
kind: ObjectStore
metadata: {name: archive}
spec:
  configuration:
    destinationPath: s3://backups/
    endpointURL: http://s3:9000
    s3Credentials:
      accessKeyId: {name: archive-access, key: access}
      secretAccessKey: {name: archive-access, key: secret}
    data: {compression: gzip}
    wal: {compression: gzip}
YAML
k wait --for=condition=Ready pod/s3 --timeout=5m >/dev/null

# Keep the signed hostname identical to Barman's endpoint; only the lab transport differs.
start_forward() {
    "${lab_kube[@]}" --namespace "${namespace}" port-forward --address 127.0.0.1 service/s3 0:9000 \
        >"${work}/forward.log" 2>&1 &
    forward_pid=$!
    for _ in {1..30}; do
        kill -0 "${forward_pid}" 2>/dev/null || die 'S3 loopback forwarding exited.'
        s3_port="$(sed -nE 's/^Forwarding from 127\.0\.0\.1:([0-9]+) -> 9000$/\1/p' "${work}/forward.log" | head -1)"
        [[ -z "${s3_port}" ]] || return 0
        sleep 1
    done
    die 'Timed out starting S3 loopback forwarding.'
}
s3() {
    curl --silent --show-error --fail --noproxy '*' --connect-timeout 4 --max-time 15 \
        --aws-sigv4 'aws:amz:us-east-1:s3' --config "${work}/curl.conf" \
        --connect-to "s3:9000:127.0.0.1:${s3_port}" "$@"
}
start_forward
s3 --retry 5 --retry-all-errors -X PUT http://s3:9000/backups >/dev/null

admin_secret() {
    printf 'postgres' >"${work}/username"
    openssl rand -hex 24 | tr -d '\n' >"${work}/password"
    k create secret generic "$1" --type=kubernetes.io/basic-auth \
        --from-file=username="${work}/username" --from-file=password="${work}/password" >/dev/null
}
admin_secret source-admin
k create -f - >/dev/null <<YAML
apiVersion: postgresql.cnpg.io/v1
kind: Cluster
metadata: {name: source-db}
spec:
  instances: 1
  imageName: ${BACKUP_POSTGRES_IMAGE}
  storage: {size: 1Gi}
  enableSuperuserAccess: true
  superuserSecret: {name: source-admin}
  plugins:
    - name: barman-cloud.cloudnative-pg.io
      isWALArchiver: true
      parameters: {barmanObjectName: archive, serverName: origin}
YAML
k wait --for=condition=Ready cluster/source-db --timeout=10m >/dev/null
sql() {
    local primary
    primary="$(k get cluster "$1" -o jsonpath='{.status.currentPrimary}')"
    k exec "${primary}" -c postgres -- psql -XAtq --set=ON_ERROR_STOP=1 -U postgres -d postgres -c "$2"
}
backup() {
    local name="$1"
    k create -f - >/dev/null <<YAML
apiVersion: postgresql.cnpg.io/v1
kind: Backup
metadata: {name: ${name}}
spec:
  cluster: {name: source-db}
  method: plugin
  pluginConfiguration: {name: barman-cloud.cloudnative-pg.io}
YAML
    for _ in {1..180}; do
        local state
        state="$(k get backup "${name}" -o json)"
        case "$(jq -r '.status.phase' <<<"${state}")" in
            completed) jq -er '.status.backupId | select(test("^[0-9]{8}T[0-9]{6}$"))' <<<"${state}"; return ;;
            failed|'invalid backup definition') die "Backup ${name} failed." ;;
        esac
        sleep 2
    done
    die "Timeout creating backup ${name}."
}
sql source-db 'CREATE TABLE markers(id int primary key); INSERT INTO markers VALUES (1)'
# A control-plane-only outage must not interrupt the workload's next backup.
stop_forward
if s3 http://s3:9000/backups >/dev/null 2>&1; then die 'Catalog route unexpectedly reachable.'; fi
first_id="$(backup first)"
sql source-db 'INSERT INTO markers VALUES (2)'
target_time="$(sql source-db "SELECT to_char(clock_timestamp() AT TIME ZONE 'UTC','YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"')")"
sql source-db 'INSERT INTO markers VALUES (3)'
second_id="$(backup second)"
[[ "${first_id}" != "${second_id}" ]] || die 'Expected two distinct backups.'
start_forward
# Isolate ingress to S3 from workload pods. The kubelet loopback tunnel remains reachable.
k create -f - >/dev/null <<YAML
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata: {name: isolate-storage}
spec:
  podSelector: {matchLabels: {app: s3}}
  policyTypes: [Ingress]
  ingress: []
YAML
baseline_failures="$(sql source-db 'SELECT failed_count FROM pg_stat_archiver')"
sql source-db 'INSERT INTO markers VALUES (4); SELECT pg_switch_wal()' >/dev/null
for attempt in {1..90}; do
    (( $(sql source-db 'SELECT failed_count FROM pg_stat_archiver') > baseline_failures )) && break
    [[ "${attempt}" -lt 90 ]] || die 'Workload WAL archiving did not report the isolated S3 route.'
    sleep 2
done
s3 --head "http://s3:9000/backups/origin/base/${first_id}/backup.info" >/dev/null
k delete networkpolicy/isolate-storage >/dev/null
k delete cluster/source-db --cascade=foreground --wait --timeout=3m >/dev/null
k delete secret/source-admin >/dev/null
[[ -z "$(k get pods -l cnpg.io/cluster=source-db -o name)" ]] || die 'Source pod still exists.'

# List prefix on the host, then read both Barman metadata files from storage.
s3 'http://s3:9000/backups?delimiter=%2F&list-type=2&prefix=origin%2Fbase%2F' >"${work}/catalog.xml"
python3 - "${work}/catalog.xml" "${first_id}" "${second_id}" <<'PY'
from sys import argv
from xml.etree import ElementTree
catalog = ElementTree.parse(argv[1])
assert catalog.findtext('{*}IsTruncated') == 'false'
assert {node.text for node in catalog.findall('{*}CommonPrefixes/{*}Prefix')} == {
    f'origin/base/{backup}/' for backup in argv[2:]}
PY
for backup_id in "${first_id}" "${second_id}"; do
    s3 "http://s3:9000/backups/origin/base/${backup_id}/backup.info" >"${work}/backup.info"
    grep -qx 'status=DONE' "${work}/backup.info"
    grep -Eq '^end_time=.+' "${work}/backup.info"
done
info 'Both completed base backups were listed directly from S3 after deleting the source.'

restored_sql() {
    local name="$1" statement="$2" primary
    primary="$(k get cluster "${name}" -o jsonpath='{.status.currentPrimary}')"
    [[ -n "${primary}" ]] || return 1
    k exec -i "${primary}" -c postgres -- sh -c '
        PGPASSWORD=$(cat); export PGPASSWORD
        exec psql -XAtq --set=ON_ERROR_STOP=1 -h "$1" -U postgres -d postgres -c "$2"' \
        sh "${name}-rw" "${statement}" <"${work}/password"
}

restore() {
    local name="$1" target="$2" expected="$3"
    admin_secret "${name}-admin"
    k create -f - >/dev/null <<YAML
apiVersion: postgresql.cnpg.io/v1
kind: Cluster
metadata: {name: ${name}}
spec:
  instances: 1
  imageName: ${BACKUP_POSTGRES_IMAGE}
  storage: {size: 1Gi}
  enableSuperuserAccess: true
  superuserSecret: {name: ${name}-admin}
  bootstrap:
    recovery:
      source: source-archive
      recoveryTarget: ${target}
  externalClusters:
    - name: source-archive
      plugin:
        name: barman-cloud.cloudnative-pg.io
        parameters: {barmanObjectName: archive, serverName: origin}
YAML
    k wait --for=condition=Ready "cluster/${name}" --timeout=10m >/dev/null
    local rows attempt login_ready=false
    for attempt in {1..90}; do
        if restored_sql "${name}" 'SELECT 1' >/dev/null 2>&1; then
            login_ready=true
            break
        fi
        sleep 2
    done
    [[ "${login_ready}" == true ]] || die "Administrative SQL login for ${name} did not become ready."
    rows="$(restored_sql "${name}" "SELECT string_agg(id::text, ',' ORDER BY id) FROM markers")"
    [[ "${rows}" == "${expected}" ]] || die "Unexpected rows after restoring ${name}."
    info "${name} recovered rows ${expected}."
}
restore older "{backupID: '${first_id}', targetImmediate: true}" '1'
restore timed "{targetTime: '${target_time}'}" '1,2'
