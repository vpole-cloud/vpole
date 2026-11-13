# Tenant Secrets

## Control-plane workspace

The Secrets workspace provides cursor-paged metadata, write-only creation and complete-map
rotation, resource bindings and per-deployment projection state. Values stay in the active
form's memory and are cleared on window blur/visibility changes, section changes, tab close,
tenant/role changes and successful writes. They are never stored in workspace preferences.
After a version conflict, refresh and explicitly review the current version before resubmitting.
The API client displays fixed problem-code messages without echoing arbitrary response bodies.

Binding choices use the group's generic resource inventory and tenant-scoped
`GET /api/v1/resource-deployments/?resourceType=...&resourceId=...`; the authoritative write
still checks tenant/group/scope and live placements transactionally. Provider-managed Secrets
link to their owner, protect its binding and restrict generic rotation while permitting
additional sharing bindings. A successful projection means Kubernetes accepted that version;
it does not configure or restart the consuming application.

Secrets are logical tenant-owned resources with encrypted, immutable value versions and explicit
workload bindings. Multiple workloads can share one Secret within the same tenant, resource group,
and isolation scope. Workload specifications never receive native Kubernetes Secret names.

## Ownership and storage

`Secrets.Domain` owns metadata, binding and version records. `Secrets.Application` owns APIs,
validation, Mongo persistence, indexes, projection reconciliation and cleanup consumers. `Secrets.Events`
publishes only IDs, version/revision numbers and change kinds. `Secrets.Contracts` exposes narrow
managed-owner credential operations and a generated projection reference; there is no HTTP value-reveal API.

The `Secrets` collection contains metadata and bindings; `SecretVersions` contains separate immutable
encrypted value documents. Each JSON key/value map uses the existing envelope encryption service
with authenticated tenant, Secret ID and version context. Temporary serialized plaintext bytes are
cleared after encryption. Previously encrypted versions remain until the Secret is explicitly deleted.
Accepted ObjectId identities and consumer resource types are stored canonically, so casing or
surrounding type whitespace cannot bypass ownership, uniqueness or cleanup. Names are
immutable and case-insensitively unique within a tenant, across all resource groups and scopes.
The resource type is implicit in the Secrets collection, matching the `(tenant, kind, normalized name)`
addressing model. Another resource type may use the same name. Group/scope restrictions still apply
to bindings. Name reuse is allowed only after Secret deletion commits, and creates a new ID with no
inherited bindings or version history. Each secret is also registered as
`pole/secret`, preventing group deletion while secrets remain. A scope-reference guard prevents
additional-scope deletion while a Secret remains, including an unbound Secret.

## API and authorization

All routes are under `/api/v1/secrets`, require authentication and `X-Pole-Tenant-ID`, and use the
standard tenant Mediator authorization pipeline. Viewer can read; Member or higher can mutate.
Platform admins must still select a tenant. Reads expose metadata/key names, never plaintext,
ciphertext, wrapped keys or encrypted version documents.

| Method | Route                        | Behavior                                                                           |
| ------ | ---------------------------- | ---------------------------------------------------------------------------------- |
| POST   | `/`                          | Create from `resourceGroupId`, `isolationScopeId`, `name`, `purpose`, `values`     |
| GET    | `/?after=&pageSize=`         | Metadata page `{ items, nextCursor }`, default 50, maximum 200                     |
| GET    | `/{id}`                      | Metadata with current version and lifecycle                                        |
| POST   | `/{id}/versions`             | Rotate with `expectedVersion` and a complete replacement `values` map              |
| DELETE | `/{id}`                      | Delete metadata and all versions; 409 while bindings remain                        |
| POST   | `/{id}/bindings`             | Attach one consumer with a deployment selection and optional key mapping           |
| DELETE | `/{id}/bindings/{bindingId}` | Idempotently detach exactly one binding                                            |
| GET    | `/{id}/usage`                | Bindings plus currently affected deployment IDs                                    |
| GET    | `/{id}/projections`          | Per-deployment desired/applied version, health, last attempt and safe failure code |

