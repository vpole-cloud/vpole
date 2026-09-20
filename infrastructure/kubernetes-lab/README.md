# Pole Kubernetes lab

The lab is reusable infrastructure for Pole's Kubernetes and end-to-end tests. Tests never create,
replace, or delete its clusters, operators, ingress controller, storage class, Harbor, or BuildKit.
They create isolated namespaces inside it and clean those namespaces after each run.

## Quick start

Start Docker, or start the rootless Podman API socket, then run from the repository root:

```bash
./infrastructure/kubernetes-lab/lab.sh up
./infrastructure/kubernetes-lab/lab.sh verify
dotnet test
```

No test environment variables are required. Tests discover the primary worktree's shared
`.tmp/kubernetes-lab/state.json` and select target `default`. `POLE_K8S_LAB_STATE` and
`POLE_K8S_LAB_TARGET` are optional overrides.

The state also records the selected Docker-compatible endpoint. Container-bearing test assemblies
apply it and the Aspire runtime before Testcontainers or Aspire starts, keeping the separate
`dotnet test` command argument-free with rootless Podman as well as Docker. Explicit Docker,
Testcontainers, and Aspire variables remain overrides.

The default `full` profile contains:

- a topology-capable `default` cluster with monitoring CRDs, cert-manager, CloudNativePG, Harbor,
  and BuildKit;
- `workload-a` and `workload-b` clusters with monitoring CRDs for multi-target tests;
- a shared local registry and pinned k3d, kubectl, Helm, and buildctl clients.

BuildKit runs as a privileged pod inside the disposable lab cluster. This avoids unsupported nested
user-namespace creation under rootless Podman; it does not grant privileges on a production or
user-supplied cluster because the lab never installs into one.

The lab selects an active Podman socket first and otherwise uses Docker. Override auto-detection
with `POLE_K8S_LAB_RUNTIME=podman` or `POLE_K8S_LAB_RUNTIME=docker`. Podman's cgroup-v2 delegation
checks and rootless k3s flags are applied only to rootless Podman. If a Docker-compatible CLI is
connected to the local Podman socket, the lab records the actual daemon as Podman and propagates
that socket to k3d, Testcontainers, and Aspire.

Frontend verification remains separate:

```bash
npm test --prefix src/api/frontend
npm run build --prefix src/api/frontend
```

The E2E fixture starts Playwright in a disposable pinned container. The frontend still needs its
Node dependencies, so run `npm ci --prefix src/api/frontend` once after checkout.

## Ownership and parallel runs

Every test namespace has these labels:

```text
pole.dev/owned-by=pole-tests
pole.dev/worktree-id=<stable worktree id>
pole.dev/test-run-id=<test-host run id>
```

Names also begin with `pole-<worktree-id>-`. Ordinary teardown deletes only the exact namespace it
created, after verifying its UID and ownership labels. Linked worktrees resolve the same state and
lifecycle lock beneath the primary worktree, while deriving their own IDs from their own Git
metadata. This lets independent worktrees share one lab safely. Lab lifecycle commands (`up`,
`reset`, and `down`) are serialized by that shared lock.

Use the recovery command only after stopping tests in the current worktree:

```bash
./infrastructure/kubernetes-lab/lab.sh clean-tests
```

It deletes namespaces and disposable containers carrying the current worktree's exact ownership
selector, waits for volumes to be reclaimed, and then runs the leak audit. `audit-tests` is
read-only and fails when it finds owned namespaces, matching persistent volumes, or labeled test
containers:

```bash
./infrastructure/kubernetes-lab/lab.sh audit-tests
```

CI runs the audit even after a failed or cancelled test step. GitHub-hosted runners are ephemeral,
so CI intentionally does not call `lab.sh down`.

## Focused profiles

Smaller profiles remain available for focused local work:

| Profile | Clusters | Purpose |
| --- | --- | --- |
| `minimal` | `pole-lab-minimal` | One small cluster. |
| `multi` | `pole-lab-default`, `pole-lab-workload-a`, `pole-lab-workload-b` | Multi-target behavior. |
| `topology` | `pole-lab-topology` with three agents | Scheduling and topology behavior. |

