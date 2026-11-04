# Repository GitOps

Pole synchronizes a tenant-owned GitHub repository source through ResourceGroup, Secret,
SecretBinding and WebService providers. The source API, durable worker, polling, signed push
webhooks and dependency recovery now use phase-aware managed configuration planning. Every poll
and Sync checks drift, including unchanged commits, repairs mutable configuration and records
verified no-op without business revisions, resource events or releases. This remains one part of
the #88/#98 PR; the other nine providers and lifecycle commands belong to later stages.

The [canonical repository contract](docs/repository-contracts.md) defines all thirteen kinds,
source/run/inventory boundaries, YAML identity and the remaining delivery stages.

## Execution

`POST /api/v1/gitops/sources/` atomically stores source configuration, the first requested
generation and an outbox signal. A consumer dispatches a Hangfire job containing only tenant/source
IDs. The Mongo recovery scanner also discovers pending requests, delayed runs and expired claims.
An unavailable enqueue cannot lose the request.

`RepositorySourceJob` claims a run before network access. It checks current tenant/connection
access, resolves the branch to a full SHA, persists it, reads that exact commit and compiles the
whole snapshot before resource writes. Fetch/compile failures remain inspectable runs and never
become an empty desired state. A valid existing directory without YAML is an empty snapshot;
retained ownership is marked pending removal, never deleted.

Accepted input stores normalized declarations, allocated resource IDs and ordered operations.
Internal references use allocated IDs; successful external resolution is pinned durably before
provider mutation. Recreated names never silently change the accepted target. Execution uses the
existing Prepare/Bind/Configure phases, preserving inert WebService placement until complete
configuration can activate it. Runtime readiness and configuration applied are separate facts.

Every provider transaction performs real fencing writes against source revision/fence, current
run and worker/attempt/expiry. Resource mutation, ownership, receipt, release and outbox commit
atomically. Successful receipts prevent replayed side effects after a crash. Finalization checks
successful matching receipts for every accepted operation. Lease predicates and expiry use Mongo
time; local scheduling uses `TimeProvider`.

Source creators and Sync/Retry users remain audit metadata. The worker has server authority;
removing an author's membership does not remove tenant-owned automation authority. WebService
releases record source/run/commit provenance without fabricated user IDs or email addresses.

## Create named catalog entries

ComputeProfile and DeploymentTarget creation require an explicit immutable `technicalName` in
addition to the mutable `displayName`. Names use the lowercase DNS-label rule, are unique within
each platform catalog, and are supplied in the existing platform-admin create request. Creation
returns a named resource at revision 1, immediately usable by IaC; there is no assignment endpoint
or second naming request. The API never derives a technical name from a display label.

For example, include `"technicalName": "small"` in `POST /api/v1/platform/compute-profiles/`
and `"technicalName": "lab"` in `POST /api/v1/platform/deployment-targets/`, together with their
normal configuration fields. Deployment-target connection validation and existing admin permissions
remain unchanged. Update requests cannot rename the identity; display labels and configuration
remain editable. Duplicate names return a typed 409 conflict, including concurrent creation.

The built-in ComputeProfile is created with the explicit name `default`. Deployment-target bootstrap
uses `DeploymentTargets:Bootstrap:TechnicalName` (explicit built-in default `local`), independently
of its display label. Bootstrap and all catalog creation paths persist names from the start.
Initial schema/index setup requires names; no compatibility assignment, inferred names or backfill
is provided. Actual future ComputeProfile configuration edits retain immutable history, and Git
continues to pin the desired revision explicitly.

Name resolution runs through provider contracts with the trusted tenant, workload type and placement
context. Unknown, inaccessible and ambiguous references block. Prepared services retain resolved
name/ID pairs internally; retries and unchanged replays never switch to another catalog object that
later reuses a name. An explicit profile revision selects only that bound profile's history.
Changing the declared profile name is an explicit desired-state change; target replacement remains
blocked. ID-based YAML is rejected structurally. There are no deployed legacy snapshots to migrate.

## Versioned schema and example

Each YAML document has exactly `apiVersion`, `kind`, `metadata.name` and `spec`. Documents
and files are unordered. Placement references use immutable catalog technical names, never database
IDs or mutable display labels. ComputeProfile revision is required; it is never resolved implicitly
to latest. Names are lowercase DNS labels beginning with a letter (at most 63 characters).

