# Repository GitOps contracts

This is the target contract for the single #88/#98 delivery. Stage 1 provides compiled models,
source acquisition and resource planning contracts, and read/plan implementations for four providers.
It does **not** enable repository fetching, background synchronization, drift repair or lifecycle commands.
The existing manual snapshot pipeline remains operational until repository input replaces it.

## Ownership and module boundaries

The shared phase identity lives once in dependency-leaf GitOps.Primitives, used by Domain and Contracts.
GitOps.Domain owns stored source configuration, synchronization state, run/lease, operation,
inventory and preview records. BSON attributes stay on those models; Application owns collections,
transactions, codecs and boundary mappings. The new models are not dual-written into the current
manual snapshot collections. Collection cutover belongs to the durable-worker stage.

GitOps owns source and resource extension contracts. GitProviders.Application will implement the
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
none may be converted to an empty desired set. GitHub implementation and extraction limits arrive in stage 2.

Resource providers normalize desired declarations, describe dependencies, resolve names, read current
managed configuration, project desired configuration and apply operations. The generic planner compares
normalized projections and returns create, update, no-op, blocked, adoption, replacement or removal.
Stage 1 planners emit create/update/no-op, adoption/replacement requirements and blockers; they do not
execute adoption/replacement/removal. Read projections contain only managed fields. Runtime observations,
revision timestamps, credentials and Secret values must never enter a projection or diff.

Projection references use resolved internal IDs inside the server boundary, so a renamed/recreated
reference cannot appear identical just because a name matches. Current reads return a version vector
covering all documents that contributed managed state. A plan is advisory until its source, ownership,
resource versions and references are revalidated in the mutation transaction. Stage 1 does not switch
the existing apply pipeline to a new execution engine. Drift reconciliation is stage 4.

## YAML identity and coverage

Envelope: `apiVersion: pole.io/v1alpha1`, `kind`, `metadata.name`, `spec`. Unknown properties are rejected.
Names are immutable technical names, unique by `(tenant, kind, name)`; group and isolation are admission
constraints, not address qualifiers. References are tenant-local, may cross source ownership, and never
transfer it. Accepted IDs remain pinned; name reuse cannot silently rebind an accepted declaration.

DeploymentTarget is referenced by technical name. ComputeProfile and StorageProfile require explicit
technical name **and revision**. StorageProfile naming/history are stage 5 work. GitHub connections get
an explicit immutable tenant-unique technical name at creation in stage 2; account display names and
Mongo IDs are not YAML connection identities. Generated runtime objects are never declarations.

The table records the current API surface as input to later providers, not a claim that all schemas
are implemented. Absence of an update API requires an explicit future lifecycle decision; it does
not establish a permanent domain prohibition. New provider schemas are implemented in stages 5–6.

| Kind / owner | Managed fields from current API | References / immutable boundaries | Excluded / phases |
| --- | --- | --- | --- |
| ResourceGroup / ResourceGroups | Description, Tags | Name is identity | Default scope, registrations, lifecycle; Prepare |
| IsolationScope / ResourceGroups | Name, parent group | Group reference; default scope reserved; current names are group-local and need tenant-wide declaration identity | Runtime cells, lifecycle; Prepare after group |
| WebService / WebServices | Image server/name/tag, env, ports, secret/connection refs, probes, autoscaling, tags, exposure, target replicas | Group/scope, target, pinned compute, secrets/connections; placement identity changes need explicit handling | Registry credentials, generated hostnames, releases, observations; Prepare → Bind → Configure/activate |
| PostgresCluster / Postgres | Version, Instances, Storage, Username; update supports Instances/Storage/compute | Group, placement, pinned storage/compute; creation Password must become a separate secret-reference provisioning design | Password/ciphertext, runtime; Prepare → Bind credentials → Configure/activate |
| RedisService / RedisServices | Storage, Tags, compute, placement | Group/scope, pinned profiles; storage profile changes and any size changes are currently rejected | Resolved storage/runtime; Prepare → Bind → Configure/activate |
| FunctionApp / Functions | FunctionRoute, image, env/secret refs, resources, scaling, handlers and triggers, tags | Group, compute, secrets; route/group creation-only; current API has no target/scope selector | Protocol/runtime data, invocations, generated trigger IDs; Prepare → Bind → Configure/activate |
| ResourceConnection / ResourceConnections | Name, source, destination, EndpointId | Typed named resource references; endpoint is provider-owned selector; no general update API | Resolved network snapshots, fences, cleanup; Bind after both placements |
| DomainZone / Domains | DeploymentTarget, Label | Managed label reservation under target base domain; target/label creation-only; separate declaration identity | Derived Domain, generated zones; Prepare |
| HostnameBinding / Domains | Zone, RelativeName, workload placement selection | Named zone/workload/target; no generated deployment IDs; no update API | Computed hostname and generated bindings; Bind |
| Secret / Secrets | ResourceGroup, Purpose, Keys | Group immutable in current provider | Values/ciphertext, encryption metadata, value versions, bindings; Prepare |
| SecretBinding / Secrets | Secret, WebService, KeyMappings | Secret/consumer immutable; current slice all deployments/WebService only | Required/retained rollout pins and projections; Bind |
| SourceRepository / GitProviders | Connection, external repository, Branch; current link also requires CandidateKey | Connection technical name; general repository access must be separated from build analysis | URL/privacy/analysis observations; Prepare |
| SourceBuildConfiguration / SourceBuilds | Recipe candidate/kind/root/manifest/commands/start command/Dockerfile | Repository + WebService creation-only; candidate changes currently rejected | IsCustomized is server-set; jobs/logs/images/credentials excluded; Configure |

