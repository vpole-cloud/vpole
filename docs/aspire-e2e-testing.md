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
- browser named repository/branch selection, optional build-plan analysis and explicit inline
  Dockerfile authoring, source-build service creation without a placeholder image, BuildKit
  build/push, Harbor artifacts, Kubernetes deployment, and a visible failed-build result;
- tenant-scoped global search and contextual commands, keyboard selection, focus restoration,
  and reuse of the existing resource tab after opening a search result.
- PostgreSQL browser catalog/create/details/configuration, readable single-target placement,
  CloudNativePG provisioning, compute update/reload and administrator credential UI without secret screenshots.
  The backup API journey creates two real Barman copies, reads the catalog after source deletion, and
  restores both a selected copy and a chosen time before checking customer data with fresh credentials.
- Redis browser catalog/create/details/configuration, tags update and reload, confirmed
  deletion, readable single-target placement and desktop/mobile light/dark screenshots.
- Secret browser metadata/create/full-map rotation, version-aware writes, generic resource
  binding, native projection convergence and detach cleanup. Screenshots cover desktop/mobile
  light/dark views and are captured before values are entered or after sensitive forms unmount.
- Domain zone reservation in a separate workspace, exact target hostname ownership, protected
  generated names, alias replacement/reload and confirmed detach. Zone details link the owning
  resource and group; desktop/mobile light/dark screenshots exercise both domain workspaces.

## Journey guarantees and inventory

The migration inventory is frozen at `5ddf8c00`: **160 passing cases**, including every Theory
display identity and multiplicity. The machine-readable
[journey inventory](../tests/Pole.Tests.E2E/journey-inventory.json) maps each original case to its
target test, named phase and guarantee description. Argument values are represented only by SHA-256
hashes of complete TRX display names; the verifier never exports arguments, output or exceptions.

Only two pairs are approved for consolidation. They reduce the original inventory to **158 cases
before new regression tests**. A successful case count alone does not prove preservation: review
the named guarantee phases and the actual assertion diff.

| Original cases                                                                                                       | Target journey and phases                                                                                                                                             | Guarantees retained                                                                                                                                                                                                                                                                                                              |
| -------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `SecretBrowserE2ETests` + `DomainBrowserE2ETests`                                                                    | `PublicServiceBindingsJourney.Secret_projection_and_domain_bindings_share_one_public_service`; `e2e.public-bindings.secret`, then `e2e.public-bindings.domain`        | Default-placement Public service; endpoint discovery and anonymous/foreign-tenant rejection; write-only Secret create/rotate/project/detach/delete and credential absence; exact-target zone reservation, attach/replace/detach, protected deletion and ownership links; existing theme/viewport screenshots and native absence. |
| `Same_account_tabs_keep_different_tenant_notifications_and_dirty_workspaces_isolated` + `RealtimeMembershipE2ETests` | `TenantAccessRealtimeJourney.Tenant_tabs_and_membership_changes_preserve_realtime_workspace_isolation`; `e2e.tenant-access.tabs`, then `e2e.tenant-access.membership` | Two tenants/resources in the same SSO context; independent hints, reads and dirty DOM/storage; held discovery response across Member→Viewer, actual forbidden write, membership removal/restoration without token rotation and fresh authority; cleanup the latest membership ID.                                                |
| Existing image-service browser smoke                                                                                 | Existing case identity, extended lifecycle phases                                                                                                                     | Both complete desktop/mobile branches, navigation/drafts/focus/auth refresh, group/catalog/tree/tab checks, exact native rollout/config and screenshots; isolated-tenant quota usage, registration, actual UI deletion, same-name recreation with fresh resource/native identities and final baseline restoration.               |

For public bindings, create through the Secret scenario's default-placement path, retain all its
checks, then select the service's **actual** public-ingress-eligible target in the domain UI. This
explicitly changes the old Domain setup order, which reserved the zone before creating an
explicit-target service; target correctness and every domain assertion remain required. A single
owned group/service and a single fresh browser context serve both phases.

For tenant access, attach both journals **before login**, finish all two-tab assertions before
starting membership faults, close the separate Beta page, and establish fresh membership-phase
counters. The Alpha resource may already have its tab-phase metadata revision. Keep two real
services across the two tenants and retain the same browser session for the no-token-rotation
checks. Register one cleanup closure over the current membership ID, including its recreated ID.

