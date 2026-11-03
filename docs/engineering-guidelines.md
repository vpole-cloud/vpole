# Engineering Guidelines

These guidelines describe how Pole code should divide responsibilities and make design decisions. They complement the focused architecture, multitenancy, observability, and testing documentation.

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
are not a condition for Domain ownership. Application.Persistence owns database operations,
filters, runtime index identifiers, codecs and transport serialization configuration.
Feature.Migrations owns frozen BSON schema and index migrations; it references only the small
shared migration API and never Application, Domain, Contracts, or runtime helpers.
MongoDB BSON mapping attributes may live with the persisted Domain models.

DTOs and integration events retain their Contracts/Events ownership when serialized as payloads.
Technical `BsonDocument` use in schema/index migrations does not define a feature model.
`POLE010` checks feature-owned root types used by Mongo collection APIs; architecture checks
cover F# collection declarations as well. There are no legacy model-placement exceptions.
GitOps Domain may reference MongoDB and declare BSON mapping attributes, like other Domain projects.
It stays independent of Contracts and transport; contract conversion and codecs belong to Application.

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

Group fields into smaller models when they form a coherent concept with shared invariants.
The parameter limit applies to contract operations, not DTO constructors, dependency injection
constructors, or pure model factories. `ContractOperationParameterTests` enforces the limit across
all feature Contracts projects without feature exemptions.

## Validation boundaries

Separate validation by the kind of information required to evaluate a rule.

### Request validation

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

## Model states explicitly

When fields are valid only during particular phases or variants, model those states as separate types instead of combining them into one type with many nullable properties.

- Each state should require the data needed to be valid in that state.
- A nullable member should represent a genuinely optional value within one state, not the possibility that the object is actually in another state.
- Use the language and persistence framework's supported polymorphism or discriminated representation when state variants must be stored.
- Keep state transitions explicit so invalid combinations are difficult to construct and easy to review.

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

## Treat nullability as a contract

Enable nullable reference types and fail the build on nullable compiler warnings.

- Do not make request or domain members nullable solely because model binding, serialization, or another framework creates the object.
- Let the owning boundary report missing required input. Do not repeat null or blank checks in every downstream layer when middleware, a pipeline behavior, or request validation already owns the rule.
- Do not add `ThrowIfNull` for a non-nullable internal parameter unless the method is a documented runtime boundary that can still receive null from unannotated or reflection-based callers.
- Use nullable members only for values that are genuinely optional in a valid state.

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
test gate. New positional record/union/delegate declarations with multiple parameters
put each parameter on its own line. Shared DTOs are allowed; a shared mutation outcome
that adds impossible results to unrelated handlers is not.

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
