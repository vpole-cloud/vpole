# Review checklist

Use this checklist for self-review and independent review of the actual changed tree. It applies
to C# and F# where the language and project role support the rule. The canonical policies remain
in [Engineering guidelines](engineering-guidelines.md), [Module boundaries](module-boundaries.md)
and the focused feature documentation. This document organizes their review, not a second set of
implementations or copied feature schemas.

## Review target and evidence

Record the request, agreed scope, base commit, head commit and working-tree state before reviewing.
Inspect the complete file list and diff. Follow changed invariants into relevant callers,
serializers, alternate write paths and tests. When a request calls for a repository-wide audit,
record its scope; do not restrict it to the user's example or expand it into unrelated refactoring.

For every manual area below, record one disposition:

- **Checked:** name the inspected paths or behavior and the supporting evidence.
- **Finding:** give a file/line, the violated rule, failure mechanism and consequence.
- **Not applicable:** explain why the area does not apply; related areas may share one explanation.
- **Unverified:** state the missing evidence or prerequisite and its effect on readiness.

A checked box means the reviewer inspected the applicable behavior. Passing tests alone do not
complete the manual checks. Keep the record concise; link existing evidence instead of copying logs.

## Manual checks

- [ ] **R1 — Readability.** Can the reader distinguish guards, reads, decisions and mutations?
      Check type separation, logical spacing, meaningful intermediate names and long expressions.
      Verify that formatting changes preserve literals and behavior. See
      [readable code](engineering-guidelines.md#readable-code-and-focused-responsibilities).
- [ ] **R2 — Models and construction.** Do submodels describe cohesive concepts, factories own
      initial state, and variants require their own data? Check record construction, conditional
      nullability, meaningful policies and the origin of reported statuses. See
      [explicit construction](engineering-guidelines.md#explicit-domain-construction),
      [data and policy](engineering-guidelines.md#cohesive-data-and-explicit-policy) and
      [nullability](engineering-guidelines.md#treat-nullability-as-a-contract).
- [ ] **R3 — Responsibilities and ownership.** Do capability names match their work? Check large
      DI constructors, handwritten partial classes, forwarding wrappers, feature dependency direction
      and persisted-model ownership. A generic engine must support another registered provider without
      a new kind switch. See [responsibilities](engineering-guidelines.md#readable-code-and-focused-responsibilities)
      and [module boundaries](module-boundaries.md).
- [ ] **R4 — Contracts and transport.** Does each operation admit only possible outcomes and keep
      its inputs in an operation-specific request? Check the three-parameter limit, closed switches
      versus open extension boundaries, HTTP enum strings/numeric rejection, endpoint ownership and
      canonical problem mappings. See [contracts](engineering-guidelines.md#cross-feature-contract-parameter-limit),
      [failures](engineering-guidelines.md#expected-failures) and
      [request validation](engineering-guidelines.md#request-validation).
- [ ] **R5 — Mongo and stable formats.** Check injected typed collections, named filter helpers,
      concrete BSON mappings, polymorphic queries and justified raw BSON boundaries. Inspect each
      exception's scope and reason. Check owner-owned key builders and shared declaration paths;
      preserve formats. Migrations must remain frozen and registered, without duplicate runtime index
      definitions. See [typed APIs](engineering-guidelines.md#prefer-strongly-typed-apis),
      [Application](module-boundaries.md#application) and [Migrations](module-boundaries.md#migrations).
- [ ] **R6 — Authority and side effects.** Follow tenant, ownership and actor authority through
      every changed path. Check session ownership, real conditional fences, atomic mutation/outbox,
      retries, cancellation, crash replay, cleanup and no-op side effects. Events must retain tenant
      metadata and exclude Secret values and credentials; external side effects stay outside Mongo
      transactions. Check native resource ownership and manifest/runtime behavior when Kubernetes
      paths change. External observations must not be confused with accepted configuration or
      authorize stale writes. For UI changes, include
      tenant/role races, drafts and callback lifetime. Use [multitenancy](multitenancy.md),
      [transaction references](agent-delivery.md#task-brief-and-canonical-implementations) and the
      applicable focused skills and feature docs.
- [ ] **R7 — Regressions and documentation.** Does each regression fail for the intended reason?
      Check specific failure assertions, real Mongo schema/index prerequisites, current serialization,
      alternate mutation paths and production registration. Feature docs and examples must describe
      the actual behavior. See [dependency evidence](engineering-guidelines.md#verify-uncertain-dependency-behavior),
      [testing](testing.md) and [documentation ownership](README.md#where-documentation-belongs).
- [ ] **R8 — Scope and delivery.** Are findings fixed in separate commits and user-facing review
      based on committed code? Verify exact current test evidence, unresolved risks, approved scope
      and authorization for the next Git action. Historical success is not current evidence. See
      [delivery and review fixes](agent-delivery.md#review-record-and-review-fixes) and the
      [publication gate](agent-delivery.md#local-and-ci-gate-profiles).

## Automated evidence and its limits

Use the [actual diagnostic catalog](agent-delivery.md#automated-rules) and checks present in the
reviewed tree. Report commands, scope, results and source identity. Do not claim an analyzer exists
because a policy mentions the rule, or rerun a test manually merely to tick a second box.

- **Formatting and declarations:** `.editorconfig`, the changed-file formatter and `POLE007`
  cover their configured syntax/whitespace. Logical spacing, useful names, readability and wrapping
  that a formatter does not enforce remain R1 checks.
- **Construction and state:** `POLE003`–`POLE005` and `POLE009`–`POLE012` cover their supported
  factory, collection, state, record, model-placement, constructor and switch shapes. Domain/BSON
  tests supply behavioral evidence. Cohesion and truthful status mapping remain R2 checks.
- **Boundaries and contracts:** architecture suites cover project roles, dependencies, endpoint
  layout and the contract parameter limit. They do not prove a provider decomposition or an
  operation's set of possible results is well designed; R3/R4 still apply.
- **Mongo:** `POLE001` and `POLE008` cover adapter access and named filters. `POLE013` checks
  typed field arguments in production C# filter, update, sort and index builders. `POLE014`
  and semantic F# architecture checks cover raw BSON types with scoped exceptions. Serializer
  mappings, query semantics and exception reasons still need R5 review; passing one diagnostic
  is not proof of the whole area.
- **Transport, tenant and transactions:** use actual HTTP enum, tenant-isolation, serialization,
  rollback, concurrency and recovery regressions for the changed boundary. Inspect the assertions
  and production composition; a passing test name does not establish coverage.
- **Publication:** `quality_gate.py run`, `record-review` and `verify` bind the complete local
  checks and review to the current source. Follow their [delivery requirements](agent-delivery.md);
  focused or structural checks are development evidence, not publication evidence.

## Compact review record

Store the complete record in ignored task notes or a review artifact. A short handoff can link it.
For documentation-only changes, explain the runtime areas that are not applicable and record
structural/link validation; publication still requires the full gate.

```text
Target: request/scope; base SHA; head SHA; working-tree state
Automated evidence: commands, current results, report paths and missing checks
Manual dispositions: R1–R8, with inspected evidence or a reason for non-applicability
Findings: priority; file/line; canonical rule; failure mechanism; proposed correction
Exceptions and unknowns: justification, scope and remaining readiness impact
Conclusion: unresolved findings or explicitly none; authorization needed for next action
```

Deferred proposals and historical task commands are not new rules. Revisit them only through an
explicit scope decision. A review record does not grant permission to commit, push, open a PR or
merge.