Create returns metadata (201), other mutations return metadata (200), and deletion returns 204. Missing Secret IDs return
404; stale rotation, duplicate names/consumer bindings, used-key removal and bound deletion return
409; unavailable or foreign references return 422. Values are limited to 64 keys and 64 KiB total
UTF-8 value bytes; keys use ASCII letters/digits, `_`, `-`, `.` and at most 128 bytes. Secrets support
up to 256 consumer bindings, and a selection supports up to 256 unique deployment IDs.

A binding body contains `consumerType` (for example `pole/web-service`), `consumerId`,
`allDeployments`, `deploymentIds`, and `keyMappings`. An empty mapping selects the current key set;
a mapping selects source key names and their consumer-facing names. With `allDeployments: true`,
`deploymentIds` must be empty and later same-scope deployments are included dynamically. Otherwise,
the nonempty explicit ID list remains fixed. Read usage to obtain binding IDs for detach. A consumer
has at most one binding per Secret; detach and reattach to change its selection.

## Application and persistence boundaries

Each endpoint owns its request, validator, closed outcome and handler. Handlers depend on named
persistence capabilities (`LoadSecretById`, `LoadSecretPage`, `PersistCreatedSecret`,
`PersistRotatedSecret`, `PersistSecretBinding`, `RemoveSecretBinding`, `DeleteStoredSecret`).
Each capability has one internal Mongo implementation and returns domain values or operation-specific
failures. `RemoveConsumerSecretBindings` owns retry-safe cleanup. HTTP metadata is mapped at the
endpoint boundary; persisted domain bindings are never serialized directly as API responses.

`SecretFactory` owns normalization and initial lifecycle/version/revision. Aggregate key sets,
bindings and mappings are immutable. Deployment selection has separate `AllSecretDeployments` and
nonempty `SelectedSecretDeployments` variants. HTTP accepts the existing selection fields and maps
them into those valid domain states after request validation.

Expected conflicts have distinct problem codes: `secrets.name-already-exists`,
`secrets.version-conflict`, `secrets.mapped-keys-required`, `secrets.binding-limit-reached`,
`secrets.consumer-already-bound`, `secrets.unknown-keys`, and `secrets.bindings-remain`.
Clients can identify the correction needed without parsing a generic conflict message.

## Consumer capabilities

Resource owners contribute `SecretDeclarationConsumer` through Secrets.Contracts. The capability
declares binding usage, projection policy, placement requirements and whether the control plane may
read explicitly granted keys. Secrets Domain does not select behavior by a provider's resource kind.
Bootstrap grants authorize one-time credential preparation and do not grant runtime projection.

`GrantConsumerSecretAccess` joins the consumer's transaction, fences its registration and validates
the tenant/group/scope and selected keys. `ReadConsumerSecretValues` requires that purpose-specific
grant and returns only the requested current values, with a redacted string representation. Ordinary
runtime consumers have projection-only access. Protocol-specific interpretation, such as pairing S3
access-key fields, belongs to the consumer feature. Secret values never enter events or metadata DTOs.

## Mutation and lifecycle flows

1. Create validates the active group/scope through ResourceGroups' provider-owned contract, acquires
   its transaction write fence, and registers the Secret. Metadata, encrypted version and outbox
   event commit together. Any failure rolls all of them back.
2. Ordinary runtime binding validates the registered consumer and its live placements in the same
   tenant/group/scope. Group and deployment write fences serialize admission with unregistration,
   scope deletion and deployment deletion. A provider may explicitly contribute a purpose-bound
   consumer capability that permits grants before its first placement, as Backups does for retained
   archive credentials. GitOps planning uses that capability rather than assuming every consumer has
   a workload. Provider-managed creation joins the caller transaction after registration and pending
   placement are staged, so it needs no running workload.
