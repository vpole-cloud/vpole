# Pole metadata-feature starter

Use this starter for a new C# tenant-owned metadata feature with Mongo persistence and outbox
notifications. It is a complete small example, not a universal workload implementation. Existing
features should usually gain a slice in place rather than another generated module.

The canonical [engineering rules](../../docs/engineering-guidelines.md),
[module boundaries](../../docs/module-boundaries.md) and [review checklist](../../docs/review-checklist.md)
remain authoritative. Advanced patterns link to maintained production code in
[agent delivery](../../docs/agent-delivery.md#task-brief-and-canonical-implementations).

## Generate a feature

After scope and branch authorization, run from the repository root:

```bash
python3 scripts/create_feature.py --feature Bookmarks --resource Bookmark --route bookmarks
```

This creates `src/features/Bookmarks`. The command validates names and the route, replaces project
paths/namespaces/event identities, and refuses to overwrite an existing directory. It does not
edit the solution, architecture classification, production composition, Git or external systems.

The initial migration ID combines this starter's frozen schema-release timestamp with a
feature-specific suffix. It sorts before later migration IDs rather than treating an arbitrary
hash as an ObjectId timestamp. Once generated, keep the literal ID frozen with the migration.

```text
FeatureName.Domain/             Resource, details/audit submodels, factory and transitions
FeatureName.Application/        Create/Get/Update endpoints and their colocated handlers/outcomes
  Common/                      Shared HTTP DTOs and validation
  Creation/                    Transactional creation capability
  Reads/                       Tenant-scoped load capability
  Updates/                     Revision-checked update capability
  Mongo/                       Collection/index names and one feature filter catalog
FeatureName.Migrations/         Frozen BSON validator and unique tenant/name index
FeatureName.Events/             Created/updated events with required tenant EventCore
FeatureName.Tests/              Domain, BSON and event serialization tests
FeatureName.Tests.Integration/  Real HTTP/Mediator, replica-set and outbox tests
```

The source files here end in `.tpl`. They are not active solution projects or production feature
registrations. Only the materialized result is compiled and tested. No architecture allowlist or
production role is weakened for a template.

## Complete explicit composition

1. Add the generated projects to the solution:

    ```bash
    dotnet sln Pole.slnx add src/features/Bookmarks/*/*.csproj
    ```

2. Classify the feature explicitly in `ModuleBoundaryArchitectureTests.FeatureRoles`. Choose its
   real platform, built-in or service-provider role; this example is plain platform metadata.
3. Add Application and Migrations project references at the appropriate composition root. Register
   `AddBookmarksFeature()`, include its Application assembly in Mediator/validator discovery and
   its Events assembly in the event catalog. Map `MapBookmarksEndpoints()` beneath the existing
   authorized `/api/v1` tenant-aware group.
4. Register the independent migration assembly explicitly using the existing host migration
   mechanism. Keep its generated ID, collection, fields and index definitions frozen after adoption.
   Do not import runtime constants into a migration. Review startup registration with the real host.
5. Replace the example's business rules and payloads, review line wrapping after renaming and keep
   the relevant tests. Update the generated feature README with its actual scope.

See `Pole.Server/Extensions` and `Pole.Platform.Hosting` for current composition, and follow the
[feature-slice skill](../../.codex/skills/pole-add-feature-slice/SKILL.md). The generator deliberately
leaves these decisions visible for review.

## Behavior represented by the example

- **Create:** normalize name/description through Domain, generate the ID server-side, set revision
  and UTC audit values in the factory, then commit root and event in one transaction.
- **Get:** combine accepted tenant context with resource ID and return not-found for another tenant.
  The Domain root implements dependency-leaf `ITenantOwned`; filters use the shared typed `ForTenant`.
- **Update:** require a positive expected revision, read current state in the transaction, run the
  domain transition and conditionally replace the matching revision. A name conflict or stale
  revision returns its own typed HTTP problem. A normalized no-op changes no timestamps, revision
  or outbox records.

Create accepts a details body:

```json
{ "name": "Documentation", "description": "Internal reference links" }
```

Update accepts the same cohesive details and an explicit precondition:

```json
{
    "expectedRevision": 1,
    "details": {
        "name": "Documentation",
        "description": "Updated reference links"
    }
}
```

Names are case-sensitive, trimmed, 1–80 characters and unique within a tenant. Description is
trimmed and at most 500 characters. The migration independently freezes the corresponding stored
bounds. Responses map to HTTP-owned DTOs; events contain only ID/name/revision and tenant metadata.

The sample is not a Kubernetes resource: it has no placement, quota, runtime observation, GitOps
ownership, deletion lifecycle, search or UI. Add those only for a real requirement using their
owner contracts and focused skills. Do not transplant this update path over such admission rules.
Contracts and Contracts.HttpMapping are optional additions when a synchronous cross-feature API
is actually needed; do not create empty projects preemptively.

## Validate changes to the starter

A Docker-compatible runtime, the pinned SDK and a .NET 10 runtime for CleanupCode are required;
no Kubernetes cluster or registry is needed for this lane:

```bash
python3 scripts/run_script_tests.py
python3 scripts/check_feature_template.py
```

The checker snapshots current repository inputs into an owned `.artifacts/feature-template` run,
materializes a normally named feature, explicitly classifies it and includes its projects in that
snapshot's solution. It restores and builds the whole solution so semantic architecture checks
have every project's evaluated role and reference assemblies. It retains the production analyzers,
restores the pinned tools,
checks generated C# using `Pole Safe Cleanup` and the current EditorConfig, and runs the
full existing architecture suite, generated unit tests and generated Mongo/API suite with coverage
and zero skipped tests. It never changes the working repository's feature registry or solution.
Coverage uses the gate's evaluated product-assembly inventory, excluding test support and vendor
assemblies while retaining migrations and child-process collection.

The integration suite reuses `Pole.Testing.FeatureTestHost` and `MongoReplicaSetFixture`; it does
not copy infrastructure or reference Pole.Server. Its test composition registers ResourceConnections
because the shared platform's deployment guards require that capability. The generated Application
has no dependency on ResourceConnections. Tenant authorization remains real in HTTP tests.

Tests cover concurrent name/revision conflicts, both-tenants-authorized isolation, actual schema
rejection, normalized no-op and rollback after a real outbox insert. Generator tests cover safe
renaming, path validation and refusal to overwrite existing work. Reports are local artifacts;
never treat an interrupted/failed run or a structural generator test as a passing executable sample.

The selected local publication gate and a separate CI job execute the generated-feature checker
when template behavior changes. Proven template formatting uses `--format-only`: generate output
and check formatting without solution build or behavioral suites. README-only changes use the
documentation lane.
This focused lane does not replace the full [publication gate](../../docs/agent-delivery.md).
