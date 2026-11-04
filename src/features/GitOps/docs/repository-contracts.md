# Repository GitOps contracts

This is the canonical contract for the single #88/#98 delivery. Repository acquisition, the source
API, durable execution, polling, push webhooks and managed drift reconciliation are implemented.
All thirteen resource providers are registered, including stage-6 connectivity, domains and source
delivery. Public lifecycle commands belong to stage 7. Manual snapshot entrypoints are removed.
The verification section describes required evidence separately from implementation scope.

## Ownership and module boundaries

The shared phase identity lives once in dependency-leaf GitOps.Primitives, used by Domain and Contracts.
GitOps.Domain owns stored source configuration, synchronization state, run/lease, operation,
inventory and preview records. BSON attributes stay on those models; Application owns collections,
transactions, codecs and boundary mappings. Repository source/synchronization/run collections are active.
Manual source/execution models and storage are removed; there are no compatibility readers, backfills or dual writes.

GitOps owns source and resource extension contracts. GitProviders.Application implements the
source extension; each resource provider implements its own resource extension. Generic GitOps
must not reference concrete provider projects or branch on known resource kinds. HTTP DTOs remain
beside endpoints. Contracts operations accept at most three parameters, counting session and cancellation.

Source configuration contains tenant, connection ID, external repository ID, branch, directory,
configuration revision, polling policy, activity and prune policy. Internal bootstrap APIs may use
Pole IDs. YAML cannot supply tenant, actor authority, credentials or internal resource IDs.
A source belongs to its tenant; its creator is audit metadata. Background execution uses server
identity and current tenant/connection authorization, including after the creator loses membership.

Source synchronization state holds requested/completed generations, current run, fencing version
and distinct observed, attempted and fully applied commits. A run pins configuration revision,
requested generation, immutable commit, accepted snapshot and execution authority. Lease attempt
and source fence protect writes; checking a lease before a transaction alone is not sufficient.
A push during a run schedules a later pass. Runtime readiness is separate from configuration applied.

Pause retains ownership and manual-write guards. Detach fences in-flight work, retains resources
and returns manual control. Delete source requires zero owned resources, including pending removal;
otherwise it returns DetachRequired. Neither pause nor delete implicitly deletes workloads.
Prune defaults to Retain in the source factory. Actual lifecycle commands arrive in stage 7.

## Snapshot and planning boundaries

Resolve the configured branch to an immutable commit, then read only that exact commit and directory.
The snapshot result must prove a complete successful read, including a valid empty directory.
Access denial, repository/ref/directory absence and rejected archive/content are distinct outcomes;
none may be converted to an empty desired set. The stage-2 GitHub adapter implements the repository input described below; the stage-3 worker orchestrates these contracts.

Resource providers normalize desired declarations, describe dependencies, resolve names, read current
managed configuration, project desired configuration and apply operations. The generic planner compares
normalized projections and returns create, update, no-op, blocked, adoption, replacement or removal.
Providers perform create/update mutations and guard no-op preconditions; the engine records the
operation result. Adoption/replacement/removal return typed blockers because those lifecycle
operations are not connected. Read projections contain only managed fields. Runtime observations,
revision timestamps, credentials and Secret values must never enter a projection or diff.

Projection references use resolved internal IDs inside the server boundary, so a renamed/recreated
reference cannot appear identical just because a name matches. Current reads return a version vector
covering all documents that contributed managed state. A plan is advisory until its source, ownership,
resource versions and references are revalidated in the mutation transaction. The durable worker uses
this phase-aware planning on every poll and Sync, including the same commit.

## YAML identity and coverage

Envelope: `apiVersion: pole.io/v1alpha1`, `kind`, `metadata.name`, `spec`. Unknown properties are rejected.
Names are immutable technical names, unique by `(tenant, kind, name)`; group and isolation are admission
constraints, not address qualifiers. References are tenant-local, may cross source ownership, and never
transfer it. Accepted IDs remain pinned; name reuse cannot silently rebind an accepted declaration.

