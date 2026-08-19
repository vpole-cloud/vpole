# Module boundaries

Pole is a modular monolith. A feature owns its domain model and implementation; other features
integrate only through explicitly published synchronous contracts or serialized integration
events. Small, stable concepts shared by several modules live in thematic leaf projects.

These rules apply to production projects under `src/features`. `Pole.Server` is the composition
root, and tests may reference the production assemblies required by their verification boundary.

## Project roles

### Domain

`Feature.Domain` owns aggregates, value objects, invariants, lifecycle states, and domain
transitions. Domain types are internal implementation details of the feature boundary even when
their CLR visibility is public for persistence or application use.

A Domain project may depend on dependency-free thematic primitives and technical leaf libraries.
It must not reference Contracts, Events, Application, or another feature project.

Domain events, when needed, belong to Domain. They describe an internal transition, may contain
domain types, and are not serialized integration contracts.

### Primitives

`Feature.Primitives` contains immutable, dependency-leaf concepts with identical semantics in the
feature's Domain, Contracts, or Events. It does not reference other feature projects. Only projects
from the same feature normally reference it directly. A consumer Application may reference a
provider's Primitives when a published provider Contract uses those primitive types in its public
surface. This exception does not allow consumer Domain, Contracts, or Events projects to depend on
foreign Primitives.

Concepts that are platform-wide independently of one provider contract belong in a thematic
project under `src/shared` instead.

### Contracts

`Feature.Contracts` is the provider-owned synchronous in-process API of a feature. It contains
narrow interfaces or delegates and their input/output representations. HTTP request and response
types remain with their endpoints unless they are genuinely used as an inter-feature contract.

Contracts may depend on the BCL, `ResultType`, and small thematic primitives. A narrow operation
that must join a caller-owned Mongo transaction may accept `IClientSessionHandle`; this is an
explicit in-process transaction exception, not a claim that the contract is transport-portable.

A Contracts project must not reference Domain, Events, Application, or another feature's
Contracts. When two contracts appear to need the same type, either give each boundary its own
representation and map in Application, or promote a genuinely shared concept to a thematic
primitives project.

Contract operations return contract representations, not domain aggregates.

### Contract HTTP mapping

`Feature.Contracts.HttpMapping` is an optional companion for the default HTTP representation of
provider-owned synchronous contract errors. It depends on `Feature.Contracts` and the shared HTTP
mapping abstraction; the Contracts project never depends on it. A consuming Application continues
to reference only the provider Contracts, while `Pole.Server` composes the mapping companion.

Contract HTTP mappings define provider-owned defaults. A consuming HTTP boundary may supply an
explicit local override when its endpoint gives a contract error different HTTP semantics. Global
duplicate registrations and order-dependent "last registration wins" behavior are not allowed.
Map a closed contract error union with an exhaustive switch and treat non-exhaustive switch
diagnostics as build errors in the companion project.

HTTP mappings for feature-local Application errors stay in that feature's Application project.
They are not cross-feature contracts, so they do not justify another companion project. Keep the
mapping separate from endpoint methods, register it at the server composition root, and apply the
same exhaustive-switch rule to closed local error unions.

Core Pole problem types use stable, non-dereferenceable identifiers derived from their codes:
`tag:pole.dev,2026:problem:<code>`. The tag authority is part of the public contract and never
changes with a self-hosted installation's hostname. A plugin owns the namespace and HTTP mapping
for its own problem types; the host does not rewrite those identities.

### Mediator outcomes

A request handler owns its closed application outcome. `MediatorResult<TOutcome>` defines the
public pipeline envelope returned by `IMediator.Send`, whose only control-flow alternatives are the
request outcome and `PipelineRejected`. A rejection carries an `IMediatorRejection` reason. This
marker is intentionally open so a host or plugin can add a rejecting behavior without changing the
core union. Pipeline behavior delegates also return `MediatorResult<TOutcome>`, so a plugin behavior
can stop execution with its own rejection type without changing a request handler contract.

Existing `Result<TResponse>` handlers remain an internal compatibility path until they migrate. The
runtime translates their failures into `LegacyResultRejected`. Request validation stops the
pipeline with `RequestValidationRejected`, which retains structured field errors without depending
on the legacy failure hierarchy. Base tenant authorization emits `TenantPlatformAdminRequired` or
`TenantRoleRequired`; its missing-tenant case reuses `RequestValidationRejected`. The feature-owned
tenant-member authorization behavior emits feature-owned authentication, identity, tenant,
membership, and member-not-found rejections, and reuses `TenantRoleRequired` for its Viewer, Admin,
and Owner requirements.