```yaml
apiVersion: pole.io/v1alpha1
kind: ResourceGroup
metadata:
  name: demo
spec:
  description: Demo application
---
apiVersion: pole.io/v1alpha1
kind: Secret
metadata:
  name: demo-api-credentials
spec:
  resourceGroup: demo
  keys: [API_TOKEN]
---
apiVersion: pole.io/v1alpha1
kind: WebService
metadata:
  name: demo-api
spec:
  resourceGroup: demo
  image:
    server: docker.io
    name: nginx
    tag: "1.27"
  placement:
    deploymentTarget: lab
    computeProfile:
      name: small
      revision: 1
  ports:
    - name: http
      servicePort: 80
      targetPort: "8080"
      protocol: TCP
  probes:
    startup:
      {
        path: /health,
        port: "8080",
        initialDelaySeconds: 5,
        periodSeconds: 10,
        timeoutSeconds: 5,
        failureThreshold: 3,
        successThreshold: 1,
      }
    liveness:
      {
        path: /health,
        port: "8080",
        initialDelaySeconds: 5,
        periodSeconds: 10,
        timeoutSeconds: 5,
        failureThreshold: 3,
        successThreshold: 1,
      }
    readiness:
      {
        path: /health,
        port: "8080",
        initialDelaySeconds: 5,
        periodSeconds: 10,
        timeoutSeconds: 5,
        failureThreshold: 3,
        successThreshold: 1,
      }
  secretEnvironment:
    API_TOKEN: { secret: demo-api-credentials, key: API_TOKEN }
---
apiVersion: pole.io/v1alpha1
kind: SecretBinding
metadata:
  name: demo-api-credentials-access
spec:
  secret: demo-api-credentials
  webService: demo-api
```

Optional fields/defaults: group `description: ''`, `tags: []`; Secret `purpose: ''`;
WebService `exposure: Private`, `replicas: 1`, `environment: {}`, `secretEnvironment: {}`,
`tags: []`; binding `keyMappings: {}` (all expected keys, unchanged names). Bindings currently
select all service deployments in the same default isolation scope. A Secret can be referenced
across sources; the reference does not transfer ownership or relax group/scope/placement admission.

The first WebService slice deliberately supports one explicitly selected target, default group
scope, Private exposure, explicit image, numeric TCP/UDP ports, HTTP probes and disabled
autoscaling. Public exposure, extra scopes, multiple targets, custom domains, connection refs,
source builds and the remaining resource kinds await the next provider stages. Git fields are
complete authoritative desired state: omission resets optional fields to these defaults.
Changing group/target or a binding's Secret/consumer requires an explicit replacement decision;
the pipeline blocks it and never guesses a rename or adopts a manual resource.
Changing or removing the projected alias of a key used by the working service blocks with
`SecretKeysInUse`, including identity-to-alias and alias-to-identity changes. The existing
projection and service remain usable. Stage a replacement source key and grant, supply its value,
move the service reference, wait for the replacement rollout to complete successfully on every
deployment, then retire the old key/alias. The recovery job retains old key/alias pins until complete
native rollout proof is fenced against current deployments. `ConfigurationApplied` alone is insufficient:
old runtime consumers may still use the previous key.
An asynchronous projection replacement
cannot safely rename a key still referenced by the current workload template. Equivalent aliases
and remapping keys no longer referenced by the service remain supported.

## Source API

All source routes use bearer authentication and `X-Pole-Tenant-ID`. Viewer can read;
Member/Admin/Owner can write, with existing platform-admin behavior preserved. Foreign sources
and runs return not-found. Tenant/user authority comes from the server, never the body.

Under `/api/v1/gitops/sources`:

| Method/path                                | Input and behavior                                                                                                                                                                    |
| ------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `POST /`                                   | `idempotencyKey`, `provider`, `connectionId`, `externalRepositoryId`, `branch`, `manifestDirectory`, optional `pollIntervalSeconds`; returns the created source, not an apply result. |
| `GET /`, `GET /{id}`                       | Configuration, generations, commit markers, current/latest run identity, sanitized latest-run diagnostics and inventory counts. Lists use bounded `page`/`pageSize`.                  |
| `PUT /{id}`                                | `expectedRevision`, `pollIntervalSeconds`; only polling policy can change in this stage.                                                                                              |
| `POST /{id}/sync`                          | `expectedRevision`, `idempotencyKey`; returns 202 for a durable request to resolve the current branch.                                                                                |
| `POST /{id}/retry`                         | `expectedRevision`, `idempotencyKey`, `runId`; returns 202 for a new run using the pinned input.                                                                                      |
| `GET /{id}/runs`, `GET /{id}/runs/{runId}` | Paginated history and run details.                                                                                                                                                    |
| `GET /{id}/runs/{runId}/operations`        | Bounded pending/succeeded/blocked descriptors and sanitized diagnostic codes.                                                                                                         |