The following scenarios retain independent test boundaries:

| Family                                        | Independent guarantees                                                                                                                                                                                                                                                                    |
| --------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Health and Releases                           | Initial unhealthy create with Applied distinct from Ready; separately, ready initial spec, Pending redeploy across reload/automatic recovery and exact rollback revision/spec.                                                                                                            |
| PostgreSQL and Redis browser journeys         | Provider-specific credentials/placement/configuration, accepted writes/reload, current native state and real Redis UI deletion.                                                                                                                                                           |
| SourceBuild UI and API                        | Automatic UI initial artifact and failed-recipe log/recovery; separately, deferred/manual first artifact, exact commits/digests/provenance and real BuildKit transport failure.                                                                                                           |
| Six GitOps journeys                           | Webhook and actual recurring Poll; mixed-provider preparation/drift/no-op; source UI settings/access; API approval/independent-source fences; browser reviewed lifecycle; stateful Redis identity/PVC replacement and reviewed removal.                                                   |
| Realtime replicas, refresh and outage         | Two real API processes with shared cookie/backplane and owned stop/restart/logout; real short-lived token renewal; actual Redis outage/fallback/recovery. Each keeps its own restoration boundary.                                                                                        |
| Realtime sampling and storage                 | Two **sequential 65-second** quiet windows; **20 metadata samples** with p95≤3s; **20 real Pod deletes** with p95≤5s and Ready outside latency; native-storage 35-second held read/30-second validation and 46-second freshness checks; separate cheap native API/hub authorization case. |
| RuntimeLogs, MultiCluster and TenantWorkspace | Exact runtime stream/container/UID and independent transport failure; real two-cluster isolated retry with healthy target unchanged; Viewer/Owner tenant switching, stream cancellation and late-response fencing.                                                                        |
| Storage CRUD and helper tests                 | Admin profile revision/mapping UI and screenshots; every existing isolated helper assertion and frozen Theory row.                                                                                                                                                                        |

The image-service lifecycle creates an owned tenant through the platform-admin
`POST /api/v1/tenants` API, then grants Alpha Owner membership through the public member API.
Every functional UI action, HTTP helper and namespace discovery uses Alpha and that tenant
explicitly; tenant creation does not broaden Alpha's platform role. Its tenant document
lives until disposable fixture database teardown. After both original viewport branches finish,
`e2e.image.quota-and-recreation` verifies committed service count and settled reservations through
`GET /api/v1/tenant-quotas/usage`. It deletes the mobile resource through UI, observes API404/native
absence/group deregistration, recreates the same name/group through UI with a different resource ID
and native Deployment UID, then deletes replacement and desktop resources. All four WebService
quota dimensions must return to the original baseline. The extra rollout is added coverage, not
an optimization saving. Existing desktop/mobile screenshots and cancellation/focus assertions finish
before this added lifecycle phase.

Functional phases fail fast. Cleanup still attempts all registered actions and preserves both body
and cleanup failures. Shared browser **transport** does not imply shared cookies, storage, routes,
contexts or logged-in state across journeys. Original screenshots and negative/native assertions
remain part of their owning phases. Quota edge cases, races and rollback remain in narrower Quotas
tests rather than being duplicated in every journey.

Verify a complete final E2E result against the planned inventory and both merged-phase records:

```bash
python3 scripts/verify_e2e_journeys.py --trx /path/to/results.trx \
  --timings-directory /path/to/current-run-timings --output .artifacts/e2e-inventory.json
```

`--mode baseline` checks the frozen original inventory before consolidation. Missing cases,
unexpected duplicate executions, retired source cases, failures/skips or inconsistent result
counters fail verification. Additional regression cases are reported separately. Each pair's
timing phases must belong to one current target-case instance within its TRX execution interval;
stale timing files or phases from separate instances cannot fill a missing phase. Timing identity
does not prove assertion semantics, so source review remains mandatory.

Performance comparisons distinguish shared transport, lifecycle changes, merges and added
quota/recreation work. Compare merges only after browser transport reuse so the same startup and
teardown saving is not counted twice. Nested phase durations are not additive wall time.