DeploymentTarget is referenced by technical name. ComputeProfile and StorageProfile require explicit
technical name **and revision**, with immutable catalog history. GitHub connections have immutable
tenant-unique technical names; account display names and Mongo IDs are not YAML connection identities.
Generated runtime objects are never declarations.

All rows describe implemented provider schemas. Creation-only identity changes require the explicit
lifecycle decisions of stage 7; they never imply silent adoption or replacement.

| Kind / owner                             | Status and managed fields                                                                                                                        | References / immutable boundaries                                                                                                    | Excluded fields and phases                                                                                                |
| ---------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------- |
| ResourceGroup / ResourceGroups           | Implemented: description, tags                                                                                                                   | Name is identity                                                                                                                     | Default scope, registrations, lifecycle; Prepare                                                                          |
| IsolationScope / ResourceGroups          | Implemented: name, parent group                                                                                                                  | Tenant-wide name; parent group immutable; `default` reserved                                                                         | Runtime cells, lifecycle; Prepare after group; create/no-op                                                               |
| Secret / Secrets                         | Implemented: resourceGroup, isolationScope, purpose, keys                                                                                        | Group/scope immutable; declared key metadata only                                                                                    | Values, ciphertext, value versions, binding state; Prepare                                                                |
| SecretBinding / Secrets                  | Implemented: secret, consumer kind/name, usage, keyMappings                                                                                      | Secret/consumer/usage immutable; scope admission; Runtime for WebService/FunctionApp, Bootstrap for PostgreSQL                       | Native projections and retained rollout pins; Bind                                                                        |
| WebService / WebServices                 | Implemented: image, environment, logical secrets, ports, probes, tags, exposure, fixed replicas or autoscaling, pinned compute                   | Group/scope/target immutable; Secret grants required                                                                                 | Connection environment references, registry credentials, generated hostnames, releases, observations; Prepare → Configure |
| RedisService / RedisServices             | Implemented: tags, pinned compute; storage size/profile and placement on creation                                                                | Group/scope/target and storage profile/revision/size immutable                                                                       | Runtime, timestamps; Prepare → Configure                                                                                  |
| PostgresCluster / Postgres               | Implemented: version, instances, storage size, username/bootstrap reference, pinned compute/storage, placement                                   | Version, username, bootstrap identity, group/scope/target and storage profile/revision immutable; capacity updates retain mode rules | Credentials, runtime, timestamps; Prepare → Configure with separate Bootstrap binding                                     |
| FunctionApp / Functions                  | Implemented: functionRoute, image, environment, logical secrets, resources, scaling, handlers/triggers, tags, pinned compute, explicit placement | Route/group/scope/target immutable; runtime Secret grants required                                                                   | Protocol/runtime data, invocations, generated trigger IDs and schedule versions; Prepare → Configure                      |
| ResourceConnection / ResourceConnections | Implemented: named source, destination, endpoint | Accepted endpoint IDs and target compatibility; identities immutable | Runtime network observations, credentials; Bind after source Prepare and destination Configure |
| DomainZone / Domains | Implemented: technical name, target, DNS label | Target and label immutable; current managed target reservation policy | Computed DNS name and generated zones; Prepare |
| HostnameBinding / Domains | Implemented: technical name, zone, workload, target, relative name; attachment repair | Accepted zone and Public WebService IDs; address/attachment immutable; cross-source references preserve ownership | Generated identities and runtime ingress observations; Bind after zone Prepare/workload Configure |
| SourceRepository / GitProviders | Implemented: technical name, connection, repository ID, branch | Accepted connection ID; location immutable; no mandatory analyzer | Display URL/privacy metadata, candidates and credentials; Prepare |
| SourceBuildConfiguration / SourceBuilds | Implemented: technical name, repository, WebService, inline Dockerfile | Accepted repository/target IDs immutable; recipe revisions; explicit image authority | Jobs, application commits, artifacts, logs and credentials; Configure after repository/WebService Prepare |

