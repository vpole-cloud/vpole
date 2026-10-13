# Aspire E2E testing

`tests/Pole.Tests.E2E` starts an isolated Aspire graph with disposable MongoDB, Keycloak, and
RabbitMQ containers; real Pole API/frontend processes; and a local GitHub protocol stub. It uses
the reusable Kubernetes lab for deployment targets, Harbor, and BuildKit.

Fixture API requests renew registered users' bearer tokens before expiry, including after host
suspension. Renewal uses the same Keycloak account and rejects a changed subject or stale token.
API responses are sent through unchanged: the helper does not retry 401/403 responses, renew
unregistered tokens, or change tenant headers. Browser sessions exercise the application's own
authentication flow. `E2eApiClientTests` covers an overnight clock gap and concurrent renewal.

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
- browser Git repository/branch/build-plan selection, source-linked service creation, BuildKit
  build/push, Harbor artifacts, Kubernetes deployment, and a visible failed-build result;
- tenant-scoped global search and contextual commands, keyboard selection, focus restoration,
  and reuse of the existing resource tab after opening a search result.
- PostgreSQL browser catalog/create/details/configuration, readable single-target placement,
  native provisioning, compute update/reload and credential UI without secret screenshots.
  The fixture explicitly selects native StatefulSet mode for these browser scenarios;
  operator-specific manifest and credential-projection tests run separately in the owning feature
  suites against CloudNativePG in the full lab.
- Redis browser catalog/create/details/configuration, tags update and reload, confirmed
  deletion, readable single-target placement and desktop/mobile light/dark screenshots.
- Secret browser metadata/create/full-map rotation, version-aware writes, generic resource
  binding, native projection convergence and detach cleanup. Screenshots cover desktop/mobile
  light/dark views and are captured before values are entered or after sensitive forms unmount.
- Domain zone reservation in a separate workspace, exact target hostname ownership, protected
  generated names, alias replacement/reload and confirmed detach. Zone details link the owning
  resource and group; desktop/mobile light/dark screenshots exercise both domain workspaces.

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

`DependencyBrowserE2ETests` additionally checks the lab's actual ingress-policy behavior with a
source-built application: SQL and Redis operations are rejected before network declarations,
succeed after the browser configures access, and PostgreSQL fails again after disconnect. Only
application HTTP is port-forwarded; database traffic originates in the application pod. The test
also replaces temporary literal addresses with target-resolved logical references and consumes
managed PostgreSQL credentials through ordinary Secret key references. This observation does not
change the platform target's `Unknown` attestation or claim broader CNI conformance.

The API and E2E configure an outbox trigger interval of one second so asynchronous workflows do
not wait for the minute-based recovery schedule. Dispatch shares that schedule's distributed lock
and never queues a Hangfire job for every tick. Removing the optional interval disables fast dispatch.

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
runs and other worktrees independent. The test restores its port-forward at the original local
port in `finally`, so later tests in the fixture can build again. The fixture keeps its tenant's
Harbor project until all source-build tests finish, then deletes it during teardown.

The API provenance test uses `FROM scratch` to isolate artifact/release behavior. The browser
journey selects the stub's runnable `dependencies` revision, builds its .NET application and
waits for `/health` on port 8080 to become ready. It saves a deliberately broken COPY recipe,
reloads its retained failure output, verifies the running deployment is unchanged, then repairs
the recipe through the browser and observes a new successful release and ready workload.

`TenantWorkspaceBrowserE2ETests` temporarily grants the real Beta Keycloak user Viewer access to
Alpha while retaining ownership of Beta. It proves read-only controls and an actual forbidden
mutation, dirty-form cancellation/confirmed tenant switching, and cancellation/clearing of a
real runtime follow stream. A narrowly intercepted, already fetched real Alpha API response is
released after switching to Beta; it cannot populate Beta's workspace or persistence.
`HealthBrowserE2ETests` injects a browser read failure, retains explicitly stale operation state,
and restores it by reading the real API again. `MultiClusterWebServiceE2ETests` confirms the exact
failed target retry through Operations and verifies its request revision and the unchanged
healthy target's native release/generations. The existing runtime relay test covers a real target
transport outage independently of this browser read injection.

`StorageBrowserE2ETests` signs in as the fixture's real platform administrator, reads native target
inventory, creates a target-mapped profile, and updates it with the saved revision. It captures
both themes and desktop/mobile layouts, then disables its owned profile before fixture teardown.

Secret browser acceptance uses fixed-message absence assertions for controlled initial/rotated
values and the real browser/fixture tokens in metadata, projection diagnostics and screenshot-bound DOM/storage
after sensitive forms unmount. Failed assertions do not echo those values. Workload output is
still application-owned; this controlled check does not claim arbitrary runtime-secret removal.

`RuntimeLogsBrowserE2ETests` creates two owned Redis workloads and adds harmless test-only log
containers to one owned StatefulSet. It verifies container selection, response cancellation,
same-name pod replacement with an exact UID delete precondition, explicit reconnect and an
independent healthy target during a real transport outage. A loopback TLS pass-through relay is
used only by the dedicated test target; pausing it closes that target's connections without
changing shared lab networking or inspecting credentials. Cleanup restores the relay before
deleting resources and waits for both owned namespaces to disappear. Production Redis manifests
are unchanged by this fixture.

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