## GitOps repository scenarios

The GitHub stub serves deterministic source archives and isolated YAML-only repositories.
Repository `9002` exercises create/apply, signed push dispatch, polling after a missed push, controlled
managed-field drift in the disposable Mongo database, same-SHA poll repair, verified Sync no-op and
branch deletion without inventory removal. Repository `9003` (`pole-e2e/gitops-mixed`) has independent
immutable commit snapshots controlled by the authenticated test-only `/__e2e/gitops-mixed` endpoint.
Repeating identical commit/input is idempotent; changed content at an existing commit is rejected.

`MixedRepositoryWaitsForSecretsRepairsDriftAndThenPerformsNoOp` covers all thirteen kinds with
fifteen declarations and nineteen phase operations: ResourceGroup, IsolationScope, Secret,
WebService, RedisService, PostgresCluster, FunctionApp, three SecretBindings for runtime/bootstrap
access, ResourceConnection, DomainZone, HostnameBinding, SourceRepository and SourceBuildConfiguration.
Its fixture reads the canonical `src/features/GitOps/examples/full/manifests` YAML; only lab target,
profile, connection, application repository/branch and Function image references are substituted.
It checks the missing-value blocker and dependency recovery without a new Git commit, inert prepared
consumers, PostgreSQL native credential projection and password-authenticated login.

The Public WebService delegates image ownership to its accepted build configuration. After GitOps
apply it remains `AwaitingFirstArtifact`, with no image, build job, release, native Deployment, Pod or
Ingress. A manual Build resolves the application repository's current SHA, builds the explicit
Dockerfile through the real BuildKit/Harbor lane and activates the workload using a verified digest.
The application source uses repository `9001` on the stub's `gitops-application` branch, distinct from
the manual build tests' `main` identity; its SHA also differs from the GitOps manifest commit.

The scenario verifies connection environment values and custom ingress rules, repairs
WebService/Redis/PostgreSQL drift at the same manifest SHA, then removes the accepted hostname
attachment through the Mongo fixture and checks a `HostnameBinding` update restores it. The final
Sync reports nineteen no-op operations, preserves business revisions and the delivered digest, and
creates no additional build job. Native ingress-rule assertions do not prove routed HTTP, public
DNS or TLS. Separate routing checks must supply that evidence.

Repository `9004` isolates the lifecycle API journey from active mixed-repository polling. It uses
two sources, explicit manual adoption, pause/resume guards, protected prune with stale confirmation,
explicit rename, repository-directory relocation and terminal detach that restores manual control.
The mixed thirteen-kind scenario also confirms an empty snapshot and waits for owner finalization,
then verifies native namespace/PVC, credential, ingress and network cleanup. Assertions inspect only
the fixture-owned isolated resources and do not claim public DNS/TLS verification.

Repository `9006` (`pole-e2e/gitops-console`) isolates the browser journeys. Its authenticated
`/__e2e/gitops-console` control publishes immutable inputs; `/__e2e/gitops-console/access` changes
access only to that disposable repository, without revoking the shared GitHub connection.
`BrowserGitOpsSourceRunsSettingsAndTenantAccess` creates a source through the real pickers, proves
Retry keeps a failed run's resolved SHA while Sync reads the new branch head, follows inventory and
ownership links, reloads a selected run, resolves a settings revision conflict, and checks pause/resume,
Viewer commands and foreign-tenant reads. The source is paused before another console snapshot is
published, so later input cannot race its polling.

`BrowserGitOpsLifecycleReviewsExactPlansAndRestoresManualControl` adopts genuine manual resources,
including an unnamed SecretBinding selected by Secret/consumer metadata. It changes only the owned
Secret's reference fence in the disposable Mongo database to force a server-side stale-confirmation
conflict, then requires a fresh preview and unchecked data-loss acknowledgment. It exercises explicit
rename, directory relocation, guarded source deletion and inventory-only detach while Git is denied;
manual configuration writes work again after detach. Confirm requests contain only the preview
revision. Runtime cleanup/PVC guarantees remain covered by the existing owner/native lifecycle tests.

