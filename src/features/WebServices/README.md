# Web services

The WebServices feature owns the logical desired state, release history, deployment tokens, and
lifecycle of tenant-scoped web services. One WebService can have several physical
`ResourceDeployment` instances, each bound to a deployment target with its own fixed replica
count, resolved compute snapshot, runtime cell, generation, operational status, and optional
hostnames.
MongoDB remains the source of truth, but application consumers do not receive a feature-wide
database context.

CPU, memory, extended resources, and Kubernetes scheduling are resolved from a
[`ComputeProfile`](../ComputeProfiles/README.md) and persisted as an immutable
`ResourceDeployment` snapshot. Create uses the default profile when no ID is supplied. Update
without an ID preserves the snapshot; an explicit ID adopts the latest eligible profile revision.
Rollback preserves the currently deployed compute policy. Quota admission measures resolved
requests across every desired target deployment and its admitted replica count. A target stops
consuming quota when deletion is requested, even while its deployment record remains for cleanup;
pending and failed desired targets still consume quota. See
[tenant quotas](../../../docs/quotas.md#placement-aware-accounting). Generated Kubernetes Deployments
consume only the persisted physical snapshot.

Rollback restores the selected succeeded release's specification while retaining the service's
current exposure mode. If that mode is Public, a historical specification without a numeric TCP
target port is rejected before any deployment, quota, release, or outbox mutation.

Release recovery requires the caller's expected active revision: `POST /{id}/redeploy` carries
`expectedRevision` in the query and `POST /{id}/rollback` carries `targetRevision` and
`expectedRevision` in its JSON body. A changed revision returns conflict before mutation;
transactional service/placement fences still reject changes occurring during recovery. Rollback
accepts only a tenant-owned Succeeded release before the active revision. Every accepted recovery
creates a new revision and release, including rollback to an identical specification.

Redeploy reapplies the current specification without rebuilding source. Rollback restores image
reference, literal configuration, probes, autoscaling and logical connection/Secret references;
current dependency authority is revalidated. Current tags, desired targets, fixed replicas,
compute, runtime cells, exposure and hostname snapshots are retained. It restores neither database
contents nor Secret versions/values. Image digest is recorded provenance; rendering still uses
the image tag, which can point to different bytes later. Source-build provenance exposes job and
repository IDs for navigation; recovery trace/correlation IDs come from its actual update event.

The resource workspace's Releases section pages bounded history, distinguishes Pending/Succeeded/
Failed/PartiallyFailed/Superseded, and shows actor/time, image and configuration/reference counts.
It hides values and native failure messages. Retryable target failures keep a release Pending;
Operations exposes their current exact state. Viewer can read history, while recovery requires
a writer, explicit confirmation and a fresh revision. Polling and reconnect refresh persisted
history; read failures retain the loaded page as stale, scope changes cancel and clear it, and
conflict keeps the selected context until an explicit refresh and new confirmation.

Shared desired-state changes advance one logical resource revision and transactionally fan out to
the desired deployment set. Update, rollback, redeploy, image-tag and source-build releases exclude
targets already being removed; inventory and finalization still retain those targets for cleanup.
Configuration comparisons use values across serialization boundaries, including literal and logical
environment references. Tags-only updates advance the logical revision without restarting physical
reconciliation or replacing its current claim. Identical configuration, tags and exposure are a no-op
and create neither a revision nor a release; redeploy remains the explicit request to reapply them.
Reconciliation then proceeds independently per physical generation.
Release entries identify the exact deployment, resource revision, generation, and operation they
track, allowing an aggregate release to report success, failure, or partial failure without a
completion from one target masking the state of another.

Web Services default to `Private`: creation does not reserve a public hostname or create an Ingress,
and no public endpoint is advertised. `Public` is an explicit exposure choice that requires a
configured ingress source and public ingress settings on every selected target. Public hostname
ownership belongs to the Domains feature. Each Public physical deployment receives one immutable
generated hostname under its target's public base domain and may have additional aliases under
tenant-owned zones. Its resolved ingress class, issuer, base domain, and complete hostname set are
snapshotted in `ResourceDeployment`; Kubernetes rendering never rebuilds them from mutable target
or domain state. Releases snapshot the logical exposure mode alongside the spec and deployment
generations, so later completions cannot reinterpret an older Public release as Private or vice
versa.

Changing Public to Private clears each deployment's ingress snapshot and reconciles removal of
its Ingress and public traffic allowances. The generated hostname binding is released only after
that deployment's matching Private apply succeeds; a failed or delayed apply keeps the binding
reserved. Changing back to Public reserves the generated hostname before admitting and applying
the Public deployment. [Workload placement](../../../docs/workload-placement.md) describes the
RuntimeCell ingress baseline and the target-specific source rules.

Public ingress reconciliation checks the desired deployment generation again after Kubernetes
accepts a Create or Replace. A superseded write is conditionally deleted using only the UID and
resource version returned by that write, then rejected; a missing ingress or a newer mutation
cannot be removed by the stale attempt. If Kubernetes omits that identity, reconciliation rejects
the stale attempt without issuing an unfenced delete.

## Application dependencies

The WebService Dependencies tab exposes three independent persisted steps: a directed network
connection, an optional Secret binding, and explicitly chosen application environment references.
Reload reconstructs completed steps. Retrying one step does not undo the others or delete a shared
database or Secret. Network policy `Applied` and Secret projection `Ready` acknowledge their own
infrastructure operations; application SQL or Redis operations are still needed to prove connectivity.

`Spec.Config.ConnectionEnvironmentReferences` maps application environment names to a logical
connection ID and `Host`, `Port`, or a published `Configuration` key. ResourceConnections checks
the exact tenant, consumer, desired target coverage and endpoint under the caller's transaction.
Runtime resolution uses the exact physical consumer deployment and its current connection snapshot;
each target receives its own service DNS and service port. Native service names are not user input.

`Spec.Config.SecretEnvironmentReferences` maps environment names to logical Secret IDs and source
keys. Secrets owns binding authorization, same-group/scope and deployment coverage checks, and native
projection resolution. WebServices receives generated Secret names and mapped keys, never decrypted
values, and renders non-optional Kubernetes `secretKeyRef` environment entries. Required keys are
pinned on the consumer binding without advancing the encrypted version or projection revision.
Rotation must retain those keys; removing references releases the pins. Rotation alone does not
restart an application or replace values already loaded into a running process.

Dependency configuration updates can send `ExpectedRevision`; a stale revision returns a conflict
without writes. References are retained in events and release specifications and through source-build
image updates. Apply mutations revalidate current authority; deployment cleanup may retain unchanged
references after independent revocation. Removing network access or a binding does not rewrite saved
configuration, and later applies fail with a typed dependency error until unavailable references are
removed or authorization is restored.

The additive `WebServiceDependencyReferencesSchemaMigration` accepts legacy documents without the
maps and validates new logical reference shapes. Domain reads normalize absent maps to empty maps.

## Persistence boundary

`WebServices.Application` exposes named delegates from its configuration, query, lifecycle,
deployment, release and token workflows for handlers, consumers, SourceBuild integration, and
Kubernetes reconciliation. Every delegate is
implemented by one internal Mongo operation class in the same file. `AddWebServicePersistence`
binds those implementations to their method groups and registers the typed collections.

The shared `Mongo/WebServicesFilters.cs` catalog centralizes tenant ownership, resource identity,
optimistic revision, lifecycle, release and deployment-token predicates. The token-hash lookup is the sole intentional unscoped read: the unguessable bearer token
establishes tenant context, and every subsequent operation is tenant-fenced.

Handlers must not inject `IWebServiceDbContext`, `IMongoDatabase`, or Mongo collections. They
receive semantic capabilities such as `LoadWebServiceById`, `PersistUpdatedWebService`,
`BeginWebServiceDeletion`, and `CompleteWebServiceDeployment`. Cross-feature callers receive only
the capability they require; for example SourceBuild configuration uses `WebServiceExists`.

Create/update operations join caller-owned quota/deployment transactions through an active Mongo
session. An unconditional aggregate mutation and its outbox event are one persistence operation.
Release insertion supersedes older pending generations in that same transaction. Self-contained
completion and deletion-finalization operations own their transaction and keep resource, release,
deployment cleanup, and outbox changes atomic. Revision and duplicate-key conflicts are typed
expected outcome errors; Mongo availability and driver failures continue as exceptions.

Transaction-owning flows use closed `RunMongoOutcomeTransaction<TOutcome>` delegates and return
an explicit commit or abort decision. The WebServices registration binds the closed outcome types
it owns; no generic legacy transaction delegate is registered.

Resource-deployment reads and mutations use provider-owned contracts from ResourceDeployments.
Exact and set capabilities preserve the `TenantId + ResourceType + ResourceId` ownership fence,
optimistic physical revision, compute/target fences, and caller-owned transaction without exposing
ResourceDeployments Domain or Application types.

Production code has no general `MongoDbContext`. Raw WebService Mongo access is limited to the
feature persistence directory, historical migrations, and explicit integration-test helpers.

## Mongo document validation

`WebServiceSchemaValidationMigration` is a self-contained historical artifact. It embeds the
`WebServices` collection name, BSON field names, nested shapes, numeric types, nullable branches,
and lifecycle discriminators without referencing current domain or persistence types. It creates
the validated collection when absent and otherwise applies the same validator with `collMod`.

Validation uses level `strict` and action `error`. The schema requires the complete currently
serialized aggregate, including tenant scope,
specification, isolation scope, exposure policy, active revision, tags, events, persistence
revision, and lifecycle.
Active and deleting lifecycle objects are closed because their discriminator-specific shape is a
state invariant; ordinary nested objects remain open so additive fields can roll out safely.

`MongoDocumentContractTester<WebService>` verifies the independent schema against real driver
serialization. Tests cover collection creation, `collMod`, strict/error settings, nullable and
populated optional shapes, both lifecycle variants, missing tenant scope, and invalid lifecycle
documents.

`WebServiceReleaseSchemaValidationMigration` independently validates the multi-target release
shape, including polymorphic provenance and exact deployment entries. Deployment tokens retain
their own migration and persistence rules.

## Audit commands

```bash
rg -n "IWebServiceDbContext|IMongoCollection<WebService>|Builders<WebService>" \
  src/features/WebServices src/features/SourceBuilds src/api/Pole.Server
rg -n "IWebServiceDbContext|MongoDbContext|StoreEventAsync" src tests
```

Related rules:

- [Engineering guidelines](../../../docs/engineering-guidelines.md)
- [Multitenancy](../../../docs/multitenancy.md)
- [Observability and outbox propagation](../../../docs/observability.md)
- [Testing](../../../docs/testing.md)

## Autoscaling runtime

Autoscaling uses an owned `autoscaling/v2` HorizontalPodAutoscaler with the declared CPU or memory
resource utilization target. Utilization is relative to the resource request and can exceed 100%.
The first Deployment starts at `minReplicas`; later reconciliation preserves the HPA-managed live
replica count. Switching to fixed replicas removes the owned HPA with UID/resource-version
preconditions before setting the requested replica count. Workload cleanup removes the HPA too.
Prepared services do not create native resources.

The target credential needs `get`, `list`, `watch`, `create`, `update`, `patch`, and `delete` on
`autoscaling/horizontalpodautoscalers` in managed namespaces. Pole does not install a workload
Role/ClusterRole or binding; operators grant these permissions to the supplied target identity.
HPA resource metrics also require the target cluster's resource metrics API.

Observation checks HPA identity and declared policy and watches its changes. A missing or drifted
HPA is not reported as healthy merely because Deployment replicas are ready. Existing periodic
ResourceDeployment verification reapplies the policy, including recreating a missing owned HPA;
live replica counts remain runtime observations and never become desired GitOps configuration.

## Tests

See [test boundaries](docs/test-boundaries.md) for the feature-owned suites, platform testing
helpers, and the guarantees exercised by the shared integration scenarios.