Focused profiles accept repeated `--overlay` values for `monitoring`, `cert-manager`, and `cnpg`.
Harbor and BuildKit are coupled shared services and belong only to the fixed `full` profile, which
accepts no overlay flags.

```bash
./infrastructure/kubernetes-lab/lab.sh up minimal --overlay monitoring
./infrastructure/kubernetes-lab/lab.sh reset multi --overlay monitoring
```

Plain `dotnet test` expects the full profile. Use project or trait filters when deliberately working
with a smaller profile.

## Commands and safety

```bash
./infrastructure/kubernetes-lab/lab.sh status
./infrastructure/kubernetes-lab/lab.sh verify
./infrastructure/kubernetes-lab/lab.sh reset
./infrastructure/kubernetes-lab/lab.sh down
```

Generated state, standalone kubeconfigs, local credentials, and downloaded tools stay beneath
`.tmp/kubernetes-lab/`. The lab never updates the default kubeconfig or switches its current
context. Cleanup validates the recorded lab-instance labels on the network and every cluster
container, the registry's immutable container ID, and the context and path contract
before deleting lab infrastructure.

Harbor credentials are generated locally in a mode-`0600` ignored file and are referenced by path
from state; secrets are never written into `state.json`. Harbor is HTTP-only and reachable solely
through the lab's loopback mapping from the host. The test-only `harbor.pole.localhost` name maps
to loopback on the host and to the default server only inside owned lab nodes and CoreDNS. It is not
a production trust or registry configuration.

For rootless Podman, the user service must delegate `cpu`, `cpuset`, `io`, `memory`, and `pids` on
cgroup v2. If the lab reports a missing `cpuset` controller, stop an active
`xdg-document-portal.service` if it owns that controller, configure `user@.service` delegation when
needed, and begin a new user session before retrying. The full multi-node lab also requires
`fs.inotify.max_user_instances` of at least `512`; the script reports the exact one-time `sysctl`
command when the host default is too small. A nonstandard local Podman Unix socket can be selected
with matching `POLE_K8S_LAB_DOCKER_HOST` and `POLE_K8S_LAB_DOCKER_SOCK` values.

## Recovering after a reboot

Run `lab.sh status` to distinguish running, stopped, partially running, and missing lab resources.
Run `lab.sh up` to resume stopped existing resources after validating their recorded ownership.
It resumes the recorded registry and named lab clusters without resetting them. A missing recorded
registry remains an explicit error; normal provisioning creates any absent profile clusters. Follow with `lab.sh verify` before running tests.

Rootless Podman containers may remain stopped after reboot even when its API socket is active.
A temporary inotify adjustment also resets at reboot. To persist the required capacity, have an
administrator create `/etc/sysctl.d/90-pole-kubernetes-lab.conf` containing:

```text
fs.inotify.max_user_instances=1024
```

Apply that specific file with `sudo sysctl -p /etc/sysctl.d/90-pole-kubernetes-lab.conf`.
The lab reports insufficient capacity before starting containers.

Infrastructure-free recovery regression tests:

```bash
bash infrastructure/kubernetes-lab/tests/recovery.sh
```

The lifecycle lock stays in the lab shell. Container-runtime and k3d subprocesses
close its descriptor before launch so long-lived Podman networking/monitor daemons
cannot retain the lock after `up` exits. `tests/lifecycle-lock.sh` checks both runtime
boundaries and confirms that the parent remains locked until it closes the descriptor.

Harbor uses ephemeral storage in this disposable lab. The registry blob-descriptor
cache therefore uses `inmemory`, scoped to the registry process. A Redis cache can
survive replacement of a registry pod whose `/storage` is an `emptyDir`: it then
claims an old manifest exists, skips rewriting its content, and produces
`404 MANIFEST_UNKNOWN` immediately after a successful manifest PUT (see Distribution’s
[descriptor lookup before write](https://github.com/distribution/distribution/blob/v2.8.3/registry/storage/blobstore.go)). The override
is applied on every `up`, including existing generated values. The unchanged
BuildKit scratch-image push/pull verification exercises the affected path; Redis
data is not flushed as a recovery shortcut.