Schemas and phase evidence live with their owners: `DeclarativeResourceGroupProvider`,
`DeclarativeIsolationScopeProvider`, `DeclarativeSecretProvider`, `DeclarativeSecretBindingProvider`,
`DeclarativeWebServiceProvider`, `DeclarativeRedisProvider`, `PostgresDeclaration.fs` and
`FunctionDeclaration.cs`. Later-provider API evidence remains in the Domains, ResourceConnections,
GitProviders and SourceBuilds endpoints. Build output cannot mutate Git-owned desired image configuration.

## Target source API

All routes are under the existing authenticated tenant-context `/api/v1/gitops/sources` root.
Use normal tenant read/write authorization; input cannot elevate authority. Repository permissions
are checked through the provider. Optimistic conflicts use typed outcomes mapped to HTTP 409.

| Operation         | Target route / inputs                                                                            | Result                                                          |
| ----------------- | ------------------------------------------------------------------------------------------------ | --------------------------------------------------------------- |
| Create            | POST root; idempotency key, connection/repository IDs, branch, directory, sync/prune policy      | Source identity and revision; no YAML files                     |
| List / detail     | GET root / `{id}`                                                                                | Configuration, sync progress, blockers, inventory summary       |
| Edit              | PUT `{id}`; expected revision, configuration; relocation additionally requires confirmed preview | Updated revision or stale/conflicting preview                   |
| Sync / retry      | POST `{id}/sync` / `{id}/retry`; expected revision                                               | Durable requested generation, not synchronous apply success     |
| Pause / resume    | POST `{id}/pause` / `{id}/resume`; expected revision                                             | Revised activity state; pause fences in-flight work             |
| Preview           | POST `{id}/previews`; intended adoption/removal/replacement/relocation/detach                    | Server-owned reviewed operation set and preconditions           |
| Confirm           | POST `{id}/previews/{previewId}/confirm`; expected preview revision                              | Confirmation of that exact preview, not client-selected actions |
| Detach            | POST `{id}/detach`; expected revision and confirmation reference                                 | Retained resources returned to manual control                   |
| Delete            | DELETE `{id}`; expected revision                                                                 | Deleted only after ownership is empty                           |
| Runs / operations | GET `{id}/runs`, `{id}/runs/{runId}`, `{id}/runs/{runId}/operations`                             | Tenant-scoped progress and sanitized results                    |

Stage 3 exposes create/list/detail/polling-policy update/sync/retry and run/operation reads.
Lifecycle routes in this table remain target contracts for later stages. Manual snapshot entrypoints
are removed; no public YAML apply API or compatibility API is exposed.

## Preview and confirmation

A stored preview binds tenant/source, source configuration revision, commit, desired hash,
concrete actions/phases, resource identities, ownership, resource version vectors and configuration hashes.
Creation preconditions include absence; appearance of a resource invalidates a create preview.
For source detach/relocation, observations must cover the complete current inventory, including resources
added after preview creation. A new owned resource invalidates the reviewed set.
Confirmation carries only a preview reference/revision. The server rereads the preview and current
state under authorized tenant context. Any changed precondition invalidates it; source/commit changes
cannot reuse an old confirmation. Approval does not waive admission, data protection or ownership.
Protected deletion, empty-set deletion, adoption, immutable replacement and relocation require the
concrete reviewed action. Failed read/preflight never authorizes deletion. Stage 7 implements these commands.

## Verification and delivery

Required evidence covers current-schema BSON, factories, phases, normalized projections, explicit
constructor inputs and pinned identities. Mongo checks cover all thirteen providers, catalog history/current
policy, mutation/quota/ownership/receipt/revision/outbox atomicity, fencing, retry, bootstrap recovery,
rollout retention and absence of no-op side effects. Architecture checks enforce Domain ownership,
provider boundaries, records and the three-parameter contract limit. No legacy documents exist.