3. Rotation checks the expected current version, preserves explicitly mapped keys, and increments
   the version. An aggregate write conflict serializes rotation with binding and deletion. Every
   version has its own encrypted envelope; a rotation never overwrites an earlier version.
4. Delete requires no bindings, unregisters the Secret, removes its versions and metadata, and
   publishes a metadata-only event in one transaction. Detach bindings explicitly first.
5. ResourceGroups publishes `resource-unregistered` atomically during a consumer's final
   unregistration. The Secrets consumer removes only that tenant/group/type/ID's bindings, retaining
   the Secret, its versions and all other consumers. Duplicate delivery is harmless. This is
   asynchronous: until cleanup is delivered, deletion remains blocked; explicit detach is available.

Usage resolves deployments through the tenant-aware ResourceDeployments contract and restricts
results to the Secret's scope and selection. Scope/group identities on deployments are immutable;
usage is a current inventory, not a transactionally frozen deployment plan.

## Events, operations and limits

`pole.secrets/v1/secret-changed` carries Secret ID, version, revision and a typed change variant
(`created`, `metadata-updated`, `rotated`, `deleted`, `binding-added`, `binding-removed`), plus tenant/trace context.
Only binding variants carry a required binding ID. The event includes the display name for resource inventory updates; it contains no values, key names or purposes. Secrets have no
search projection; metadata APIs and group inventory provide discovery. No payload is logged by the
feature, and write-body diagnostic strings are redacted.

Configure and back up the shared encryption key ring before use; see
[envelope encryption operations](../../../docs/envelope-encryption.md). This feature has no KEK
rewrap endpoint yet: retain old KEKs for retained immutable versions and backups. Value rotation
creates a new encrypted version but does not eliminate older-key references in history.

Ordinary reveal APIs, cross-scope sharing and platform credentials remain outside this
feature. A successful logical rotation creates desired runtime state; it does not itself prove that
an application has consumed a new credential or restart workloads.

The provider-owned `AdmitConsumerSecretReferences` and `EnsureConsumerSecretProjections` contracts
let WebServices configure explicit logical Secret/key references. Admission requires a current
binding, the same tenant/group/scope, every desired consumer deployment and authorized source keys.
Consumer bindings pin current `Pins.Required`. Declarative reference changes move retired keys to durable
`Pins.Retained`; rotation, metadata edits and alias changes protect both sets. The minute-based recovery
job releases retained pins only after the consumer-owned observer confirms complete current native
rollouts on every placement, with no old or terminating pods or truncated observations. The Mongo
release transaction fences the Secret revision/reference fence and the complete physical deployment
set, revisions and generations. Missing, failed or superseded proof retains pins for retry. These admission changes do not create encrypted versions or
advance projection revisions. Runtime resolution checks exact consumer placement and current
projection authority and returns generated native names and mapped keys, never plaintext values.

## Kubernetes projection flow

Each authorized `(Secret, ResourceDeployment)` has one deterministic native name and durable
`SecretProjections` row. Separate consumers of the same Secret get independent objects, even in the
same namespace. The exact tenant/type/resource/deployment contract resolves the persisted RuntimeCell,
target and namespace; no ambient/default client or caller-supplied native name selects placement.
Bindings select deployments and map permitted keys. Native labels and annotations contain identities
and desired version/revision only, never values. The protected managed credential binding can request
`kubernetes.io/basic-auth`; other shared bindings remain `Opaque` so key remapping is supported.

Reference-only Secret events trigger reconciliation. A minute-based recovery job discovers new dynamic
placements, retries failures and repairs deleted objects or ordinary data/annotation drift. Discovery
uses bounded pages. Every projection has its own lease, storage revision, desired/applied version,
last attempt and explicit state. A failed target retains its previous applied version and cannot roll
back another target. The target adapter returns `SecretProjectionTargetOutcome` for expected availability,
ownership, concurrency and SDK failures; reconciliation stores explicit safe failure codes. Database
and unexpected programming failures, as well as caller cancellation, propagate. SDK request/response exceptions are discarded without
retaining an inner exception because they may contain plaintext HTTP bodies.

