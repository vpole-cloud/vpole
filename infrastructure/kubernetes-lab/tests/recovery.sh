#!/usr/bin/env bash
# Copyright (C) 2026 Victor Shnaider and vPole contributors
# SPDX-License-Identifier: AGPL-3.0-only

# Infrastructure-free regression tests; runtime calls are replaced, not the ownership checks.
set -Eeuo pipefail
source "$(dirname -- "${BASH_SOURCE[0]}")/../lab.sh"
TEST_TMP="$(mktemp -d)"
trap 'rm -rf -- "${TEST_TMP}"' EXIT
CALLS="${TEST_TMP}/calls"
LAB_INSTANCE_ID=0123456789abcdef0123456789abcdef
LAB_REGISTRY_CONTAINER_ID="$(printf '%064d' 1)"
PROFILE=full

run_container() {
    case "$1" in
        ps) printf '%s' "${IDS}" ;;
        inspect)
            case "$4" in
                '{{.State.Running}}') [[ "$2" == node-b ]] && printf '%s' "${SECOND_RUNNING}" || printf '%s' "${RUNNING}" ;;
                '{{index .Config.Labels "k3d.cluster"}}') printf pole-lab-default ;;
                '{{index .Config.Labels "pole.dev/owned-by"}}') printf '%s' "${OWNER}" ;;
                '{{index .Config.Labels "pole.dev/lab-instance-id"}}') printf '%s' "${INSTANCE}" ;;
                '{{.Id}}') printf '%s' "${REGISTRY_ID}" ;;
                '{{index .Config.Labels "k3d.role"}}') printf '%s' "${REGISTRY_ROLE}" ;;
                *) printf 'Unexpected inspect: %s\n' "$*" >&2; return 90 ;;
            esac ;;
        start) printf 'container %s\n' "$*" >>"${CALLS}" ;;
        *) printf 'Unexpected runtime call: %s\n' "$*" >&2; return 90 ;;
    esac
}
run_k3d() {
    if [[ "$1 $2" == 'registry list' ]]; then
        [[ "${REGISTRY_EXISTS}" == true ]] && printf '%s\n' "${LAB_REGISTRY_NAME}" || true
    elif [[ "$1 $2" == 'cluster start' ]]; then
        printf 'k3d %s\n' "$*" >>"${CALLS}"
    else
        printf 'Unexpected k3d call: %s\n' "$*" >&2
        return 90
    fi
}
reset_case() {
    : >"${CALLS}"
    IDS=$'node-a\nnode-b'
    RUNNING=false SECOND_RUNNING=false
    OWNER=kubernetes-lab INSTANCE="${LAB_INSTANCE_ID}"
    REGISTRY_EXISTS=true REGISTRY_ID="${LAB_REGISTRY_CONTAINER_ID}" REGISTRY_ROLE=registry
}
assert_equal() {
    [[ "$1" == "$2" ]] || { printf 'Expected <%s>, got <%s>\n' "$2" "$1" >&2; exit 1; }
}
assert_rejected() {
    # Run in a separate shell context so die's exit cannot end this test process.
    if ( "$@" ) >"${TEST_TMP}/failure" 2>&1; then
        printf 'Expected rejection: %s\n' "$*" >&2
        exit 1
    fi
    assert_equal "$(cat "${CALLS}")" ''
}
reset_case
assert_equal "$(cluster_runtime_status pole-lab-default)" stopped
resume_owned_cluster pole-lab-default
assert_equal "$(cat "${CALLS}")" 'k3d cluster start pole-lab-default --wait --timeout 180s'
reset_case
RUNNING=true SECOND_RUNNING=true
assert_equal "$(cluster_runtime_status pole-lab-default)" running
resume_owned_cluster pole-lab-default
assert_equal "$(cat "${CALLS}")" ''
reset_case
RUNNING=true
assert_equal "$(cluster_runtime_status pole-lab-default)" partial
resume_owned_cluster pole-lab-default
assert_equal "$(cat "${CALLS}")" 'k3d cluster start pole-lab-default --wait --timeout 180s'
reset_case
IDS=''
assert_equal "$(cluster_runtime_status pole-lab-default)" missing
assert_rejected resume_owned_cluster pole-lab-default
reset_case
OWNER=someone-else
assert_rejected resume_owned_cluster pole-lab-default
reset_case
INSTANCE=foreign-instance
assert_rejected resume_owned_cluster pole-lab-default
reset_case
assert_equal "$(registry_runtime_status)" stopped
ensure_registry
assert_equal "$(cat "${CALLS}")" "container start ${LAB_REGISTRY_CONTAINER_ID}"
reset_case
RUNNING=true
assert_equal "$(registry_runtime_status)" running
ensure_registry
assert_equal "$(cat "${CALLS}")" ''
reset_case
REGISTRY_EXISTS=false
assert_equal "$(registry_runtime_status)" missing
assert_rejected ensure_registry
reset_case
REGISTRY_ID=foreign-id
assert_rejected ensure_registry
reset_case
REGISTRY_ROLE=server
assert_rejected ensure_registry
printf 'Lab recovery regression tests passed (11 cases).\n'