Stage 5 requires solution build; the affected feature and architecture suites; full
`Pole.Tests.Integration`; frontend `npm test`, `npm run build`, `npm run lint`; GitHub setup and mixed
repository Aspire E2E; and the dedicated PostgreSQL/Function live checks. See the
[E2E boundaries](../../../../docs/aspire-e2e-testing.md#gitops-repository-scenarios). Test definitions
and fixture image publication are not evidence that runtime activation or a full suite passed.
Record actual results at delivery; unavailable infrastructure is a verification blocker. A fresh,
unfiltered publication quality gate and independent review remain required after all stages.

## Stage 2: GitHub repository input

GitOps accesses public and private repositories through the current tenant's GitHub App connection
and external repository ID. It neither links a build SourceRepository nor runs build analysis.
Resolve the branch once and read the resulting full commit SHA. Build analysis also pins every
contents read to its resolved SHA. Connection access is rechecked on each operation; source-author
membership is not execution authority.

Connections require an explicit immutable TechnicalName at installation setup, using the shared
ResourceTechnicalName rule. Names are unique within a tenant. Setup state retains the name across
installation and OAuth callbacks. Reconnecting the same installation/name retains its ID; a different
name is a conflict. Installation ownership and connection/outbox transaction boundaries remain intact.

The adapter reads .yaml and .yml recursively, case-insensitively by extension, from the configured
case-sensitive directory. Empty directory configuration means repository root. Results contain ordinally
sorted repository-relative paths. A complete existing directory without YAML is a successful empty
snapshot; a missing directory or failed read never implies empty desired state.

A complete Git tree at the same commit establishes directory existence and expected manifest paths.
Truncated trees, selected submodules, missing archive files and partial archives are rejected. Selected
symlinks/hardlinks and special entries are rejected; links outside the directory are never followed.
All archive entry paths must be safe, unique and share one root. Sparse metadata is rejected before
logical expansion, including outside the selected directory. Unsupported TAR metadata returns a typed
snapshot rejection. UTF-8 is strict, with BOM allowed.
The bounded temporary archive is read without extracting files and removed on every exit path.

Server archive defaults are 100 MiB compressed, 500 MiB decompressed and 50,000 entries. Manifest
input retains the shared limit of 256 files and 1 MiB of UTF-8 content. Limits reject rather than truncate.
Archive redirects are bounded and allowlisted; credentials never cross origins. The HTTP client's
configured timeout bounds the complete download, including redirects and streamed response bodies;
caller cancellation still propagates. Expected access,
missing repository/ref/path, snapshot rejection and temporary service failures have distinct outcomes.

Stage 2 supplied this adapter; stage 3 now connects it to durable execution, webhook and polling.
No public YAML apply API is exposed. Verification includes
GitProviders/GitOps/SourceBuilds unit suites, architecture, full shared integration, frontend gates and
GitHub setup E2E. The publication gate runs after all stages of the single PR.

## Stage 3: durable repository execution

Stage 3 replaces the manual source/execution storage and HTTP input with repository
sources, synchronization generations and durable runs. Ownership inventory and receipts remain the
single transactional authority. Runs exist before Git resolution and retain typed acquisition failures.
An accepted input never changes SHA; Retry targets the last failed/blocked pinned run, while Sync
resolves the current branch. Requested work arriving during a run remains pending for the next pass.

Mongo owns claims, expiry, fencing and recovery. Lease expiry predicates use database time. Provider
mutations write the current fence and atomically retain ownership, receipts, releases and outbox.
Source authors are audit metadata; execution uses server authority and current tenant/connection access.
The source API exposes create/read/polling-policy update/sync/retry/run operations; no new UI is added.
Signed GitHub pushes and polling request generations through a generic GitOps-owned boundary.
Prune stays Retain. Stage 4 below adds drift planning; lifecycle commands remain later stages of this single PR.

The active API, defaults, operational flow and webhook configuration are documented in
[Repository GitOps](../README.md#source-api). Source updates currently accept polling policy only.
Source/run IDs are canonicalized at the HTTP boundary; views include sanitized latest-run blockers.
Run completion verifies receipts for the complete accepted operation list before advancing the
fully applied marker. Retry and dependency wakeup reserve their immutable input transactionally.
GitOps-owned scheduling contracts keep GitProviders/Secrets event adapters in their owning features.

## Stage 4: managed configuration reconciliation

Every poll and Sync evaluates provider-owned managed projections, including unchanged commits.
The worker may reuse accepted input from the latest successful run at the same source revision/SHA,
but must recheck repository access and compute fresh phase plans. Retry keeps immutable accepted IDs,
not stale plans. Kubernetes observations, credentials and Secret values are outside configuration drift.

Plans are persisted per run/resource/phase with a revision, preconditions, action, managed hashes,
sorted changed field paths and evaluation time. Receipts alone record execution results. Ownership
and desired/applied projections remain in the single ManagedDeclaration inventory. Allocated IDs
permit first creation; missing previously created identities block without recreation or rebinding.

Create/update/no-op require transactional source/lease, ownership, identity and dependency fences.
No-op may write technical fences and GitOps bookkeeping, but never business revisions, timestamps,
resource events or releases. Preconditions are replanned at most three times immediately, then use
the persisted recovery backoff and five-failure bound. Applied projections require the final phase.

API summaries count operations rather than resources and expose actions and field paths only.
Adoption, replacement, removal and other lifecycle commands remain blocked until their later stage;
prune stays Retain. No new timer, endpoint family or UI is introduced.

## Stage 5: catalog references and workload providers

### Catalog and placement rules

Every workload selects one DeploymentTarget and an explicit ComputeProfile name/revision.
`placement.isolationScope` is optional; omission selects the group's default scope. Secret uses
`spec.isolationScope`. Additional scopes have immutable tenant-wide names; the system default
scope remains one per group and `IsolationScope/default` cannot be declared. Accepted scope,
resource and reference IDs remain pinned. Scope supports create/no-op; changing its parent group
blocks. Created resources that disappear are not recreated or replaced by same-name objects.

StorageProfile creation takes an immutable globally unique technical name, while display name stays
mutable. Root changes, immutable revision snapshots and outbox commit together; a no-op creates no
history revision. A pinned historical revision supplies the storage class for the selected target.
Current profile enablement, tenant access, allowed workload types, target availability and size policy
are checked again. Discovery still validates storage-class availability and returns typed failures.
Transactional fences cover current policy and the pinned snapshot; history cannot bypass revocation.

`storage.profile: { name, revision }` and the selected class are immutable after workload creation.
Redis additionally fixes `storage.sizeGb`. PostgreSQL allows only its existing supported resize path:
StatefulSet mode requires one instance and rejects resize; Operator mode permits supported instance
changes and growth, rejects shrink and unsupported compute runtime classes. Deployment mode is server
configuration, never YAML. Version, username and the accepted bootstrap Secret/key remain immutable.
Compute changes use each owner's admission, quota and revision workflows.

### Grants, phases and updates

SecretBinding has `secret`, `consumer: { kind, name }`, optional `keyMappings` and `usage`.
`usage: Runtime` is the default for WebService/FunctionApp. `usage: Bootstrap` grants PostgreSQL
credential initialization without creating a Kubernetes projection for that grant. An empty
`keyMappings` grants all expected keys with unchanged names. A reference alone grants no access;
current tenant, group/scope and placement admission still apply. The old `webService` field has no alias.
Removing or remapping a key still required by active/retained consumers is blocked. Move consumers to
new references and complete their rollout before removing the old grant/key mapping.

PostgreSQL requires `username` and `bootstrapPassword: { secret, key }` plus a Bootstrap grant.
Secrets validates the grant, scope, key and source version, then atomically creates the encrypted
owner-managed credential. GitOps snapshots, plans, receipts, diagnostics and events never receive
plaintext. Missing values block Configure; supplying them wakes eligible runs without a new commit.
Once bootstrapped, source-value rotation is not a runtime dependency and does not change the database
password. PostgreSQL's existing rotation workflow remains authoritative; crash replay cannot reset it.

Workload providers contribute Prepare and Configure. SecretBinding contributes the intervening Bind
operations for consumers that need grants. Redis has no artificial Bind step. Prepare stores an inert
identity, registration and deferred placement; it starts no workload, HTTP route or timer. Configure
accepts complete configuration atomically, after owner admission and quota checks. Previous working
configuration stays in place until an update is accepted. Configuration applied and runtime ready are
separate facts; Function activation additionally requires its runtime validation/promotion workflow.

FunctionApp stores logical Secret/key references rather than Kubernetes names. Secrets resolves native
names and keys at provisioning. Admission, projection readiness and rollout confirmation preserve old
references until older revisions are retired/failed and runtime observations confirm only the ready
active revision still consumes them. Configuration applied alone does not release retained pins.
A no-op creates no Function revision, trigger ID or schedule version. Pending configuration is compared as desired state; runtime status
changes alone are not drift. Explicit resource limits retain the existing Function API semantics
alongside pinned compute placement.

WebService supports Private/Public exposure, with Private as the default. Public exposure follows
current ingress admission and generated-hostname workflows. `replicas` and `autoscaling` are mutually
exclusive; omitting both means one fixed replica. Autoscaling accepts `minReplicas`, `maxReplicas`,
`metric: { type: cpu|memory, targetAverageUtilization }`; observed replica counts are excluded from drift.
Image, ordinary/logical-secret environment, numeric TCP/UDP ports, HTTP probes and tags remain managed.
Probe `port` is numeric text, not a named port. Connection environment references and server credentials
are preserved. Custom hostname/connectivity declarations are specified in the stage-6 section below.

All operations retain revisioned plans, session-consistent reads, real resource/dependency fences,
bounded replanning, receipts and atomic mutation/quota/ownership/revision/release/outbox writes. Manual
configuration changes and deletion are blocked server-side for Git-owned resources; existing DTO/UI
ownership indicators disable those actions. Supported operational actions and PostgreSQL password
rotation retain their ordinary workflow and restrictions. Prune stays Retain; lifecycle operations
including adoption, replacement, relocation, pause/resume, detach/delete and public preview/confirm
remain unavailable.

### Mixed repository example

Create catalog entries named `lab`, `small` and `standard` first, with the shown revision available
and current policy allowing all selected workloads. The target must admit workloads; Public WebService
also needs ingress configuration. Replace the Function image below with a published application built
with the real Pole Function SDK and a registered `hello` handler. Supply `PASSWORD` through the protected
Secret API/UI after the metadata exists. The example intentionally contains no Secret values.

```yaml
apiVersion: pole.io/v1alpha1
kind: ResourceGroup
metadata:
  name: demo
spec:
  description: Mixed GitOps application
  tags: [gitops]
---
apiVersion: pole.io/v1alpha1
kind: IsolationScope
metadata:
  name: demo-scope
spec:
  resourceGroup: demo
---
apiVersion: pole.io/v1alpha1
kind: Secret
metadata:
  name: demo-credentials
spec:
  resourceGroup: demo
  isolationScope: demo-scope
  purpose: Application and bootstrap credentials
  keys: [PASSWORD]
---
apiVersion: pole.io/v1alpha1
kind: RedisService
metadata:
  name: demo-cache
spec:
  resourceGroup: demo
  placement:
    deploymentTarget: lab
    isolationScope: demo-scope
    computeProfile: { name: small, revision: 1 }
  storage:
    sizeGb: 1
    profile: { name: standard, revision: 1 }
  tags: [gitops]
---
apiVersion: pole.io/v1alpha1
kind: PostgresCluster
metadata:
  name: demo-database
spec:
  resourceGroup: demo
  placement:
    deploymentTarget: lab
    isolationScope: demo-scope
    computeProfile: { name: small, revision: 1 }
  storage:
    sizeGb: 1
    profile: { name: standard, revision: 1 }
  version: 16
  instances: 1
  username: app
  bootstrapPassword: { secret: demo-credentials, key: PASSWORD }
---
apiVersion: pole.io/v1alpha1
kind: WebService
metadata:
  name: demo-web
spec:
  resourceGroup: demo
  placement:
    deploymentTarget: lab
    isolationScope: demo-scope
    computeProfile: { name: small, revision: 1 }
  image: { server: docker.io, name: traefik/whoami, tag: v1.11.0 }
  exposure: Public
  autoscaling:
    minReplicas: 1
    maxReplicas: 3
    metric: { type: cpu, targetAverageUtilization: 70 }
  ports:
    - { name: http, servicePort: 80, targetPort: "80", protocol: TCP }
  probes:
    startup:
      path: /
      port: "80"
      initialDelaySeconds: 0
      periodSeconds: 5
      timeoutSeconds: 1
      failureThreshold: 3
      successThreshold: 1
    readiness:
      path: /
      port: "80"
      initialDelaySeconds: 0
      periodSeconds: 5
      timeoutSeconds: 1
      failureThreshold: 3
      successThreshold: 1
    liveness:
      path: /
      port: "80"
      initialDelaySeconds: 0
      periodSeconds: 5
      timeoutSeconds: 1
      failureThreshold: 3
      successThreshold: 1
  secretEnvironment:
    APP_PASSWORD: { secret: demo-credentials, key: PASSWORD }
  tags: [gitops]
---
apiVersion: pole.io/v1alpha1
kind: FunctionApp
metadata:
  name: demo-function
spec:
  resourceGroup: demo
  functionRoute: demo-function
  placement:
    deploymentTarget: lab
    isolationScope: demo-scope
    computeProfile: { name: small, revision: 1 }
  image: { server: registry.example.test, name: apps/functions, tag: v1 }
  environment: {}
  secretEnvironment:
    STAGE_FIVE_SECRET: { secret: demo-credentials, key: PASSWORD }
  resources:
    requests: { cpuMillis: 100, memoryMi: 256 }
    limits: { cpuMillis: 500, memoryMi: 512 }
  scaling:
    minInstances: 1
    maxInstances: 1
    targetConcurrencyPerInstance: 1
    maxConcurrencyPerInstance: 10
    scaleDownDelaySeconds: 60
    activationTimeoutSeconds: 120
  handlers:
    - name: hello
      maxConcurrencyPerInstance: 10
      trigger:
        type: http
        route: hello
        methods: [GET]
        exposure: Internal
  tags: [gitops]
---
apiVersion: pole.io/v1alpha1
kind: SecretBinding
metadata:
  name: demo-web-access
spec:
  secret: demo-credentials
  consumer: { kind: WebService, name: demo-web }
  usage: Runtime
---
apiVersion: pole.io/v1alpha1
kind: SecretBinding
metadata:
  name: demo-function-access
spec:
  secret: demo-credentials
  consumer: { kind: FunctionApp, name: demo-function }
  usage: Runtime
---
apiVersion: pole.io/v1alpha1
kind: SecretBinding
metadata:
  name: demo-database-bootstrap
spec:
  secret: demo-credentials
  consumer: { kind: PostgresCluster, name: demo-database }
  usage: Bootstrap
```

This is ten declarations across eight kinds and fourteen phase operations. The Function HTTP trigger
route is `hello`, without a leading slash. Timer handlers instead use `type: timer`, `cron`,
`concurrencyPolicy` (`Allow`/`Forbid`), `misfirePolicy` (`Skip`/`FireOnce`) and
`retry: { maxAttempts, backoffSeconds }`; trigger identities and schedule versions are generated.
Function resources/scaling/handlers are explicit. Redis tags, Function/WebService environment and tags,
Secret purpose and binding key mappings default to empty collections/text; PostgreSQL instances default
to one. Omission resets managed optional fields to their documented defaults, not to their prior value.

### Stage 7 handoff

All thirteen schemas/providers use the same generic planning, accepted-ID, transactional fencing,
ownership and no-op contracts. Repository snapshot acquisition uses connection plus external
repository ID independently of SourceRepository and optional build analysis. Stage 7 supplies
preview/confirmation, pause/resume, detach/delete, adoption, replacement, relocation and prune
lifecycle decisions; these actions have no successful placeholders in the current implementation.

## Stage 6: connectivity, domains and source delivery

The following schemas complete the thirteen-kind contract. Their implementation is delivered
as Stage 6; lifecycle commands remain Stage 7. All declarations retain tenant-local technical
identity and accepted reference IDs. Missing previously created resources are blockers, not creates.

| Kind | Managed fields | Phase and dependencies | Mutation policy |
| --- | --- | --- | --- |
| ResourceConnection | source `{kind,name}`, destination `{kind,name}`, endpoint | Bind after source Prepare and destination endpoint configuration | Create/no-op; identity changes blocked |
| DomainZone | deploymentTarget, label | Prepare after target admission | Create/no-op; target/label immutable |
| HostnameBinding | zone, resource `{kind: WebService,name}`, deploymentTarget, relativeName | Bind after Public WebService Configure and zone Prepare | Create/no-op; repair desired ingress attachment |
| SourceRepository | connection, repositoryId, branch | Prepare after current repository/ref admission | Create/no-op; location immutable |
| SourceBuildConfiguration | repository, webService, recipe `{dockerfile}` | Configure after repository and WebService Prepare | Editable Dockerfile; associations immutable |

ResourceConnection uses the existing provider capabilities: WebService, PostgreSQL, Redis and
FunctionApp sources, with WebService, PostgreSQL and Redis destinations. Networking admission
continues to support cross-group and cross-scope references. WebService may declare logical
connection environment references to Host, Port or a provider-published Configuration key;
connections do not grant credentials. Cyclic phase dependencies fail complete preflight.

DomainZone reserves a child label under a deployment target's managed base domain. It does not
implement external-domain ownership verification. Custom zones and hostname bindings have immutable
technical names distinct from DNS names; generated entities retain explicit system identity.
A hostname binding may attach to any Public WebService in the tenant through a narrow owner
capability without acquiring that workload's ownership. Full authoritative alias sets survive
ordinary WebService updates. Missing desired ingress attachment can be repaired; missing-created
binding cannot. Changing a workload with custom bindings to Private is blocked.

SourceRepository is independent of build analysis and candidate selection. A named build
configuration owns an explicit inline Dockerfile, with the repository root as build context.
Analyzer candidates are optional UI authoring inputs, never reconciliation inputs.

WebService image input is either the existing concrete image or
`{sourceBuildConfiguration: <technical-name>}`. Mixed forms are rejected. The image authority mode
and accepted configuration identity are immutable in this stage. GitOps manages that association;
SourceBuilds owns the delivered immutable digest and credentials, excluded from managed projection.

Initialization orders repository Prepare, WebService Prepare, build configuration Configure, then
WebService Configure. The last step can accept a complete configuration as AwaitingFirstArtifact.
It does not start a workload or HTTP routes and must not substitute a fake bootstrap image.
Bindings and desired hostnames may be accepted while awaiting the artifact. The first successful
manual build promotion activates placement and creates the release atomically.

Build is manual-only and resolves the current application branch once. Each job stores immutable
application SHA, Dockerfile, configuration revision and target association. The application SHA
is distinct from the GitOps manifest SHA. There is no separate exact-input Retry API; recovery of
the same job retains its input. GitOps poll/Sync/webhook and recipe changes never enqueue builds.
Promotion requires the latest eligible build generation and unchanged configuration/image-owner
association, uses a verified digest, and preserves current non-image fields. Placement, artifact
state, release, promotion receipt and outbox commit atomically. Failed or superseded builds keep
the previous running artifact; replay cannot create duplicate releases.
