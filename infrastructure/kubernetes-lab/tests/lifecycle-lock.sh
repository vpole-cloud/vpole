#!/usr/bin/env bash
# Copyright (C) 2026 Victor Shnaider and vPole contributors
# SPDX-License-Identifier: AGPL-3.0-only

# A daemon launched by Podman/k3d must not inherit the parent's lifecycle lock.
set -Eeuo pipefail
source "$(dirname -- "${BASH_SOURCE[0]}")/../lab.sh"
LOCK_TEST_TMP="$(mktemp -d)"
trap 'rm -rf -- "${LOCK_TEST_TMP}"' EXIT
cat >"${LOCK_TEST_TMP}/runtime" <<'PROBE'
#!/usr/bin/env bash
set -Eeuo pipefail
[[ ! -e /proc/$$/fd/9 ]] || { echo 'Runtime inherited lifecycle lock descriptor' >&2; exit 1; }
# Closing it in the child must not release the live parent's lock.
if flock --nonblock "$1" true; then
    echo 'Parent lifecycle lock was released too soon' >&2
    exit 1
fi
PROBE
chmod +x "${LOCK_TEST_TMP}/runtime"
CONTAINER_CLI="${LOCK_TEST_TMP}/runtime"
K3D_BIN="${LOCK_TEST_TMP}/runtime"
exec 9>"${LOCK_TEST_TMP}/lifecycle.lock"
flock --nonblock 9
run_container "${LOCK_TEST_TMP}/lifecycle.lock"
run_k3d "${LOCK_TEST_TMP}/lifecycle.lock"
exec 9>&-
flock --nonblock "${LOCK_TEST_TMP}/lifecycle.lock" true
printf 'Lifecycle lock descriptor tests passed (2 runtime boundaries).\n'