Canonical evidence is in each owning feature's create/update endpoint and Domain. In particular:
`WebServices.Application/Common/WebServiceDto.cs`, `Postgres.Application/Endpoints/UpdatePostgresCluster.fs`,
`RedisServices.Application/Endpoints/UpdateRedisService.cs`, `Functions.Application/Endpoints/CreateFunctionApp.cs`,
`Domains.Application/Endpoints/CreateDomainZone.cs`, `GitProviders.Application/Endpoints/Repositories/LinkProviderRepository.cs`.
Build output may publish an image but cannot mutate Git-owned desired image configuration.

## Target source API

All routes are under the existing authenticated tenant-context `/api/v1/gitops/sources` root.
Use normal tenant read/write authorization; input cannot elevate authority. Repository permissions
are checked through the provider. Optimistic conflicts use typed outcomes mapped to HTTP 409.

| Operation | Target route / inputs | Result |
| --- | --- | --- |
| Create | POST root; idempotency key, connection/repository IDs, branch, directory, sync/prune policy | Source identity and revision; no YAML files |
| List / detail | GET root / `{id}` | Configuration, sync progress, blockers, inventory summary |
| Edit | PUT `{id}`; expected revision, configuration; relocation additionally requires confirmed preview | Updated revision or stale/conflicting preview |
| Sync / retry | POST `{id}/sync` / `{id}/retry`; expected revision | Durable requested generation, not synchronous apply success |
| Pause / resume | POST `{id}/pause` / `{id}/resume`; expected revision | Revised activity state; pause fences in-flight work |
| Preview | POST `{id}/previews`; intended adoption/removal/replacement/relocation/detach | Server-owned reviewed operation set and preconditions |
| Confirm | POST `{id}/previews/{previewId}/confirm`; expected preview revision | Confirmation of that exact preview, not client-selected actions |
| Detach | POST `{id}/detach`; expected revision and confirmation reference | Retained resources returned to manual control |
| Delete | DELETE `{id}`; expected revision | Deleted only after ownership is empty |
| Runs / operations | GET `{id}/runs`, `{id}/runs/{runId}`, `{id}/runs/{runId}/operations` | Tenant-scoped progress and sanitized results |

These are target contracts, not newly exposed stage-1 routes. Existing manual source/submit/resume
endpoints remain the temporary testable input to the same compiler/executor. Remove them once the
repository flow replaces them. No public YAML apply API or compatibility API ships in the final PR.

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

Unit/BSON tests prove current schema and transitions, lease fencing, projection normalization and
preview invalidation. Mongo tests prove all four provider projections plus the unchanged snapshot
transactions, retry/fencing and secret-value recovery. Architecture tests enforce Domain ownership,
provider boundaries, records, explicit constructors and contract parameter limits. No legacy documents exist.
Stage 1 runs the solution build, full GitOps unit and architecture suites and full Pole.Tests.Integration.
The final single PR still requires a fresh unfiltered publication quality gate and independent review.