Screenshots in `.artifacts/frontend-gitops` cover create, overview, runs and protected preview screens
at desktop/mobile sizes in light/dark themes. Screenshots are captured outside Secret value forms;
GitOps candidate, preview and metadata responses are checked for sensitive payloads.

The mixed lane checks configuration applied; it does not require Function runtime activation before
evaluating configuration drift. The no-op check includes Function revision stability.

The separate Function live test checks native Secret projection, SDK activation and authenticated
invocation. Its lifecycle callbacks apply Domain transitions in memory; Mongo integration tests cover
durable promotion, outbox writes and rollout retention separately. Its shared `FunctionSdkTestImage`
builds a real .NET 10
application with `Pole.Functions.Sdk` pinned to the control-plane protocol version. The authenticated
`hello` handler returns only `configured`/`missing`, never the supplied Secret value. BuildKit publishes
to a unique public project in the selected local lab Harbor, so test workloads need no pull credential.
Private Docker configuration stays outside the build context. Temporary files/BuildKit access are
removed after publishing; image/project and Harbor access are removed on disposal. Successful image
publication alone is not proof of Function activation or rollout.

Fault injection stays in the test fixture; production has no test mutation endpoint. Journals exclude
manifest bodies, credentials, OAuth state, private keys and bearer tokens. Git-owned resources retain
production write/delete guards. Teardown stops the isolated application before removing only the
fixture's labelled namespaces with UID preconditions, then disposes owned test images.

Delivery must report actual results and infrastructure blockers for the mixed Aspire scenario and
separate live checks; the scenario descriptions alone are not evidence of a passing run.
A fresh complete publication gate is required after the final stage; focused browser results do not
replace its evidence.

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

The API provenance test supplies an inline Dockerfile based on `traefik/whoami:v1.11.0`, copying
the application revision marker into the image. It creates the WebService in explicit `SourceBuild`
mode with no initial image or release. A second manual Build resolves the changed application branch
without reanalysis; each job retains its own immutable commit and recipe. Deployment assertions use
the verified digest, rather than the mutable build tag. The browser journey selects the stub's
runnable `dependencies` revision, supplies repository/configuration technical names and a Dockerfile,
builds its .NET application and waits for `/health` on port 8080 to become ready. It saves a
deliberately broken COPY recipe, reloads its retained failure output, verifies the running
deployment is unchanged, then repairs
the recipe through the browser and observes a new successful release and ready workload.
Analysis is only an authoring aid; linking a repository and executing an authored recipe do not
require a selected candidate. A new Build uses the current application branch; recovery of the
same job retains its accepted input. There is no exact-input Retry API. GitOps polling, Sync and
webhook processing never queue application builds.

Source-build cleanup deletes the WebService first, then removes any remaining build configuration
and finally unlinks the repository. Deleting a configuration still bound to image authority is
blocked even when no build is running. The ordered cleanup waits for dependent deletion before
removing the resource group and its namespace.

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

## Lifecycle defaults and timing evidence

The serial Aspire fixture owns one Playwright run-server container. Every browser journey opens
fresh browser connections and contexts, with independent cookies, storage, routes, pages and journals;
its contexts are closed before fixture teardown removes the shared transport. The standalone
browser-start test still owns its own container. Independent cleanup groups are limited to two
actions. The two sequential 65-second observation windows, twenty native Pod samples, twenty
metadata samples and their latency budgets remain explicit scenario guarantees.

RuntimeCell pending-deletion checks use a five-second persisted due time. GitOps cleanup uses
`GitOpsWorker:CleanupCheckSeconds` (default 5, range 1–300), independently of lease heartbeats.
Hangfire scheduled-job polling uses `Hangfire:SchedulePollingIntervalSeconds` (default 5, range
1–60); minutely recovery scanners remain fallback paths.

Set `POLE_TEST_TIMING_DIRECTORY` to collect opt-in phase timings and separate
`lifecycle-events.jsonl` evidence from the primary API process. The fixture owns the asynchronous
log reader and cancels and awaits it after application disposal. Only exact application-owned
lifecycle messages with validated timestamps/counters and hashed identities are persisted;
raw log lines, payloads and credentials are excluded. These records are independent of the HTTP
journal limit. Diagnostic storage failures do not replace a scenario failure, and nested spans
must not be added together as wall time.

