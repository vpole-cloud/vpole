# Aspire E2E testing

`tests/Pole.Tests.E2E` starts an isolated Aspire graph with disposable MongoDB, Keycloak, and
RabbitMQ containers; real Pole API/frontend processes; and a local GitHub protocol stub. It uses
the reusable Kubernetes lab for deployment targets, Harbor, and BuildKit.

The suite covers:

- real Keycloak login, JWT validation, roles, and the platform-admin user policy;
- asynchronous default-tenant creation through MongoDB transactions and RabbitMQ;
- tenant isolation and GitHub App installation/OAuth flows against the local stub;
- browser login, tenant selection, resource-group and image-service creation, observed Kubernetes
  replica availability, configuration updates, independent catalog/create/resource sections,
  draft retention, tab keyboard controls, reload, confirmation-dialog focus, and light/dark
  screenshots of each workspace section at desktop and mobile viewports;
  resource-group metadata tabs, dirty-close confirmation, direct URLs/reload, and typed resource
  links from group contents and the paginated navigation tree;
- multi-target Kubernetes placement and isolated target failure/retry;
- deterministic source analysis, BuildKit build/push, Harbor artifacts, and Kubernetes deployment.

The GitHub stub serves deterministic `v1` and `v2` source archives and never records credentials,
OAuth state, private keys, or bearer tokens.

## Prerequisites and run

Start a Docker-compatible container runtime, install the repository's .NET and Node.js versions,
and install frontend dependencies once:

```bash
npm ci --prefix src/api/frontend
```

Start and verify the reusable full lab, then run either the whole backend suite or only E2E:

```bash
./infrastructure/kubernetes-lab/lab.sh up
./infrastructure/kubernetes-lab/lab.sh verify
dotnet test

# focused alternative
dotnet test tests/Pole.Tests.E2E/Pole.Tests.E2E.csproj
```

No E2E settings file, lab-selection variables, Harbor secrets, local `buildctl`, or host browser
installation is required. The fixture discovers `.tmp/kubernetes-lab/state.json`, uses its
`default` target and pinned `buildctl`, and starts a pinned Playwright browser in a disposable
container. The environment variables `POLE_K8S_LAB_STATE` and `POLE_K8S_LAB_TARGET` remain optional
overrides.

The fixture creates a unique Keycloak bootstrap user, test users, a direct-grant client, temporary
RSA material, and a dynamic frontend origin. It removes those resources and disposes the AppHost
graph in reverse order. Kubernetes namespaces carry the shared test ownership labels and are
deleted only after their UID and labels are verified.

Before workloads are created, the fixture uses its platform-admin token to explicitly set
`Unknown` enforcement with `AllowIsolationNotEnforced=true` on the bootstrap target and each
multi-cluster target. These tests exercise workload flows, not CNI enforcement, so they do not
claim `Supported` enforcement. This test-only API handshake preserves the production default
that blocks workload admission until enforcement is attested or an operator explicitly overrides it.

E2E configures an optional outbox trigger interval of one second so asynchronous workflows do
not wait for the production minute-based recurring schedule. The production default remains
unset, so this faster trigger is active only when a test or another environment enables it.

The first browser run may take several minutes while the pinned Playwright image is downloaded.
Later runs reuse the local image and start the browser test immediately.

## Multi-cluster and source-build behavior

The default full lab provides `default`, `workload-a`, and `workload-b`. Multi-cluster tests register
the workload targets through the platform API, reconcile a WebService to both, disable one target,
observe the isolated failure, re-enable it, and retry only its failed physical deployment.

Source-build tests use Harbor and BuildKit running inside the lab's default cluster. The fixture
opens temporary port-forwards for host-side API/buildctl access while image references use the
lab's stable registry authority. Failure injection closes only that fixture's BuildKit
port-forward; it never stops or mutates the shared BuildKit deployment. This keeps concurrent test
runs and other worktrees independent.

The source image is deliberately `FROM scratch`, so the test proves build, push, release,
provenance, image-pull-secret, and reconciliation behavior without an external base image. It does
not wait for an HTTP-ready application workload.

## Parallelism and cleanup

Independent worktrees may run tests against the same lab. They resolve the state and lifecycle lock
beneath the primary worktree, but derive distinct worktree IDs from their own Git metadata. Every
test namespace is labeled with `pole.dev/owned-by`, that stable worktree ID, and a test-host run ID.
Normal fixture teardown targets only exact owned namespaces, and lab lifecycle commands are
serialized by the shared lock. Disposable Testcontainers and Aspire containers carry the same
identity labels; the recovery and audit commands include them as well.

Isolation allows concurrent worktrees, but Kubernetes-backed E2E places significant load on the
host. Queue these runs by default and coordinate any overlap according to
[test scheduling](testing.md#parallelism-across-worktrees). Docker-only suites from independent
worktrees may run concurrently.

After an interrupted run, stop the tests and recover only the current worktree's namespaces:

```bash
./infrastructure/kubernetes-lab/lab.sh clean-tests
./infrastructure/kubernetes-lab/lab.sh audit-tests
```

The test results need no user secrets and never call the real GitHub API. For manual validation
against a real GitHub installation, see [GitHub source build E2E](github-source-build-e2e.md).

## CI

The [Test workflow](../.github/workflows/kubernetes-lab.yml) runs the full lab and the complete
backend test set on GitHub-hosted Ubuntu for every pull request and push to `master`. The workflow
removes the unused preinstalled Android SDK to retain enough disk for the nested clusters, restores
and builds once, and serializes test projects on the constrained runner. Local development retains
the plain `dotnet test` command. Failure diagnostics include node conditions, pods, and events for
every target; the worktree-scoped leak audit still runs after test failure. Frontend unit tests and
the frontend build run in a separate job.