The writer resolves the target, reads current metadata and rechecks authoritative bindings/revision
before decrypting immediately for the target write. Temporary UTF-8 plaintext and manifest byte buffers
are cleared afterward. Replacements use Kubernetes resourceVersion; deletion uses UID and resourceVersion
preconditions. A stale claim cannot complete another worker's status or overwrite a newer Mongo revision.
Authority is read from Pole, not trusted from tamperable version annotations. Ownership-label conflicts
are reported rather than overwriting an unrelated native object.

Removing a binding, deployment or Secret leaves the persisted projection location available for cleanup.
A stale event always re-reads current authority. Checks after apply remove a projection revoked during
the write. A Mongo transaction cannot atomically revoke an in-flight Kubernetes request: revocation and
drift repair converge asynchronously. Deleted rows are retained and audited every 30 minutes after the
source disappears, because a delayed old create might finish after another worker's deletion. Active
sources and unsuccessful cleanup continue on the normal recovery cadence. Tombstones are not discarded
based on an assumed request timeout. UID/resourceVersion checks and current binding checks protect a
new authorized object from an old cleanup worker.

Kubernetes Secret data is plaintext to Kubernetes and authorized pods, despite base64 transport encoding.
Configuring etcd encryption and Kubernetes access controls remains the cluster operator's responsibility.

## Managed credentials and PostgreSQL

PostgreSQL is the first adopter. Its create transaction stages the registered PostgreSQL resource and
ResourceDeployment, then creates the encrypted managed Secret and protected owner binding in that same
session. Admission reads and fences the staged records, avoiding a placement-versus-credential startup
cycle. Secrets is the authoritative encrypted store; workloads consume generated projection references.
The internal read operation selects a credential through its tenant-scoped managed owner, not an
arbitrary Secret ID. PostgreSQL retains the existing role controls on its connection-string operation.

`Secret.Management` is required aggregate state. `SecretFactory.Create` constructs an unmanaged Secret;
`CreateManaged` constructs provider ownership together with its binding. `KubernetesSecretType` describes
the native Secret format: the PostgreSQL owner projection uses `kubernetes.io/basic-auth`, while ordinary
and remapped shared projections use `Opaque`. The provider contract represents this as
`ManagedSecretKubernetesType`.

Metadata exposes `managedBy` with owner type, resource ID and protected binding ID. Generic rotation and
detaching that binding return `secrets.managed-by-provider`; use the owner operation. Same-scope sharing
through additional bindings is allowed, and those bindings may be detached normally. Provider deletion
removes its binding atomically. If other consumers remain, the Secret and encrypted versions are retained
and management is cleared: it becomes an ordinary tenant Secret with the same immutable identity/name.
If no consumers remain, the transaction unregisters and deletes the Secret and versions. Secret writes
serialize deletion with concurrent sharing, so a committed binding cannot be orphaned. The generic
resource-unregistered cleanup also clears management when it removes an owner binding, retaining the
ordinary Secret rather than leaving an uneditable credential if an owner uses that fallback path.

Projection readiness means that Kubernetes accepted the desired Secret version. It does not mean a
running database has adopted that password. The owning PostgreSQL workflow rotates its managed
administrator Secret through CloudNativePG; generic Secret writes cannot bypass the owner workflow.

## Verification

`Secrets.Tests` covers binding/rotation invariants, safe representations, generated manifests, target
SDK fencing/redaction, reconciliation failure isolation and request validation.
`Pole.Tests.Integration/Secrets` covers real Mongo transactions, encrypted immutable storage, tenant
and role authorization, write-only HTTP responses, pagination, same-scope ownership, shared cleanup,
retained-secret deletion guards, transaction rollback, competing mutations, managed-owner lifecycle,
projection claim fencing, status authorization and dynamic selections.
Resource registration, isolation-scope and deployment schema suites cover the modified providers.
