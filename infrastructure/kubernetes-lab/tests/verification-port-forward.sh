#!/usr/bin/env bash
# Copyright (C) 2026 Victor Shnaider and vPole contributors
# SPDX-License-Identifier: AGPL-3.0-only

# No cluster calls: the owned kubectl stand-in binds and holds an OS-assigned loopback socket.
set -Eeuo pipefail
source "$(dirname -- "${BASH_SOURCE[0]}")/../lab.sh"
FORWARD_TEST_TMP="$(mktemp -d)"
UNRELATED_TEST_PID=""
cleanup_forward_test() {
    cleanup_verification_namespaces
    if [[ -n "${UNRELATED_TEST_PID}" ]]; then
        kill "${UNRELATED_TEST_PID}" >/dev/null 2>&1 || true
        wait "${UNRELATED_TEST_PID}" >/dev/null 2>&1 || true
    fi
    rm -rf -- "${FORWARD_TEST_TMP}"
}
trap cleanup_forward_test EXIT
cat >"${FORWARD_TEST_TMP}/kubectl" <<'PROBE'
#!/usr/bin/env python3
import os
from pathlib import Path
import signal
import socket
import sys

case = Path(os.environ["FORWARD_TEST_CASE"])
mode = os.environ["FORWARD_TEST_MODE"]
(case / "arguments").write_text("\n".join(sys.argv[1:]))
(case / "pid").write_text(str(os.getpid()))
listener = socket.socket()
listener.bind(("127.0.0.1", 0))
listener.listen()
port = listener.getsockname()[1]
(case / "port").write_text(str(port))

def stop(_signal, _frame):
    (case / "stopped").write_text("terminated")
    listener.close()
    sys.exit(0)

signal.signal(signal.SIGTERM, stop)
print("diagnostic-sensitive-marker", flush=True)
if mode in ("ready", "dead-ready"):
    print(f"Forwarding from 0.0.0.0:1 -> 1234", flush=True)
    print(f"Forwarding from 127.0.0.1:1 -> 80", flush=True)
    print(f"prefix Forwarding from 127.0.0.1:1 -> 1234", flush=True)
    print(f"Forwarding from 127.0.0.1:{port} -> 1234", flush=True)
elif mode == "wrong-address":
    print(f"Forwarding from 0.0.0.0:{port} -> 1234", flush=True)
elif mode == "wrong-remote":
    print(f"Forwarding from 127.0.0.1:{port} -> 80", flush=True)
elif mode == "zero":
    print("Forwarding from 127.0.0.1:0 -> 1234", flush=True)
elif mode == "oversized":
    print("Forwarding from 127.0.0.1:65536 -> 1234", flush=True)
elif mode == "bounded":
    print("x" * 4096, flush=True)
    print(f"Forwarding from 127.0.0.1:{port} -> 1234", flush=True)
if mode in ("exit", "dead-ready"):
    sys.exit(7)
while True:
    signal.pause()
PROBE
chmod +x "${FORWARD_TEST_TMP}/kubectl"
KUBECTL_BIN="${FORWARD_TEST_TMP}/kubectl"
PROFILE_CLUSTER_NAMES=(port-forward-test)
LAB_STATE_ROOT="${FORWARD_TEST_TMP}/state"
LAB_KUBECONFIG_DIR="${LAB_STATE_ROOT}/kubeconfigs"
# Timeout coverage keeps all 30 production attempts; only their waiting period is shortened.
sleep() {
    printf '.' >>"${FORWARD_TEST_CASE}/sleeps"
    command sleep 0.01
}
kill() {
    if [[ "${FORWARD_TEST_MODE:-}" == dead-ready && "${1:-}" == -0 ]]; then
        # Reap the short-lived owned child before checking its readiness line, without a scheduling race.
        wait "$2" >/dev/null 2>&1 || true
    fi
    command kill "$@"
}
assert_equal() {
    [[ "$1" == "$2" ]] || { printf 'Expected <%s>, got <%s>\n' "$2" "$1" >&2; exit 1; }
}
assert_child_stopped() {
    local child_pid
    child_pid="$(<"${FORWARD_TEST_CASE}/pid")"
    if command kill -0 "${child_pid}" >/dev/null 2>&1; then
        printf 'Owned port-forward process was not cleaned up\n' >&2
        exit 1
    fi
    command kill -0 "${UNRELATED_TEST_PID}" >/dev/null 2>&1 \
        || { printf 'Cleanup stopped an unrelated process\n' >&2; exit 1; }
}
reset_case() {
    export FORWARD_TEST_MODE="$1"
    export FORWARD_TEST_CASE="${FORWARD_TEST_TMP}/$1"
    mkdir -p -- "${FORWARD_TEST_CASE}"
    VERIFY_BUILDKIT_FORWARD_PID=""
    VERIFY_BUILDKIT_FORWARD_PORT=""
}
# This process belongs to the test and must remain untouched by the helper's exact-PID cleanup.
command sleep 60 &
UNRELATED_TEST_PID="$!"
reset_case ready
printf 'Forwarding from 127.0.0.1:1 -> 1234\n' >"${FORWARD_TEST_CASE}/port-forward.log"
start_verification_buildkit_forward "${FORWARD_TEST_CASE}"
assert_equal "${VERIFY_BUILDKIT_FORWARD_PORT}" "$(<"${FORWARD_TEST_CASE}/port")"
assert_equal "${VERIFY_BUILDKIT_FORWARD_PID}" "$(<"${FORWARD_TEST_CASE}/pid")"
grep --line-regexp --fixed-strings --quiet '0:1234' "${FORWARD_TEST_CASE}/arguments"
grep --line-regexp --fixed-strings --quiet '127.0.0.1' "${FORWARD_TEST_CASE}/arguments"
cleanup_verification_namespaces
set -e
assert_equal "${VERIFY_BUILDKIT_FORWARD_PID}" ''
assert_equal "${VERIFY_BUILDKIT_FORWARD_PORT}" ''
assert_child_stopped
assert_equal "$(<"${FORWARD_TEST_CASE}/stopped")" terminated

for mode in timeout wrong-address wrong-remote zero oversized bounded exit dead-ready; do
    reset_case "${mode}"
    if (
        trap cleanup_verification_namespaces EXIT
        start_verification_buildkit_forward "${FORWARD_TEST_CASE}"
    ) >"${FORWARD_TEST_CASE}/failure" 2>&1; then
        printf 'Expected readiness failure for %s\n' "${mode}" >&2
        exit 1
    fi
    grep --fixed-strings --quiet 'BuildKit verification port-forward' "${FORWARD_TEST_CASE}/failure"
    if grep --fixed-strings --quiet 'diagnostic-sensitive-marker' "${FORWARD_TEST_CASE}/failure"; then
        printf 'Port-forward diagnostics exposed raw process output\n' >&2
        exit 1
    fi
    assert_child_stopped
    if [[ "${mode}" != exit && "${mode}" != dead-ready ]]; then
        assert_equal "$(wc -c <"${FORWARD_TEST_CASE}/sleeps")" 29
        assert_equal "$(<"${FORWARD_TEST_CASE}/stopped")" terminated
    fi
done
printf 'Verification port-forward regression tests passed (9 cases).\n'