New rejection reasons require an explicit transport mapping. The HTTP adapter resolves the existing
`IHttpProblemMapper` from the active request when its deferred result executes. This lets a plugin
register `HttpProblemMapping.For<TRejection>` through DI without changing every endpoint. A missing
mapping fails explicitly instead of guessing a status code or leaking a transport-shaped contract
into the mediator abstraction.

Rejection reasons do not expose generic transport members such as an HTTP status, error kind, or
message. Each concrete reason remains typed, and the relevant transport adapter owns its external
mapping. Unexpected, infrastructure, and cancellation failures remain exceptions rather than
pipeline rejections.

### Events

`Feature.Events` contains serialized integration events published through the outbox. Event types
implement `IPoleEvent`, carry `EventCore`, and own their nested payload records. They contain the
stable facts consumers need, never foreign aggregates.

Events may depend on the shared event abstractions and small thematic primitives. They must not
reference Domain, Contracts, Application, or another feature project.

An integration event is an historical serialized contract:

- Existing fields do not change name or meaning.
- Compatible optional fields may be added.
- Breaking changes introduce a new event type or version.
- Enums have stable string representations rather than relying on numeric ordinals.
- Previously persisted outbox payloads must remain deserializable after deployment.

Application maps domain state to event-owned payloads. Avoiding a small amount of mapping code is
not a reason to couple event evolution to a domain or synchronous contract model.

### Application

`Feature.Application` owns orchestration, persistence implementations, endpoint flows,
integration-event mapping, consumers, and infrastructure adapters.

Application may reference its own Domain, Contracts, and Events. It may reference another
feature's Contracts for synchronous capabilities and another feature's Events to consume its
published facts. It may also reference the provider's dependency-leaf Primitives when those types
are exposed by that Contract. It must not reference another feature's Domain or Application.

The normal synchronous dependency direction is:

```text
Consumer.Application -> Provider.Contracts
Pole.Server -> Provider.Application implementation
```

The normal asynchronous direction is:

```text
Provider.Application -> Provider.Events -> outbox
Consumer.Application -> Provider.Events -> consumer
```

## Dependency matrix

| Source project | Allowed feature dependencies | Forbidden feature dependencies |
| --- | --- | --- |
| Primitives | None | Every feature project |
| Domain | Own Primitives | Contracts, Events, Application, another feature |
| Contracts | Own Primitives | Domain, Events, Application, another feature's Contracts/Primitives |
| Contracts.HttpMapping | Own Contracts; shared HTTP mapping abstraction | Domain, Events, Application, another feature project |
| Events | Own Primitives | Domain, Contracts, Application, another feature |
| Application | Own Primitives/Domain/Contracts/Events; foreign Contracts/Events; contract-exposed foreign Primitives | Foreign Domain/Application |
| Pole.Server | Application projects required for composition | None imposed by this policy |
| Tests | Projects required by the test boundary | None imposed by this policy |

References to dependency-leaf shared projects are governed by the shared-primitives rules below,
not by the feature dependency matrix.

## Shared primitives

Use a thematic shared project only when a concept has the same meaning across multiple module
boundaries. A shared primitive should be immutable, dependency-light, stable in serialization,
and free of I/O, orchestration, persistence attributes, and feature lifecycle behavior.

Good candidates include typed identifiers and small platform-wide enums. Aggregates, lifecycle
models, endpoint bodies, resolved-operation DTOs, and event payload records are not primitives.

Prefer a name that identifies the concept, such as `Workloads` or `KubernetesValidation`, over a
generic dumping ground. Resource-type constants remain owned by their resource feature even when
the `ResourceTypeId` value object is shared.

When several domains repeatedly require one another's types, first reassess the bounded-context
boundary rather than automatically moving a large model into shared code.

## Exceptions and enforcement

Architecture tests validate project references and semantic event placement. Existing violations
may temporarily appear in an explicit allowlist containing the exact source, target, reason, and
tracking issue. An allowlist entry is migration debt, not a permanent extension of the matrix.

New violations are not added implicitly. Removing a violation must remove its allowlist entry.
The desired steady state is an empty allowlist.