Repeated idempotency keys with identical input do not create another source/generation; changed
input conflicts. Retry requires the latest failed/blocked run at the same configuration revision,
a pinned SHA, and no active or pending work. If the run accepted a snapshot, Retry retains it.
Without a SHA, request Sync. New signals during a run coalesce into a later generation and never
change its commit. A retry reserves its run before later Sync requests can be claimed.

Observed commit advances after resolution, attempted commit after acceptance, fully applied commit
only after all phases succeed. Completed generation includes failures/blockers; it is not a success
flag. Polling can skip an unchanged commit/revision only after the latest successful run, never
after a later failed or partially applied run.

The former manual snapshot/resume routes are removed. There is no public YAML apply API.
Responses do not expose raw manifests, accepted specs, credentials or Secret values.

## Scheduling and GitHub webhook

Source polling defaults to five minutes and has a one-minute minimum. `GitOpsWorker` server
options default to a one-minute scan, batch 100, two-minute lease, 20-second heartbeat and five
consecutive failed attempts. Temporary failures persist delays of 1/2/4/5 minutes; exhaustion
finishes the run with an explicit failure. Polling or Sync can start a new run later.

Configure a separate `GitHubWebhook:Secret` (required at startup when GitHub is enabled), and point
the GitHub App push webhook at `POST /api/v1/integrations/github/webhook`. Use a randomly generated
secret. The endpoint verifies HMAC-SHA256 over the raw bytes with constant-time comparison before
parsing, and rejects bodies above 25 MiB, including streamed bodies without Content-Length.

A delivery receipt stores only its GUID, payload hash, event type and receive time. Receipt and
GitProviders-owned outbox event are atomic. Redelivery is idempotent; a reused ID with different
content conflicts. Signed ping/irrelevant events are acknowledged without GitOps work. Installation
identity resolves the stored connection/tenant; payload tenant fields are ignored. The consumer
calls the GitOps scheduling contract using provider/connection/repository/branch. The SHA is only
a hint: the worker resolves the current branch. Deleted branches fail with ref-not-found and
preserve inventory. Polling independently recovers missed deliveries.

## Secret values and recovery

YAML declares Secret metadata and keys, never values. Supply values through the existing protected
Secret API/UI. The Secrets-owned `SecretChangedEvent` adapter forwards value rotations through the
generic GitOps dependency wake contract. Eligible blocked runs reserve an accepted-input retry;
replayed signals are idempotent and do not displace newer pending Sync work. GitOps does not import
Secrets Events. Existing version-aware projection and old-reference rollout safeguards remain active.

## Persistence and remaining stages

`RepositorySource`, `GitSynchronizationState` and `GitReconciliationRun` replace the manual source
and execution collections. `ManagedDeclaration` remains ownership inventory, `GitOperationReceipt`
remains operation truth and `GitAcceptedReference` retains resolved identities. All stored models
belong to Domain with BSON attributes; Application owns filters, transactions and boundary mappings.
There are no compatibility readers, backfills or dual writes: Pole has not been deployed.

Stage 4 stores revisioned operation plans and desired/applied projections in the existing inventory.
Allocated IDs may be created; a missing previously created resource or changed identity blocks.
Accepted references remain pinned. Preconditions use owner-provided transactional reads and real
fences. Three immediate conflicts fall back to persisted recovery; API summaries count operations
and expose actions and field paths without projection values. WebService Prepare does not mark the
full configuration applied; Configure verifies managed fields while preserving connection references
and server credentials.

Additional providers, source
pause/resume, detach/delete, adoption, replacement, relocation and preview confirmation remain
later work. Prune stays `Retain`; there is no parallel ownership inventory or fabricated projection
state. Live Kubernetes drift and runtime readiness remain the runtime reconcilers' responsibility.
No new GitOps UI is included.

Stage verification includes solution build, full GitOps/GitProviders/SourceBuilds and architecture
suites, full Integration and GitHub setup/repository-flow/drift Aspire E2E. Frontend gates apply when
frontend transports or types change. The E2E fixture
uses its isolated service graph and the existing full Kubernetes lab. The complete publication gate
runs after all stages before the single PR; stage evidence does not replace it.