`HealthBrowserE2ETests` creates an owned private nginx workload with a deliberately failing readiness
probe. It verifies Applied separately from Not ready, bounded owned warning context, reload after
an accepted new generation, and convergence to Ready in resource/group/tenant views. Screenshots
cover desktop/mobile and both themes without native messages or credential payloads.

`ReleaseBrowserE2ETests` verifies authenticated history, redeploy and rollback. It temporarily
disables only its disposable fixture's target to hold a real release Pending across reload,
restores the target, waits for the server retry and checks rollback specification/new revision
and current native readiness. Cleanup restores target availability before removing owned resources.
Screenshots cover desktop/narrow viewports and both themes under `.artifacts/frontend-releases`.

The authenticated source-build browser scenario customizes a recipe with a controlled missing-file
`COPY`. Real BuildKit remains available; useful failure output is captured on the server, opened
from Builds, restored after reload and opened again from Operations. It compares desired revision,
placement generations, releases and native image/release metadata before and after the failed
build to prove that failure did not redeploy the resource. Log screenshots cover desktop/narrow
views and both themes under `.artifacts/frontend-build-logs`. A separate API scenario still checks
failure when only the fixture's BuildKit port-forward is stopped.

## Realtime verification lanes

`RealtimeBrowserE2ETests` exercises two full API processes with real Keycloak/shared cookie,
protected keyring/backplane and remote logout; bearer refresh preserving a dirty tenant workspace;
quiet covered metadata reads compared with disconnected polling; and committed metadata bursts
around a held HTTP read. `RealtimeMembershipE2ETests` checks current roles, Viewer actions,
held-read fencing and membership removal/restoration without relying on token rotation.
`RealtimeObservationBrowserE2ETests` uses owned WebService Pod deletes with exact UID preconditions
and journals write-start-to-visible old UID removal. Its twenty-sample upper bound is compared
with a five-second p95 budget; the verified stage 7 sample measured 3.1 s. That result is a specific
WebService/native scenario, not a universal provider latency claim. The quiet metadata pilot's
88.5% reduction likewise applies to its stated pending-release window and disconnected baseline,
not every 60-second safety refresh or every provider.

The quiet comparison uses tenant-scoped, revision-fenced manual retries of its owned disabled-target
deployment until the retry backoff covers both 65-second windows and their setup margin. It verifies
the failure phase and unchanged attempt count before/after each window, and zero owned change hints
during the covered window. The production recovery scheduler stays enabled. Automatic reconciliation
is a real change and must not be counted as idle polling. The current isolated comparison measured
26 disconnected GETs versus 3 covered GETs, an 88.5% reduction with uninterrupted coverage.
After restoring the target, the test reloads the owned operation and explicitly retries a known
retryable disabled-target failure through the public tenant-scoped API, with the current deployment
revision and `Apply` precondition. Accepted/running operations are only observed; conflicts trigger
a fresh read. The original four-minute recovery bound and release-2 success assertion remain.

The second API process runs the matching configuration/target-framework build of `Pole.Server.dll`
through `dotnet exec`. DCP supervises the API directly, without a `dotnet run` launcher and child
process tree. The primary's service, protected-key and OTLP environment callbacks are retained.
The replica alone has `DOTNET_SHUTDOWNTIMEOUTSECONDS=10`, below the pinned Aspire DCP
stop command's approximately 15-second retry window. The primary host retains its normal deadline.
The test waits for owned release reconciliation before stop/cleanup and still requires the same
process instance's terminal state, a stop timestamp after the command intent and an accepted exit
status. It verifies that stopping the replica closes its existing hub connection, while the primary
connection survives, then restarts at the same endpoint and reuses the existing shared cookie.
Safe lifecycle scalars identify the failing phase; primary and cleanup failures are reported together.

`RealtimeRedisTests` separately proves real Redis outage/restart and server/API recovery.
`KubernetesNativeWatchTests` proves actual SDK response ownership, quiet watch opening,
bookmarks/EOF/410/malformed recovery and cancellation against controlled transports.
Observation-owner tests prove singleflight/source-loss fencing; Mongo observation tests prove
lease takeover, original demand expiry, snapshot/outbox visibility/rollback and final deadline
fences. MeterListener tests prove bounded operational labels and diagnostic report authority/rate
limits. Controlled transports and browser interception are not substitutes for a new provider's
live native ownership or end-to-end outage evidence.

Redis/PostgreSQL/storage browser observation rollout, client reconnect/fallback reporting and
final merged lifecycle evidence must be evaluated on their current source tree; the earlier
WebService pilot is not their passing result. Serialize heavy lanes according to the documented
worktree scheduling. Final publication still requires `python3 scripts/quality_gate.py run` with
zero failed or skipped tests across all backend/frontend/automation/infrastructure lanes; missing
prerequisites and pre-existing failures block it. No focused result alone is a full-gate claim.
