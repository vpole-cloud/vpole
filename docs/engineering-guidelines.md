# Engineering Guidelines

These guidelines describe how Pole code should divide responsibilities and make design decisions. They complement the focused architecture, multitenancy, observability, and testing documentation.

## Module boundaries

Feature Domain projects do not reference Contracts, Events, Application, or other features.
Cross-feature synchronous integration uses provider-owned Contracts; asynchronous integration uses
provider-owned Events. Application projects do not reference another feature's Domain or
Application. Small, genuinely shared concepts belong to thematic dependency-leaf projects.

The complete dependency matrix, Mongo transaction exception, event evolution policy, and
architecture-test rules are defined in [Module boundaries](module-boundaries.md).

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

Use `Result` for expected failures that callers are required to handle, such as validation failures, conflicts, missing resources, denied operations, and known provider failures.

- Define failures at the boundary that understands their meaning and map them when crossing abstraction layers.
- Do not require every caller to catch the same exception and repeat the same mapping logic.
- Reserve exceptions for unexpected failures, broken invariants, cancellation, and infrastructure conditions that cannot be handled meaningfully at the current boundary. In particular, do not convert MongoDB availability or driver failures into `Result`; let them reach the global exception handler and produce the standard 5xx response.
- Do not catch an exception only to convert every failure into an indistinguishable generic result.
- Use `Result<bool>` only when both `true` and `false` are meaningful successful answers and a caller must separately handle an expected failure. For a command precondition, prefer an intention-revealing `Task<Result>` method such as `EnsureNoConfigurationAsync`: return success when the condition holds and `Conflict` when it does not.

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