## CI

The [Test workflow](../.github/workflows/kubernetes-lab.yml) runs the full lab and the complete
backend test set on GitHub-hosted Ubuntu for every pull request and push to `master`. The workflow
removes the unused preinstalled Android SDK to retain enough disk for the nested clusters, restores
and builds once, then runs the shared `quality_gate.py backend --results-directory` executor with
coverage and a fresh results directory. The complete local gate uses that same project inventory,
order and collector. Focused development retains plain `dotnet test` without coverage. The
worktree-scoped leak audit still runs after test failure. Frontend tests, build and lint run in a
separate job.

The workflow always publishes a sanitized failure-evidence artifact containing test outcomes,
bounded phase records and runner headroom summaries. It excludes raw TRX output/exception messages,
credentials, cookies, headers, arbitrary payloads and workload logs. See [safe failure evidence](testing.md#safe-failure-evidence)
for recording and redaction contracts.

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
around a held HTTP read. `TenantAccessRealtimeJourney` combines same-account tenant-tab isolation
with current roles, Viewer actions, held-read fencing and membership removal/restoration without
relying on token rotation.
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

## Journey synchronization and restoration contracts

Each family establishes an authoritative condition and owns restoration before changing state.
Shared helpers do not remove tenant, ownership, UID, revision or generation fences.

| Family              | Required condition                                                                           | Owned changes and cleanup                                                                        |
| ------------------- | -------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------ |
| Runtime logs        | Exact deployment, replacement Pod UID, container and read mode; one click starts one request | Owned workload; cancel exact stream on replacement, scope exit or close                          |
| Source builds       | Accepted build/release IDs and terminal server state                                         | Register BuildKit recovery and Git stub revision restoration before mutation                     |
| Multi-cluster       | Placement target and accepted rollout with current native observed generation                | Register target restoration before disable; remove each owned namespace with UID checks          |
| Releases            | Accepted new release revision and current native readiness after retry                       | Restore target settings even if the mutation response was lost; then remove owned workload       |
| Tenant workspace    | Known resource IDs and owned Pod/container, isolated tenant callbacks                        | Restore tenant context; cancel the exact old runtime request                                     |
| Auth and GitHub     | Non-null journal and explicit authentication/source state                                    | Restore stub revision and temporary client/user settings                                         |
| Functions / Orleans | Both owned silos admitted and expected cross-silo execution/stop behavior                    | Remove owned function/release resources; preserve normal stop budget                             |
| Provider journeys   | Known profile/resource IDs and read-back configuration; loaded workspace before screenshots  | Remove owned workloads before disabling their eligible profiles                                  |
| Health              | Accepted revision/generation, Applied distinct from readiness, native observed rollout       | Remove owned workload and probe configuration; preserve freshness windows                        |
| Storage             | Current target, known discovery IDs and bounded monotonic probes                             | Restore temporary target/storage settings; remove only owned resources                           |
| Realtime            | Current acknowledged coverage and successful reads, independent transport outcomes           | Clear coverage on close/unsubscribe/navigation; fault held phases on intercepted request failure |

Use `TestResourceScope.RunAsync` to preserve body and cleanup failures together. Register every
restoration before changing shared state, then attempt remaining cleanup even if one action fails.
Use `BrowserEvidence.NewPageAsync` for page-level phase evidence and forward `TestWait.UntilAsync`
callback tokens into the actual probe. Playwright role/label assertions wait for defined UI state;
quiet windows and SLO sampling remain explicit measurements rather than arbitrary delays.

The CI-stabilization acceptance contract requires ten separately recorded repetitions each of
runtime logs, Health, metadata burst and two Orleans silos. Every failure requires diagnosis and
correction; another green repetition does not erase it. After all changes and independent review,
run the complete local gate with coverage and no filters or skips, then require three independent
complete CI runs of the same final SHA on clean runners. Keep each attempt's evidence and verify
that no owned test resources remain. These are acceptance requirements, not a claim that the
current checkout has completed them. Runner topology changes, quarantine, automatic retry-to-green
and blanket timeout increases are outside this stabilization contract.
