# Web services

The WebServices feature owns the desired state, release history, deployment tokens, and lifecycle
of tenant-scoped web services. MongoDB remains the source of truth, but application consumers do
not receive a feature-wide database context.

CPU, memory, extended resources, and Kubernetes scheduling are resolved from a
[`ComputeProfile`](../ComputeProfiles/README.md) and persisted as an immutable
`ResourceDeployment` snapshot. Create uses the default profile when no ID is supplied. Update
without an ID preserves the snapshot; an explicit ID adopts the latest eligible profile revision.
Rollback preserves the currently deployed compute policy. Quota admission measures resolved
requests across the admitted replica count, and generated Deployments consume only the snapshot.

## Persistence boundary

`WebServices.Application/Persistence` exposes named delegates for the exact operations used by
handlers, consumers, SourceBuild integration, and Kubernetes reconciliation. Every delegate is
implemented by one internal Mongo operation class in the same file. `AddWebServicePersistence`
binds those implementations to their method groups and registers the typed collections.

The pure `WebServiceFilters` hierarchy centralizes tenant ownership, resource identity, optimistic
revision, and lifecycle predicates. Release and deployment-token filters are centralized beside
it. The token-hash lookup is the sole intentional unscoped read: the unguessable bearer token
establishes tenant context, and every subsequent operation is tenant-fenced.

Handlers must not inject `IWebServiceDbContext`, `IMongoDatabase`, or Mongo collections. They
receive semantic capabilities such as `LoadWebServiceById`, `PersistUpdatedWebService`,
`BeginWebServiceDeletion`, and `CompleteWebServiceDeployment`. Cross-feature callers receive only
the capability they require; for example SourceBuild configuration uses `WebServiceExists`.

Create/update operations join caller-owned quota/deployment transactions through an active Mongo
session. An unconditional aggregate mutation and its outbox event are one persistence operation.
Release insertion supersedes older pending generations in that same transaction. Self-contained
completion and deletion-finalization operations own their transaction and keep resource, release,
deployment cleanup, and outbox changes atomic. Revision and duplicate-key conflicts are expected
`Result` failures; Mongo availability and driver failures continue as exceptions.

Flows that return a value use `RunMongoTransaction<T>` directly. This preserves the callback's
`Result<T>` without mutable captured response variables. Because the built-in DI container cannot
construct arbitrary closed generic delegates from an open generic factory, the WebServices
registration explicitly binds the three result types it consumes.

Resource-deployment lookups reuse the pure `ResourceDeploymentFilters` hierarchy owned by the
ResourceDeployments feature. Its tenant-visible, logical-resource, resource-set, and revision
filters keep the `TenantId + ResourceType + ResourceId` fence consistent across adopters.

`MongoDbContext` no longer implements a WebServices context or exposes the feature collections.
Raw WebService Mongo access is limited to the feature persistence directory, historical migrations,
and explicit integration-test helpers.

## Mongo document validation

`WebServiceSchemaValidationMigration` is a self-contained historical artifact. It embeds the
`WebServices` collection name, BSON field names, nested shapes, numeric types, nullable branches,
and lifecycle discriminators without referencing current domain or persistence types. It creates
the validated collection when absent and otherwise applies the same validator with `collMod`.

Development has no legacy WebService data, so validation is immediately `strict` with action
`error`. The schema requires the complete currently serialized aggregate, including tenant scope,
specification, DNS identity, active revision, tags, events, persistence revision, and lifecycle.
Active and deleting lifecycle objects are closed because their discriminator-specific shape is a
state invariant; ordinary nested objects remain open so additive fields can roll out safely.

`MongoDocumentContractTester<WebService>` verifies the independent schema against real driver
serialization. Tests cover collection creation, `collMod`, strict/error settings, nullable and
populated optional shapes, both lifecycle variants, missing tenant scope, and invalid lifecycle
documents.

Release and deployment-token collections are deliberately not covered by this first validator.
Their migration history and polymorphic provenance need separate backfill/schema design before
strict validation is introduced.

## Audit commands

```bash
rg -n "IWebServiceDbContext|IMongoCollection<WebService>|Builders<WebService>" \
  src/features/WebServices src/features/SourceBuilds src/api/Pole.Server
rg -n "StoreEventAsync|RunInTransactionAsync" \
  src/features/WebServices/WebServices.Application
```

Related rules:

- [Engineering guidelines](../../../docs/engineering-guidelines.md)
- [Multitenancy](../../../docs/multitenancy.md)
- [Observability and outbox propagation](../../../docs/observability.md)
- [Testing](../../../docs/testing.md)
