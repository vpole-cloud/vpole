# Application Boundary Migration Plan

Status: Draft

This document records the agreed plan for migrating Pole feature boundaries to
typed unions, typed results, named capability delegates, feature-owned dependency
injection, and persistence implementations that do not expose MongoDB contexts to
application consumers.

The migration is one umbrella architecture task, executed one feature at a time.
Each feature slice must compile and pass its focused tests before work starts on
the next feature. This is not intended to become one solution-wide commit or one
unreviewable pull request.

## Motivation

Pole currently has several overlapping application-boundary patterns:

- mediator handlers returning shared result and failure types;
- expected business outcomes represented by HTTP-oriented failures;
- cross-feature interfaces whose consumers normally use only one method;
- feature `DbContext` interfaces exposed to handlers and services;
- structural validation repeated below the mediator validation boundary;
- HTTP problem mappings registered in the central server composition root;
- temporary legacy and typed capability APIs existing in parallel.

These patterns make feature contracts less explicit, allow persistence invariants
to be bypassed, and make the central dependency-injection file responsible for
details owned by individual features.

The target architecture makes every boundary state its actual contract:

```text
HTTP endpoint
  -> mediator request
  -> feature-owned outcome union
  -> named cross-feature capabilities
  -> Result<T, TError> for local composition
  -> semantic persistence capabilities
  -> internal MongoDB implementation
```

## Goals

1. Give every mediator request a feature-owned closed outcome union.
2. Use `Result<T, TError>` for local two-track composition without coupling
   handlers to shared HTTP-oriented failures.
3. Make expected cross-feature failures stable provider-owned contract types.
4. Prefer named delegates for independently consumable application capabilities.
5. Retain interfaces only for real polymorphism, strategies, plugin extension
   points, or cohesive stateful protocols.
6. Remove `IMongoDbContext` and every feature-specific descendant from application
   consumers.
7. Keep MongoDB collections, filters, and driver details inside feature persistence
   implementations.
8. Preserve tenant isolation, revision and lifecycle fences, transactions, quota
   consistency, and unconditional outbox writes.
9. Move feature registrations out of the central server DI file.
10. Remove temporary legacy APIs after each feature's consumers have migrated.

## Non-goals

- Introducing a generic repository or a global data-access project.
- Converting genuine runtime strategies or plugin extension points to delegates.
- Converting infrastructure failures into expected application outcomes.
- Redesigning domain models unrelated to the migrated boundary.
- Splitting the integration test project unless compile or execution blast radius
  becomes a demonstrated blocker.
- Implementing dynamic plugin discovery as part of this migration.
- Combining all feature changes into one commit.

## Core Decisions

### 1. Handler outcomes are feature-owned unions

Every mediator request declares the complete application outcome of that request:

```csharp
public union CreateResourceOutcome(
    ResourceCreated,
    CreateResourceError);

public union CreateResourceError(
    ResourceStateError,
    ProviderAdmissionError,
    QuotaExecutionError);
```

Handlers return their outcome union directly. They do not return the legacy
shared `Result<T>` and do not expose generic `Failure` values.

A consumer may include a provider-owned error union in its own error union. It
must not translate every provider error into consumer-local copies unless the
consumer is intentionally changing the semantics.

### 2. `Result<T, TError>` is a composition primitive

`Result<T, TError>` remains useful inside handlers and capabilities where a
success value and one typed error channel need to be composed.

It is not the mandatory outer return type of a mediator handler. A handler's
outer type is its request-specific union.

The closed result implementation must prevent third-party subclasses from adding
unknown states. When the .NET 11 closed-type design is finalized for the project,
the result primitives should use it where it improves compiler enforcement.

### 3. Expected failures and exceptions have different meanings

Expected runtime or business outcomes belong in typed unions. Examples include:

- tenant-scoped resource not found;
- disabled or unavailable provider resource;
- optimistic concurrency conflict;
- changed provider configuration revision;
- quota exceeded;
- semantic policy rejection.

Structural contract violations do not belong in expected unions and must not map
to HTTP 400 from an internal capability. Examples include:

- malformed internal object identifiers;
- `null` where the capability contract requires a value;
- an empty required batch;
- duplicate items where uniqueness is a precondition;
- mixed tenants or logical resources in one atomic request;
- invocation without a required active transaction.

The capability must keep fail-fast guards for these cases and throw an appropriate
exception:

| Condition | Default behavior |
| --- | --- |
| Required argument is `null` | `ArgumentNullException` |
| Argument violates a documented structural precondition | `ArgumentException` or `ArgumentOutOfRangeException` |
| Caller violates an operation protocol | `InvalidOperationException` |
| MongoDB, network, serialization, or provider infrastructure fails | Propagate the infrastructure exception |
| Valid request is rejected by current application state | Typed expected error |

A configured batch limit requires explicit classification. If a normal valid user
workflow can exceed it, it is a named expected limit outcome. If it is purely an
internal capability precondition, exceeding it is an argument error.

This work is a concrete implementation of the validation-boundary audit tracked
by [issue #64](https://github.com/lsbV/Pole/issues/64), but the issue remains open
until all production `ValidationFailure` usages have been classified.

### 4. Provider features own cross-feature errors

A cross-feature contract is stable independently of the provider's domain model.
Its request, success snapshots, and error types live in the provider's Contracts
project.

Provider contracts must not expose domain entities or domain-only errors. The
provider maps internal domain outcomes to its stable contract at the provider
boundary.

Consumers compose those contract outcomes into their own request outcomes. A
consumer should only override semantics when it has a real consumer-specific
reason.

### 5. HTTP mapping belongs to the contract owner

The provider supplies default HTTP problem mappings for its contract errors in a
companion `Feature.Contracts.HttpMapping` project when those contracts may reach
HTTP endpoints in multiple consuming features.

Rules:

- mappings use stable problem codes;
- problem type identifiers remain valid for self-hosted Pole installations and do
  not hard-code a documentation host controlled by one deployment;
- extension values are intentionally shaped and never expose provider exceptions,
  credentials, or infrastructure diagnostics;
- mapping switches are exhaustive;
- adding a union leaf requires updating its owner mapping;
- a consuming feature may explicitly override a provider default;
- endpoint OpenAPI declarations match the statuses that can actually be returned.

Feature-local domain or request-state errors may be mapped in the feature's
Application project when they are not shared contracts.

### 6. The mediator pipeline remains separate from handler outcomes

Validation and authorization behaviors operate outside the handler's application
outcome. Pipeline failures must not be added to every request-specific union.

The pipeline envelope must remain extensible so a future plugin behavior can add a
new pipeline failure without changing every handler. The final mediator envelope
name and extension mechanism should be decided in the shared Mediator slice, but
it must preserve this separation:

```text
pipeline failure OR handler-owned outcome
```

### 7. Named delegates are the default capability shape

Use a named delegate when an operation:

- is independently consumed;
- has its own input and output contract;
- can evolve independently from neighboring operations;
- normally has one implementation;
- is injected by consumers that do not need the provider's other operations.

Example:

```csharp
public delegate Task<Result<ResourceSnapshot, LoadResourceError>> LoadResource(
    LoadResourceRequest request,
    CancellationToken cancellationToken = default);
```

Injecting two or three explicit delegates is preferred to injecting a broad
interface and using one method. A handler with an excessive number of delegates
is a signal to review its orchestration responsibilities, not a reason to restore
a generic service interface.

An interface with more than 300 lines or more than approximately five to seven
operations triggers mandatory review. Line count is not an automatic conversion
rule because comments, formatting, and multiple cohesive operations can distort
it.

### 8. Interfaces require a reason

Keep an interface when at least one of the following is true:

- multiple implementations are selected or enumerated at runtime;
- it is a plugin extension point;
- it is a strategy or adapter contract;
- it is used as a generic constraint or descriptor type;
- the object has meaningful shared state or lifetime;
- its methods form one cohesive protocol that consumers use together.

Likely retained examples include resource deployment adapters and polymorphic
quota profiles. `IQuotaAdmissionExecutor` may remain an interface while it owns a
generic transaction protocol, but its legacy overloads must still be removed after
migration.

One-method bootstrapper interfaces do not automatically become delegates. If no
runtime abstraction is needed, depending on the concrete implementation may be
simpler than introducing a delegate.

## Persistence Boundary

### Remove feature DbContext interfaces

`IMongoDbContext` and every feature descendant must disappear from handlers,
consumers, and application orchestration services. Known examples include:

- `IQuotaDbContext`;
- `IResourceDeploymentDbContext`;
- `IDeploymentTargetDbContext`;
- every equivalent context in the remaining features.

This does not mean turning each collection property or CRUD call into a delegate.
The consumers of a DbContext are replaced by intention-revealing capabilities.

Avoid capabilities such as:

```text
GetCollection
FindByFilter
ReplaceDocument
ExecuteQuery
```

Prefer capabilities such as:

```text
LoadWebServiceById
PersistUpdatedWebService
BeginResourceDeletion
ResolveComputeProfiles
AdvanceResourceDeploymentRevision
ReserveQuota
```

The implementation may use an internal concrete context, `IMongoDatabase`, or
specific collections. Those details are not visible at the application boundary.

### Reads

Read capabilities must encode the visibility required by their consumers:

- tenant ID where the resource is tenant-owned;
- logical resource identity;
- lifecycle visibility when relevant;
- a deliberately small snapshot or domain model appropriate for the owner.

Callers must not receive raw `IMongoCollection<T>`, `IFindFluent`,
`FilterDefinition<T>`, or arbitrary query callbacks.

### Writes

A semantic write capability owns all invariants required for that write:

- tenant fence;
- resource identity;
- optimistic revision;
- lifecycle eligibility;
- mutation;
- unconditional event/outbox persistence.

For larger workflows, the caller passes its active `IClientSessionHandle` or typed
transaction context. The write capability verifies that the required transaction
is active. It must not silently start an independent transaction that separates a
resource mutation from quota, placement, release, reference, or outbox changes.

Expected duplicate, revision, or state conflicts are typed outcomes. MongoDB
availability, driver, serialization, and topology failures remain exceptions for
the global 5xx path.

## Feature-owned Dependency Injection

The central server dependency-injection file must become a host composition root,
not an inventory of every feature implementation.

Each feature receives one owner registration entry point in its Application
project:

```text
Feature.Application/
  DependencyInjection/
    FeatureRegistration.cs
```

The public extension follows one convention:

```csharp
public static IServiceCollection AddWebServicesFeature(
    this IServiceCollection services,
    IConfiguration configuration);
```

It registers everything owned by that feature:

- internal persistence implementations;
- named delegates;
- retained strategy interfaces;
- validators;
- HTTP mappings;
- event consumers;
- jobs and hosted services;
- feature options;
- migrations or migration assemblies;
- any feature-specific clocks, caches, or clients.

The server composition root should eventually contain only shared host concerns
and one call per enabled feature.

### Delegate registration and lifetimes

When several delegates use one implementation class, register the implementation
once and resolve every delegate from that same instance:

```csharp
services.AddScoped<WebServicePersistence>();

services.AddScoped<LoadWebServiceById>(provider =>
    provider.GetRequiredService<WebServicePersistence>().LoadByIdAsync);

services.AddScoped<PersistUpdatedWebService>(provider =>
    provider.GetRequiredService<WebServicePersistence>().PersistUpdatedAsync);
```

Do not register the implementation independently for every interface or delegate,
because that may create multiple scoped instances and split stateful behavior.

Feature-owned registration must be explicit, testable, and safe when invoked once
by the host. It must not use a service locator inside handlers.

### Future module projects

Do not create a separate composition project for every feature during the initial
migration. First establish the feature-owned registration API in Application.

When Pole implements dynamic plugin packaging or feature discovery, the same API
can move into `Feature.Module` or `Feature.Composition`. A future module interface
is a valid interface-based plugin extension point and may own:

- service registration;
- endpoint registration;
- consumer and event assembly discovery;
- feature metadata and dependencies.

Dynamic discovery itself is outside the scope of this migration.

## Per-feature Migration Procedure

Every feature follows the same sequence.

### Step 1: Inventory the boundary

- list mediator requests and handlers;
- list produced result and failure types;
- list cross-feature consumers and providers;
- list interfaces and the methods used by each consumer;
- list DbContext, collection, and Mongo filter consumers;
- list HTTP mappings;
- list events, consumers, jobs, and transactions;
- identify plugin or runtime strategy extension points.

### Step 2: Classify failures

For every existing failure, decide whether it is:

- structural request validation;
- capability contract violation;
- expected business or state failure;
- concurrency conflict;
- tenant-scoped not found;
- infrastructure failure.

Remove ambiguous `ValidationFailure` and `Invalid*Request` outcomes according to
that classification. Do not remove tenant or object-ID guards from a capability
that can be called without mediator validation.

### Step 3: Define stable contracts

- define provider-owned request and snapshot records;
- define expected provider error unions;
- define request-specific handler outcome unions;
- use `Result<T, TError>` where local composition needs it;
- keep domain types out of cross-feature contracts;
- document structural preconditions that throw.

### Step 4: Introduce semantic delegates

- split independently consumed interface methods into named delegates;
- replace DbContext access with read and write capabilities;
- retain only justified strategy or protocol interfaces;
- keep implementations internal where practical;
- preserve caller-owned transaction/session parameters for atomic workflows.

### Step 5: Migrate consumers

- update handlers first within the selected feature slice;
- compose provider error unions directly;
- remove repeated nullable and structural checks guaranteed by the provider;
- remove casts and generic `MapError` calls introduced only to bridge old types;
- keep transaction abort decisions explicit for expected failures.

### Step 6: Move registrations

- add the feature registration entry point;
- register implementations once with the correct lifetime;
- register all delegates from the intended implementation instance;
- register owner HTTP mappings;
- reduce the central DI file to one feature call;
- add a DI/startup test for the production registration graph.

### Step 7: Remove compatibility APIs

Parallel legacy adapters are allowed only while a feature still has unmigrated
consumers. Before the feature slice is complete, remove:

- legacy shared-result overloads;
- legacy `Failure` adapters;
- obsolete interfaces;
- duplicate HTTP mappings;
- obsolete DI registrations;
- dead tests and fakes for the old boundary.

If another feature still consumes a legacy contract, either migrate that consumer
in the same slice or record the adapter explicitly with an owner and removal point.

### Step 8: Validate and commit

- build the provider and consumer projects;
- run feature-focused unit and integration tests;
- test every new expected error branch;
- test at least one structural contract violation as an exception;
- verify HTTP problem status, code, type, and extensions;
- verify tenant isolation;
- verify optimistic conflict behavior;
- verify transaction abort leaves no mutation, quota usage, release, reference,
  or outbox partial state;
- verify success commits mutation and outbox together;
- run production DI startup validation;
- run `git diff --check`;
- create feature-scoped commits.

## Migration Waves

The ordering minimizes repeated work and starts with the boundary already exercised
by the typed WebService deployment experiment.

### Wave 0: Shared foundations and guardrails

- ResultType closed-state enforcement;
- mediator pipeline envelope and plugin-compatible pipeline failures;
- shared HTTP problem mapping and override behavior;
- feature registration naming and lifetime convention;
- architecture-test foundation;
- written engineering guidelines derived from this plan.

Wave 0 must not redesign every shared primitive in advance. Add only the shared
support required by the first feature slice, then evolve it from concrete usage.

### Wave 1: Typed deployment composition

1. ResourceDeployments
2. DeploymentTargets
3. Quotas
4. ComputeProfiles
5. Domains
6. WebServices

This wave completes and generalizes the existing `AddWebServiceDeployment`
experiment. It removes temporary typed/legacy parallel APIs after every consumer
in the wave has moved.

Special review points:

- `IResourceDeploymentAdapter` is expected to remain a strategy/plugin interface;
- broad ResourceDeployment query and mutation interfaces should be split by
  independently consumed operation;
- DeploymentTarget lookup and admission must preserve sanitized provider errors;
- quota reservation, commit, usage adjustment, and resource mutation must remain
  atomic where the workflow requires it;
- generated domain operations must stop returning structural `Invalid*Request`
  outcomes;
- WebService releases and outbox writes remain in the logical resource transaction.

### Wave 2: Workload and storage resources

1. RedisServices
2. Postgres
3. Functions
4. StorageProfiles
5. ResourceGroups

Reuse the semantic persistence capability pattern already explored in RedisServices.
Pay particular attention to resource placement, Kubernetes reconciliation inputs,
tenant fences, deletion lifecycle, and outbox writes.

### Wave 3: External providers and build pipeline

1. GitProviders
2. ContainerRegistries
3. SourceBuilds
4. DnsRecords

Provider infrastructure exceptions and sanitized expected provider outcomes must
remain distinct. Credentials and provider exception messages must never appear in
contract errors or HTTP problem extensions.

Runtime-selected provider strategies may retain interfaces. Consumer-facing leaf
operations should still use named delegates.

### Wave 4: Platform and projection features

1. Tenants
2. Users
3. Search
4. Outbox

Authorization and tenant pipeline behavior require explicit review because they
operate outside normal handler outcomes. Search and Outbox are infrastructure and
projection owners; the rules should be applied without forcing artificial
business unions where an operation is naturally event-driven or infrastructure-only.

## Initial Interface Review Rules

The first audited flow contains 35 production interfaces across ComputeProfiles,
DeploymentTargets, Domains, ResourceDeployments, WebServices, and Quotas. Domains
already exposes its relevant capabilities as delegates.

The initial expectation is:

- convert most one-operation lookup, writer, guard, fence, bootstrap, admission,
  and application-service interfaces to delegates or concrete classes;
- split broad interfaces when consumers use independent subsets;
- remove all feature DbContext interfaces from consumers;
- retain resource adapters, polymorphic profiles, and other demonstrated runtime
  strategies;
- review `IQuotaAdmissionExecutor` as a cohesive generic protocol rather than
  converting it mechanically;
- revisit every retained interface after its consumers have been migrated.

The decision is based on how consumers use the abstraction, not only on method or
line count.

## Architecture Enforcement

Add automated architecture checks as the migration establishes stable rules.
Migrated application handlers and orchestration services must not depend on:

- `IMongoDbContext` or a descendant;
- `IMongoCollection<T>`;
- Mongo filter, fluent query, or update-definition types;
- legacy shared `Failure` contracts;
- a broad feature interface when only one independent operation is required.

Additional checks should verify:

- cross-feature contracts do not reference provider domain assemblies;
- every owner error union has an exhaustive default HTTP mapping when applicable;
- feature registration can build the production DI graph;
- event-owning mutations have a transaction-aware persistence capability;
- newly migrated handlers declare request-specific outcomes.

Architecture rules should initially target migrated namespaces or projects. Expand
them feature by feature so partially migrated code does not block the entire
solution without providing an actionable path forward.

## Testing Strategy

Each feature slice adds or updates tests at the narrowest useful level:

1. Contract tests for union leaves and direct conversions.
2. Capability tests for success, expected errors, and structural exceptions.
3. Persistence tests for tenant, revision, lifecycle, session, and outbox behavior.
4. Handler tests for cross-feature composition without error casts.
5. HTTP tests for owner mappings and consumer overrides.
6. Production DI startup tests.
7. Focused end-to-end integration tests for the feature's main workflow.

For Mongo integration tests in development environments where the default image
is unavailable, use the documented `POLE_TEST_MONGO_IMAGE` override and report the
selected image. Infrastructure startup failures must be distinguished from product
assertion failures.

The large integration test project may be split in a separate enabling task if its
compile blast radius or fixture coupling prevents incremental migration. It is not
required merely to make this architecture change look smaller.

## Commit and Review Strategy

- Use one umbrella task to preserve the shared rules and completion checklist.
- Work feature by feature.
- Prefer one coherent commit per feature or per independently reviewable boundary
  inside a large feature.
- Do not mix unrelated domain or product changes into migration commits.
- Keep the solution compiling between commits.
- Record temporary compatibility adapters and remove them before the owning wave
  completes.
- Run a post-task diff review for tenant isolation, transaction boundaries,
  outbox reliability, HTTP mappings, DI lifetimes, and test gaps.

Separate pull requests may be used for migration waves or large features even
though the work is tracked by one umbrella task.

## Risks and Mitigations

### Excessive delegate count

Several explicit dependencies are acceptable. If a handler requires an excessive
number, review whether it is coordinating multiple workflows that deserve a named
orchestration capability. Do not hide the problem behind a generic service facade.

### Project and registration proliferation

Start with one registration file in each Application project. Introduce module
projects only when plugin packaging or dynamic loading gives them a real role.

### Temporary duplicate APIs

Typed and legacy APIs may coexist during one feature migration, but every adapter
must have a removal point. A wave is not complete while unnecessary duplicates
remain.

### Incorrect exception classification

Keep guards even when removing an `Invalid*Request` union leaf. Tests must prove
that contract violations fail fast and expected application state still produces
typed outcomes.

### Lost tenant or transaction invariants

Moving code behind delegates must centralize invariants, not merely rename methods.
Every write review explicitly covers tenant, revision, lifecycle, session, quota,
references, mutation, and outbox behavior.

### Plugin extensibility

Do not close genuine extension points merely to maximize delegate usage. Runtime
strategies and future plugin module contracts are intentional interface cases. The
mediator pipeline envelope must remain open to plugin-provided behaviors.

## Feature Slice Completion Criteria

A feature is migrated when:

- its mediator handlers return request-specific outcome unions;
- its local composition uses typed results or direct unions intentionally;
- expected errors are provider- or feature-owned typed contracts;
- structural contract violations throw and are not HTTP-mapped as expected 400s;
- consumers depend on named semantic capabilities or justified interfaces;
- no handler or orchestration service depends on a feature DbContext or raw Mongo
  API;
- tenant, revision, lifecycle, transaction, and outbox invariants are centralized;
- feature-owned DI registration replaces central implementation registrations;
- owner HTTP mappings and overrides are covered by tests;
- obsolete legacy APIs and registrations are removed or have a documented removal
  owner in the current wave;
- focused build, tests, DI startup, and diff checks pass.

## Global Completion Criteria

The umbrella migration is complete when:

- all feature directories have completed the inventory and classification;
- all application handlers use request-specific outcome unions;
- the legacy shared handler result/failure flow is removed where it is no longer
  needed by the mediator pipeline;
- all cross-feature expected failures are typed provider contracts;
- `IMongoDbContext` and feature descendants are absent from application consumers;
- raw Mongo APIs are confined to internal persistence implementations;
- retained interfaces have a documented strategy, plugin, polymorphism, lifetime,
  or protocol reason;
- every feature has one owner DI registration entry point;
- the central server DI file contains shared host infrastructure and one call per
  feature rather than feature implementation details;
- compatibility adapters introduced by the migration are removed;
- architecture tests enforce the new boundaries;
- engineering guidelines describe the final conventions;
- the full build and appropriate integration suites pass, with infrastructure
  limitations reported separately.

## Deferred Decisions

The following decisions should be made from implementation evidence rather than in
advance:

- the final name and representation of the mediator pipeline envelope;
- whether `IQuotaAdmissionExecutor` remains an interface after legacy overloads are
  removed;
- whether specific feature registrations later move into dedicated Module projects;
- whether dynamic plugin module discovery uses reflection, generated catalogs, or
  explicit host configuration;
- whether the integration tests should be split into per-feature projects;
- whether additional source generators or analyzers should enforce union and
  mapping conventions.

These deferred decisions must not block feature-by-feature migration under the
rules already established in this plan.
