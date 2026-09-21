# Tenant Secrets

Secrets are logical tenant-owned resources with encrypted, immutable value versions and explicit
workload bindings. Multiple workloads can share one Secret within the same tenant, resource group,
and isolation scope. Workload specifications never receive native Kubernetes Secret names.

## Ownership and storage

`Secrets.Domain` owns metadata, binding and version records. `Secrets.Application` owns APIs,
validation, Mongo persistence, indexes and cleanup consumers. `Secrets.Events` publishes only IDs,
version/revision numbers and change kinds. There is no value-reveal or runtime decryption contract.

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

| Method | Route | Behavior |
| --- | --- | --- |
| POST | `/` | Create from `resourceGroupId`, `isolationScopeId`, `name`, `purpose`, `values` |
| GET | `/?after=&pageSize=` | Metadata page `{ items, nextCursor }`, default 50, maximum 200 |
| GET | `/{id}` | Metadata with current version and lifecycle |
| POST | `/{id}/versions` | Rotate with `expectedVersion` and a complete replacement `values` map |
| DELETE | `/{id}` | Delete metadata and all versions; 409 while bindings remain |
| POST | `/{id}/bindings` | Attach one consumer with a deployment selection and optional key mapping |
| DELETE | `/{id}/bindings/{bindingId}` | Idempotently detach exactly one binding |
| GET | `/{id}/usage` | Bindings plus currently affected deployment IDs |

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

## Mutation and lifecycle flows

1. Create validates the active group/scope through ResourceGroups' provider-owned contract, acquires
   its transaction write fence, and registers the Secret. Metadata, encrypted version and outbox
   event commit together. Any failure rolls all of them back.
2. Bind validates the registered consumer in the same group and tenant, then validates live
   deployments belonging to that consumer and scope. Group and deployment write fences serialize
   admission with unregistration, scope deletion and deployment deletion. A consumer without any
   live deployment is rejected until its placement exists.
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
(`created`, `rotated`, `deleted`, `binding-added`, `binding-removed`), plus tenant/trace context.
Only binding variants carry a required binding ID. It contains no values, key names, names or purposes. Secrets have no
search projection; metadata APIs and group inventory provide discovery. No payload is logged by the
feature, and write-body diagnostic strings are redacted.

Configure and back up the shared encryption key ring before use; see
[envelope encryption operations](../../../docs/envelope-encryption.md). This feature has no KEK
rewrap endpoint yet: retain old KEKs for retained immutable versions and backups. Value rotation
creates a new encrypted version but does not eliminate older-key references in history.

Kubernetes projection, workload injection, ordinary reveal APIs, cross-scope sharing, frontend UI
and platform credentials are outside this slice. Successful rotation changes the logical Secret;
it does not restart workloads or materialize Kubernetes objects yet.

## Verification

`Secrets.Tests` covers binding/rotation invariants, safe representations and request validation.
`Pole.Tests.Integration/Secrets` covers real Mongo transactions, encrypted immutable storage, tenant
and role authorization, write-only HTTP responses, pagination, same-scope ownership, shared cleanup,
retained-secret deletion guards, transaction rollback, competing mutations and dynamic selections.
Resource registration, isolation-scope and deployment schema suites cover the modified providers.
