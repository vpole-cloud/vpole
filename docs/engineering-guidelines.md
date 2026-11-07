# Engineering Guidelines

These guidelines describe how Pole code should divide responsibilities and make design decisions. They complement the focused architecture, multitenancy, observability, and testing documentation.

Use the [review checklist](review-checklist.md) to review an actual diff against these rules.
The checklist links policy to evidence; it does not replace the rules or their automated checks.

## Module boundaries

Feature Domain projects do not reference Contracts, Events, Application, or other features.
Cross-feature synchronous integration uses provider-owned Contracts; asynchronous integration uses
provider-owned Events. Application projects do not reference another feature's Domain or
Application. Small, genuinely shared concepts belong to thematic dependency-leaf projects.

Platform capabilities must not reference concrete service-provider modules, including their
Contracts and Events. Built-in workload providers also contribute through platform-owned extension
contracts. Shipping a provider with Pole does not make its rules part of generic infrastructure.

The complete dependency matrix, Mongo transaction exception, event evolution policy, and
architecture-test rules are defined in [Module boundaries](module-boundaries.md).

### Persisted feature models

Every model of a feature's own persisted state belongs to that feature's Domain project.
This includes aggregate documents, immutable revisions, history, retained cleanup authority,
stored snapshots and projections, including their feature-owned nested data. Business methods
are not a condition for Domain ownership. Application workflow implementations own database operations,
filters, runtime index identifiers, codecs and transport serialization configuration. Raw Mongo query
APIs are forbidden in `Endpoints`, `Consumers` and `Jobs` at any directory depth. Each feature
keeps its runtime filters in one `Mongo/<Feature>Filters.cs` file, which may contain multiple helper
classes. Shared collection/index definitions and BSON mappings use the same Application-level `Mongo`
folder; workflow reads and mutations remain beside their scenarios.
Feature.Migrations owns frozen BSON schema and index migrations; it references only the small
shared migration API and never Application, Domain, Contracts, or runtime helpers.
MongoDB BSON mapping attributes may live with the persisted Domain models.

DTOs and integration events retain their Contracts/Events ownership when serialized as payloads.
Technical `BsonDocument` use in schema/index migrations does not define a feature model.
`POLE010` checks feature-owned root types used by Mongo collection APIs; architecture checks
cover F# collection declarations as well. There are no legacy model-placement exceptions.
GitOps Domain may reference MongoDB and declare BSON mapping attributes, like other Domain projects.
It stays independent of Contracts and transport; contract conversion and codecs belong to Application.

### Typed MongoDB operations and explicit BSON exceptions

Production C# and F# code must not use `MongoDB.Bson.BsonDocument`, `BsonArray` or `BsonValue`.
Use typed collection models, field expressions, indexes and commands. `POLE014` checks resolved
symbols, including aliases and inferred return types; comments, literals and unrelated namesakes
are not violations. BSON mapping attributes and scalar BSON types retain their existing roles.

`POLE013` rejects string field arguments in production C# Mongo filter, update, sort and index
builders, including constants, local variables and computed strings in shared infrastructure.
Use expressions directly in builders. Aggregation boundaries that need textual field paths use
`MongoFields.Field` and `MongoFields.Reference` to preserve concrete serializer mappings.
Its migration/test exemptions follow the same evaluated project roles as `POLE014`.

C# is checked during compilation. The architecture suite checks F# with the compiler service from
the pinned SDK, using actual project options and references after the solution build. Missing
compiler inputs or semantic errors fail the check; they do not turn into an empty passing result.

