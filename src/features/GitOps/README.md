# Manual snapshot API and declarative pipeline (first slice)

This is the first implementation slice for [#88](https://github.com/lsbV/Pole/issues/88)
and [#98](https://github.com/lsbV/Pole/issues/98). It supports ResourceGroup, metadata-only
Secret, WebService identity/placement and final configuration, and explicit SecretBinding.
It does **not** complete the GitOps epic.

## Boundary and execution

`DeclarativePipeline` is the internal application engine behind an authenticated **manual snapshot
API**. A caller can create a source, submit local YAML content, inspect the resulting resource
IDs/blockers, supply values through the existing Secret API/UI, and have the accepted snapshot
resume without another commit. This is an operable manual foundation, not repository-connected
GitOps: Pole does not fetch or verify the submitted repository URL or commit marker.

Every API route uses existing bearer authentication and `X-Pole-Tenant-ID`; Viewer can inspect,
Member/Admin/Owner can write, and existing platform-admin behavior is preserved. Tenant and actor
come from the trusted request context. Bodies cannot provide a mutation context or another tenant.
Source IDs are server-generated and tenant-scoped. Creation is idempotent by a tenant-scoped UUID;
a source URL is immutable, HTTPS, and cannot contain user info, query parameters or a fragment.
No credentials or new permissions are provisioned or retained by this feature.

Repository fetching, GitHub linking, webhooks/polling, source pause/deletion/relocation and the
complete Git source worker remain follow-up work. The engine invokes feature-owned application
operations directly; it does not chain HTTP endpoints or invoke build analysis.

Providers implement the platform-owned `IDeclarativeResourceProvider` contract and register
through DI in their owning Application feature. GitOps imports no concrete provider. Each
provider owns strict schemas, defaults, contextual admission and feature persistence operations;
providers resolve other resources through tenant-aware contracts. A test provider demonstrates
extension without changing the engine. Duplicate provider kinds fail registry construction.

Compilation validates the **entire** snapshot before even accepting source execution state.
Unknown fields/kinds/versions, duplicate addresses/map keys, anchors/tags, unsafe file paths,
malformed YAML, internal scope/key/access contradictions, duplicate binding relationships and operation cycles reject the snapshot.
External input limits are 1 MiB total UTF-8, 256 files/declarations/documents per file and depth 24. Diagnostics
contain file/document/field and a fixed code; parser messages and values are not copied into errors.
Accepted specs are capped at 6 MiB of normalized JSON before source mutation, leaving space below MongoDB's document limit after snapshot encoding. Resume revalidates accepted specs and their operation graph
directly; JSON escaping does not reapply the external text limit.
The current schema accepts lowercase DNS labels beginning with a letter and ending with a letter/digit: at most 20 characters
for ResourceGroup, 30 for WebService and 63 for Secret/SecretBinding. There is no displayName.
Names identify a kind tenant-wide, independently of resource group and source. Internal references
retain immutable IDs. Case-insensitive unique group/service indexes also protect manual creation. If existing data has
colliding tenant-wide names, the index migration fails and requires explicit operator remediation;
it never renames, deletes or adopts either resource automatically.

The operation graph establishes group, Secret metadata and inert WebService placement first,
then binding/access, then the complete configuration and activation in one transaction.
A durable `Prepared` ResourceDeployment cannot be claimed, woken by RuntimeCell readiness,
retried or recovered into a workload. Only explicit complete configuration requests activation.
Runtime-cell namespace preparation may proceed; no incomplete application is launched.

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
blocked. The unreleased checkpoint's ID-based YAML is rejected structurally; a stored incompatible
snapshot stops as `UnsupportedStoredSchema` and needs resubmission with names.

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

## Supported operator workflow

Use the existing authenticated API session and active tenant. No new credential is needed.

| Method and route (under `/api/v1`)  | Input                                                                                                                    | Behavior                                                                                                                                                                                                                                    |
| ----------------------------------- | ------------------------------------------------------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `POST /gitops/sources/`             | `{ "idempotencyKey": "<UUID>", "sourceUrl": "https://github.com/owner/repo" }`                                           | Returns server source `id`, fence 0 and `AwaitingSnapshot`. Reuse the UUID and URL after a lost response.                                                                                                                                   |
| `PUT /gitops/sources/{id}/snapshot` | `{ "expectedFence": 0, "commit": "demo-snapshot-1", "files": [{ "path": "demo.yaml", "content": "<the YAML above>" }] }` | Validates all documents, accepts normalized desired state, then applies phases. Returns 202 with `Blocked` or `ConfigurationApplied`, resource IDs and sanitized blockers.                                                                  |
| `GET /gitops/sources/{id}`          | None                                                                                                                     | Returns fence, run, observed/applied marker, next retry time owned-resource status, and bounded per-operation diagnostics (including conflicting addresses). It does not return accepted config, values, actor details or mutation context. |
| `POST /gitops/sources/{id}/resume`  | `{ "expectedFence": 1 }`                                                                                                 | Re-evaluates the persisted snapshot now, without submitting files or advancing the source fence.                                                                                                                                            |

For the example, first submission creates metadata and an inert service, then reports
`SecretValuesMissing`. Find the Secret ID in source status or the Secrets console. Submit
`POST /secrets/{secretId}/versions` with `{ "expectedVersion": 0, "values": { "API_TOKEN": "" } }`
or enter values in the existing console. The empty string in this example is a legitimate value.
Values belong only in this protected value-supply API, **never** in snapshot YAML.

The durable value event triggers resume. If delivery was missed or the process restarted, the
recovery job retries the saved snapshot; the explicit resume route provides immediate operator
recovery. `GET` eventually reports `ConfigurationApplied` and the unchanged applied marker;
inspect the normal service health/operations views separately for runtime readiness.

For later snapshots, use the latest fence returned by `GET`. An unchanged replay advances source
execution metadata but creates no resource revision, release, or deployment generation. A stale
submission/resume returns 409; inspect status after an uncertain response instead of creating a
new source. Invalid YAML returns 400 with diagnostics and leaves accepted state/resources untouched.
Unknown/foreign source IDs return 404 within the selected tenant. Writer role checks also apply to
resume and creation. Request bodies are capped (2 MiB snapshot JSON, 4 KiB control requests), with
the stricter compiled snapshot limits described above. `commit` is a bounded caller-supplied
revision marker in this manual API; repository/commit authenticity is deferred with Git fetching.

## Secret values and recovery

Secret declarations contain **expected key names only**. `values`, `data` and `stringData` are
unknown fields and rejected. Metadata begins at value version 0 with no encrypted version.
The existing authorized rotation operation accepts `expectedVersion: 0` for first supply; subsequent
rotations retain normal optimistic version checks. For declared metadata, supply a complete map
with exactly the expected keys. An empty string is a supplied value. Encrypted values stay in the
existing protected version store and never enter accepted snapshots or operation diagnostics.
Provider-managed credentials (`managedBy`) are a separate invariant and retain their restrictions.

Missing external prerequisites and missing values return explicit recoverable blockers, including
`SecretValuesMissing`, `SecretBindingUnavailable`, `ResourceGroupUnavailable`, and
`ComputeProfileRevisionUnavailable`. `ResumeAsync(context)` reloads the accepted normalized snapshot
from storage, re-evaluates prerequisites and current configuration, and can proceed after values are
supplied **without a new commit**. A restart can load the persisted run context and call the same
entry point. The manual API additionally persists retry deadlines: a Secret value-change outbox
consumer wakes same-tenant sources referencing that Secret, and a minutely Hangfire recovery job
claims up to 32 due accepted snapshots. Blocked snapshots retry after one minute; interrupted
attempts become recoverable after a five-minute lease. The due claim and per-attempt fence prevent
duplicate delivery, concurrent retry, or late blocked outcomes from overwriting a newer success.
The job reloads the server-owned source and tenant from storage, never from event payload credentials.
This bounded accepted-snapshot recovery does not fetch Git or implement the complete source worker.

Prepared resources retain stable IDs through retry. An unchanged successful replay creates no
additional logical revision, release or deployment generation. Image changes retain the service ID.
A blocked replacement configuration leaves the last working spec, placement and release intact.

## Ownership, transactions and status

A source run advances only from the expected source fence. Tenant/source ObjectIds are canonicalized
before constructing ownership keys. Each resume claims a new attempt generation within that run. The accepted schema version, normalized snapshot and
allocated IDs are persisted before provider work. Each provider transaction conditionally **writes**
the current source/run fence, claims the tenant-wide inventory address, mutates the feature,
records its operation outcome and writes outbox messages together. A newer accepted source run
conflicts with in-flight old transactions; stale contexts cannot finish or mutate newer state.
Provider CAS and registration/placement/compute/Secret reference fences remain in force.
Blocked mutations abort their feature writes before recording only a sanitized blocker in a fresh
fenced transaction. Successful independent operations remain applied; there is no distributed rollback.

The shared WebService update persistence guard covers ordinary edits, placement, redeploy/rollback,
source-build completion and image-update tokens. Delete has its own guarded boundary. ResourceGroup
metadata/deletion and Secret deletion/declared-binding detach are also guarded. Secret value supply
is the intentional authorized exception; supplied values cannot rewrite declared keys. Runtime
observations and finalization remain distinct from desired-configuration edits.
Manual deployment retry/update requests against a Prepared service return the existing
`web-services.update-unavailable` conflict without changing its placement or creating a release.
Activate the complete desired configuration through the source before using runtime operations.

`GitSourceExecutions` records observed/attempted/applied commit separately. `ManagedDeclarations`
records ownership, desired/applied hash, blocker and pending removal; `GitOperationReceipts` records
per-phase outcomes. `ConfigurationApplied` only means all configuration operations succeeded.
Runtime readiness continues through ResourceDeployments and existing health APIs. The resource
console displays ownership, source link and blockers; Git configuration controls are read-only,
while normal manual resources and Secret value entry remain available.

Pruning is unavailable/off. Removed declarations stay in inventory with ownership and
`PendingRemoval: true`; an empty snapshot does not authorize deletion. Adoption, detach, prune,
protected deletion approvals and source relocation remain explicit later stages.

## Compute profile history

ComputeProfiles now retains immutable revision snapshots on create/update and default demotion.
Initial schema/index setup does not backfill catalog data. Unknown historical revisions produce
`ComputeProfileRevisionUnavailable`. Historical configuration must still
pass **current** catalog enabled/type/tenant/target authorization, fenced inside the workload
transaction. Removing a current target mapping blocks new historical adoption to that target.
Existing deployment snapshots survive catalog changes. Git must change the pinned revision to
adopt another snapshot; image-only replay keeps the existing snapshot.

## Verification and remaining stages

`GitOps.Tests` covers strict compilation, normalization, cycle detection and provider extensibility.
`Pole.Tests.Integration/GitOps` exercises the authenticated operator workflow, Secret-event and
missed-event/restart recovery, blocked reads, concurrent attempt/source fences, manual duplicate-name
conflicts, ownership, pinned compute, preflight atomicity and pending removal. Existing Secret,
WebService, catalog, architecture and frontend suites remain regression gates.

Follow-up: full provider matrix from #88; durable source worker/claim/lease lifecycle; immutable
Git snapshot acquisition and archive hardening; webhook/poll/retry/pause; full source lifecycle management and
redacted diffs; explicit adoption/detach/replacement/prune approvals; integrated GitHub/browser and
live infrastructure end-to-end evidence. None of these are claimed by this slice.

## Execution domain and persistence boundaries

`GitOps.Domain` may depend on MongoDB for BSON attributes. It has no Contracts, transport,
or application-persistence dependencies.
It owns execution stamps and accepted resource identity/hash authorization, immutable source origin,
configuration progress, preservation of the last applied commit, and retained declaration ownership.
The application pipeline and provider mutation boundary use these decisions directly. Domain progress
means desired configuration was applied; it does not claim workload runtime readiness.

GitOps Domain owns persisted execution, inventory, receipt, source and accepted-declaration models.
Domain models declare BSON attributes and standard polymorphic discriminators. Application
persistence owns JSON codecs and maps stored state to the execution domain model. Source acceptance,
atomic attempt claims/finalization, ownership fences, receipts,
status reads and provider transaction orchestration have separate implementations. Acceptance stores
stable resource IDs before providers run. Attempt claims retain atomic increments; all provider and
final status writes retain tenant/source/run/fence/attempt predicates. Blocked provider transactions
still abort resource/outbox changes before recording their diagnostic in a fresh fenced transaction.

`SnapshotSchema` owns the manifest version and structural bounds. `SnapshotHttpLimits` owns request
bounds. `SnapshotExecutionPolicy` owns the five-minute recovery lease, one-minute retry and bounded
recovery batch. These operational/transport policies do not belong to Domain. None of these changes
fetches repositories, applies live manifests or backfills names against running systems.

Declaration providers own their typed rejection reasons. The generic execution contract carries
an open `IDeclarationFailure`; GitOps itself recognizes only its fencing and ownership failures.
The ResourceGroup operation has a feature-specific outcome before adapting to that extension
contract. Providers map failures to stable diagnostic codes when writing inventory/receipts;
execution never branches on those codes. Secret admission and manual/build/token conflicts retain
typed reasons through their owning application operations. Existing HTTP problem codes and stored
diagnostic strings remain compatible.

Placement, runtime and private-access contract projections retain typed status/operation enums.
HTTP enum converters preserve the existing string wire values and reject unknown/numeric values;
published event strings retain their existing serialized representation. PlacementSummary uses one
ordinary-property record style to preserve its nonconstant default isolation value.

Technical names use the same lower-case DNS-label rule for declarations and catalog references:
they begin with a letter, end with a letter/digit, and contain only letters, digits or hyphens.
Provider-owned name limits can be shorter than the common 63-character bound.

Secret key changes must preserve keys pinned by the current working configuration. To replace a
key, first declare/grant both keys, supply values, and move the service reference to the new key.
Wait for the replacement rollout to complete successfully on every deployment before removing
the retired key/grant in a later snapshot; source `ConfigurationApplied` does not prove runtime
consumers have stopped using it. Values are never copied or renamed
implicitly. A direct removal of an in-use key blocks with `SecretKeysInUse`; the working deployment
remains intact. Missing historical ComputeProfile revisions likewise block rather than being
reconstructed or silently replaced with the latest revision.

## Repository GitOps target contracts

See [repository contracts](docs/repository-contracts.md) for the stage-1 model, provider boundaries,
13-kind coverage matrix, target source API and confirmation rules. Repository synchronization and
lifecycle commands remain scheduled work; the manual snapshot flow above is the current runtime.