A deliberate driver boundary uses `[ViolateArchitecture(ArchitectureRules.RawBson, "reason")]`
(or the F# attribute syntax) on the smallest useful method, property or cohesive infrastructure type.
The reason explains why typed APIs cannot preserve the required behavior, for example database-time
aggregation expressions or dynamic driver error metadata. The exception covers its own declaration
and implementation, never callers, unrelated siblings or inherited types. Assembly-wide exceptions
are unavailable. Do not hide raw BSON behind JSON strings, bytes or an unannotated facade.

Migration projects and test projects are exempt by project role. `Directory.Build.props` assigns
`Migration` to the shared Migrations API and feature `.Migrations` projects; the test SDK supplies
`IsTestProject`. Test-only support libraries/probes explicitly declare
`<PoleArchitectureRole>TestSupport</PoleArchitectureRole>` without becoming executable test suites.
Production projects cannot depend on test-only projects. A `Migrations` or `Tests` folder inside a
production project grants no exemption. Shared production migration infrastructure still needs a
scoped exception where it handles the migration ledger's BSON identity representations.

### Explicit Domain construction

Domain constructors, including primary constructors and nested `CreateOptions`, require every
argument explicitly. Initial state belongs to factories and `DefaultOptions`; optional parameters
on factory methods remain allowed. `POLE011` enforces constructor parameters without exceptions.
Do not add compatibility constructors or stored-field defaults for an undeployed database schema.

### Cross-feature contract parameter limit

Public contract delegates and interface operations accept at most **three total parameters**,
including an explicit Mongo session and cancellation token. Put business inputs into a named,
provider-owned request record: `(request, cancellationToken)` or
`(request, session, cancellationToken)`. Keep optional input defaults on that request model;
do not add compatibility overloads with the old parameter list.

Choose request submodels using [cohesive data and explicit policy](#cohesive-data-and-explicit-policy).
The parameter limit applies to contract operations, not DTO constructors, dependency injection
constructors, or pure model factories. `ContractOperationParameterTests` enforces the limit across
all feature Contracts projects without feature exemptions.

## Validation boundaries

Separate validation by the kind of information required to evaluate a rule.

### Request validation

HTTP JSON enum values use their declared string names. The shared `AddPoleHttpJson` HTTP
configuration rejects integer tokens and numeric strings, including defined enum ordinals.
Property converters at this boundary must also reject integers; they take precedence over the
shared options. String fields that represent enums must validate named values. Persistence,
integration events and external protocols retain their own serialization configuration.

Request validation checks the request itself. This includes required values, formats, ranges, lengths, and relationships between fields that can be evaluated without reading external state.

- Keep request validation deterministic and free of I/O.
- Use FluentValidation at the API or application boundary when it fits the request pipeline.
- Pass validated requests to handlers and lower layers instead of repeating the same structural checks throughout the call chain.

### Domain and contextual validation

Domain validation checks business rules, aggregate invariants, or facts that depend on the current system state. It can require a database lookup, another Pole feature, or an external service.

- Enforce intrinsic aggregate invariants in domain factories or domain methods.
- Perform contextual rules in handlers, application/domain services, or focused helper abstractions close to the decision being made.
- Do not move domain rules into request validators solely to use one validation mechanism.
- A lower layer should still protect the invariants it owns, but it should not duplicate the complete request validator.

## Expected failures

Represent expected failures that callers must handle as explicit typed values rather than
exceptions. Choose the result shape at the boundary that owns the decision:

- Synchronous contracts use provider-owned, operation-specific outcome unions, including
  encryption operations.
- A Mediator request handler owns a flat, closed application outcome containing its concrete
  successful results and expected failures. Avoid nesting error-only wrappers in endpoint outcomes.
- Closed HTTP outcomes use an exhaustive switch calling typed mapping methods directly. Prefer
  static imports for those methods; `HttpProblem` implements `IResult`. Dynamic HTTP mapping
  remains the mechanism for open pipeline/plugin rejections.
- A pipeline behavior stops request execution with `PipelineRejected` and a typed
  `IMediatorRejection`; request validation uses `RequestValidationRejected`.

- Define failures at the boundary that understands their meaning and map them when crossing abstraction layers.
- Do not require every caller to catch the same exception and repeat the same mapping logic.
- Reserve exceptions for unexpected failures, broken invariants, cancellation, and infrastructure conditions that cannot be handled meaningfully at the current boundary. In particular, do not convert MongoDB availability or driver failures into `Result`; let them reach the global exception handler and produce the standard 5xx response.
- Do not catch an exception only to convert every failure into an indistinguishable generic result.
- Give command preconditions a named success state and concrete failures, as in
  `EnsureTenantsExistOutcome(TenantsExist, TenantSetNotFound)`. Use a boolean successful value
  only when both `true` and `false` are meaningful answers rather than success/failure markers.

### Exact outcomes and closed state hierarchies

Each operation's union contains only results that operation can produce. Reuse concrete error
records across operations where their meaning is identical; do not use a universal failure enum
or wrapper that admits impossible errors. Map typed operational failures explicitly to persisted
or transport diagnostic codes. Enums remain appropriate for categories whose entire set is valid.

Use C# closed record hierarchies for finite domain state families and sealed terminal variants.
Use operation-specific unions for outcomes and open interfaces for provider extension points.
Match the typed union or closed hierarchy with an exhaustive switch expression; do not switch on
an untyped Value and add a fallback that hides a newly introduced case. Null is a separate case
only where the input contract is nullable. `POLE012` rejects catch-all arms over closed hierarchies;
name rejected states explicitly so new variants still require a decision. Persistence still requires explicit discriminator
configuration and serialization verification.

## Endpoint file structure

Keep each Minimal API endpoint with its application boundary in one file: the route mapping, request/response types, request validator, and Mediator handler belong together. A file may contain one endpoint-handler pair.

- Organize related endpoint files in subfolders under `Endpoints` when that improves discovery.
- Keep only narrowly shared DTOs, helpers, or mapping extensions outside the endpoint file.
- Do not separate a route mapping into an aggregator file merely to group several handlers; use one endpoint file per route instead.

## Prefer existing solutions

Before creating a new implementation, check the platform standard library, framework APIs, existing project abstractions, current dependencies, and mature libraries appropriate to the problem.

- Prefer an existing solution when it satisfies the requirements and keeps ownership and maintenance clear.
- Add a custom implementation when existing options do not fit or when it provides a concrete advantage in simplicity, control, security, performance, or maintainability.
- Keep custom code as small as the missing capability permits.
- Record the reason near the implementation or in an architectural decision when the tradeoff is not evident from the code.

## Readable code and focused responsibilities

Separate adjacent type declarations and distinct logical steps with blank lines. Keep guard checks,
state reads, decisions, mutations and result construction visually distinguishable. Use meaningful
intermediate names when a compact expression hides those steps. Follow the existing C# line limit
of 120 characters; preserve string literals and wire formats when wrapping code.

Prefer cohesive classes over handwritten partial classes. When a provider has too many distinct
responsibilities, separate planning, reference resolution, execution or lifecycle capabilities
where those are real boundaries. Required generated partial declarations remain valid. A large
file alone does not justify splitting one class into partial files.

Treat a large dependency-injection constructor as a reason to inspect responsibilities, not as a
numeric violation. Split cohesive behavior rather than hiding the same dependencies in a
`Dependencies` or `Context` bag, a service locator or a new interface for every helper. The contract
parameter limit does not apply to DI constructors.

A helper should own a rule, transformation, check or useful abstraction. Remove forwarding wrappers
that merely repeat another method's name and arguments. Capability names should describe the
operation they provide; a class named `Planning` should actually plan rather than combine unrelated
reads, lookups and fences.

## Cohesive data and explicit policy

Group related fields in Domain, Contracts and DTOs when they describe one concept with shared
invariants or lifecycle. An artifact reference and digest can form an artifact model; unrelated
revision counters do not become one concept because they share a numeric type. Preserve each
boundary's own representation instead of exporting a foreign Domain model to avoid mapping.

Choose enums for categories or behavior policies whose complete set of values is valid. Prefer an
explicit policy to a boolean parameter when the call selects consequential behavior, such as
retaining old references until rollout. Ordinary boolean data does not require an enum. Use typed
state or outcome variants when different cases require different data or invariants.

Compute reported state at its owner before mapping a DTO. An `Unknown` status must describe a real
unknown state, not conceal a missing lookup behind a hardcoded fallback. Pass operation-specific
failure reasons explicitly when a default would hide which decision was made.

## Model states explicitly

When fields are valid only during particular phases or variants, model those states as separate types instead of combining them into one type with many nullable properties.

- Each state should require the data needed to be valid in that state.
- A nullable member should represent a genuinely optional value within one state, not the possibility that the object is actually in another state.
- Use the language and persistence framework's supported polymorphism or discriminated representation when state variants must be stored.
- Keep state transitions explicit so invalid combinations are difficult to construct and easy to review.

Exhaustiveness applies to closed state families. An open extension interface may need a defensive
failure for an unsupported implementation; do not remove that boundary merely to eliminate every
default switch arm.

## Own mutable collections locally

Keep mutation inside the smallest scope that needs it.

- A helper that accumulates values should normally create its own mutable collection and return an immutable or read-only result.
- Avoid passing builders through multiple methods when those methods can return their own results for the caller to combine.
- Use iterator methods and `yield return` when results are naturally produced as a sequence and do not require shared accumulation.
- Pass a shared builder only when shared mutation is central to the algorithm or a demonstrated performance requirement.

## Prefer strongly typed APIs

Use typed members, expressions, identifiers, and framework APIs instead of string-based names or paths when a reliable typed alternative exists.

- Avoid magic strings that duplicate names already represented by the type system.
- When a string-based API is unavoidable, centralize stable identifiers where doing so reduces drift.
- Do not force a typed API when it cannot express the required behavior correctly; document the reason for the fallback when it is not obvious.

Name resource identity and address construction rules explicitly: hostname labels, Kubernetes
object names, and Service addresses belong to their semantic owner rather than repeated string
interpolation in handlers or DTO mapping. This does not require wrapping ordinary display text.

Give each document/version-key format a named builder owned by the feature that defines it, even
when that format currently has one caller. Reuse existing kind constants. Shared YAML field paths
belong in the declaration field-path catalog; one-off diagnostic paths can remain local. Preserve
the exact formats while centralizing their construction.

Prefer typed Mongo members and existing polymorphic filter APIs to literal field or discriminator
paths. `nameof` alone does not account for BSON aliases or identity representation. Where the
driver cannot express an operation through typed builders, use serializer-aware paths and retain
the same server-time, literal-value and concurrency semantics. Production use of `BsonDocument`,
`BsonArray` or `BsonValue` requires a narrow, explicit architecture exception with a reason;
migration and test project roles are exempt from this type policy. A folder name is not a
project-role exemption. Verify the actual enforcement available in the reviewed tree before
calling this check automated.

Validated numeric port values share the range 1..65535 in these workload contracts. Keep parsing
and range checks in one small value type and protect its default struct value at the boundaries
that consume it. A numeric port does not establish protocol, endpoint eligibility, or resolve a
Kubernetes named target port; those decisions remain with the provider. Do not add implicit
unchecked conversions that bypass the value's invariant.

## Verify uncertain dependency behavior

Do not assume unclear framework or library behavior when correctness depends on it. Write a focused characterization or integration test to establish how the dependency behaves in the scenario Pole relies on.

- Put the test at the lowest shared boundary that owns the dependency assumption.
- Test the uncertain behavior once rather than repeating the same library test in every feature.
- Feature tests should verify Pole behavior and only add another dependency-focused case for a distinct shape, edge case, or known upgrade regression.
- Prefer documented contracts, but retain a focused test when translation, serialization, runtime integration, or version behavior is material and not sufficiently guaranteed by documentation.

Make regressions fail for the intended reason. A duplicate-identity test must avoid an earlier
duplicate-name rejection and assert the specific conflict, not only HTTP 409. Mongo tests that
depend on validators or unique indexes must register the real owner migrations. Verify actual
startup registration when moving migration assemblies. Tests should prove behavior or invariants,
not reproduce formatting or the implementation's sequence of calls.

## Treat nullability as a contract

Enable nullable reference types and fail the build on nullable compiler warnings.

- Do not make request or domain members nullable solely because model binding, serialization, or another framework creates the object.
- Let the owning boundary report missing required input. Do not repeat null or blank checks in every downstream layer when middleware, a pipeline behavior, or request validation already owns the rule.
- Do not add `ThrowIfNull` for a non-nullable internal parameter unless the method is a documented runtime boundary that can still receive null from unannotated or reflection-based callers.
- Use nullable members only for values that are genuinely optional in a valid state.
- Express conditional guarantees with nullable-flow attributes, such as `[NotNullWhen(true)]` on a
  nullable `out` parameter when a successful `TryCreate` guarantees a value. Keep the annotation
  consistent with every return path instead of compensating with caller-side null suppression.

## Validate configuration at startup

Configuration errors should prevent the application from starting.

- Bind settings through the options pipeline and use data annotations or a focused `IValidateOptions<T>` implementation.
- Use `ValidateOnStart` for settings required by runtime services.
- Do not validate the same settings in service methods or constructors invoked during an arbitrary request.

## Bound reflection discovery

Reflection-based registration is acceptable when it removes repetitive feature wiring and has a clear validation boundary.

- Scan only assemblies explicitly supplied by composition-root code.
- Reject duplicate identities and incompatible registrations during startup.
- Keep discovered abstractions strongly typed and test that unrelated assemblies are excluded.
- Prefer explicit registration when discovery rules would be implicit, broad, or difficult to validate.

## Centralize cross-cutting orchestration

Feature handlers should describe feature intent while shared infrastructure owns reusable lifecycle mechanics.

- Keep feature-owned calculation in typed definitions or profiles.
- Put repeated reserve/commit/release/compensation or similar sequences behind one shared executor or pipeline behavior.
- Keep the resource mutation and outbox writes together in the transaction callback supplied by the feature.
- Give stable cross-cutting identities small typed helpers instead of constructing strings throughout handlers.

## Automated delivery checks

[Agent delivery](agent-delivery.md) defines canonical compiled examples, the early slice
review, semantic architecture diagnostics, formatting and the mandatory full local
test gate. New positional records and record structs with multiple parameters put each
parameter on its own line. Unions and delegates follow ordinary method formatting. Shared DTOs
are allowed; a shared mutation outcome that adds impossible results to unrelated handlers is not.

## Record data construction

Prefer positional primary constructors for data records. A record uses either all positional
constructor data or ordinary instance properties without a positional constructor. Do not combine
a positional record with extra stored instance properties/fields or another data constructor.
Computed members, static helpers, abstract contracts, inherited members and record copy constructors
remain valid. Primary constructors on ordinary dependency-injected classes are unrelated to this rule.

`POLE009` enforces this semantically, including partial declarations and backing fields. Existing
members outside the GitOps slice are listed individually in `RecordDataConstructionLegacy.cs`;
architecture tests prevent new violations and stale allowances. This finite ratchet avoids rewriting
unrelated event and workload contracts as part of the GitOps remediation. Remove an allowance when
correcting that member; never exempt a whole type or add an allowance for new code.

Some non-positional records retain explicit constructors so collection defaults, legacy Mongo
normalization and event identity initialization remain non-null without nullable positional inputs.
