# Feature test contract

Use this checklist while planning a Pole feature or material behavior change. Each item must name
the proving test boundary or record a short, specific `N/A` reason. Prefer the cheapest boundary
that can prove the behavior; reserve live Kubernetes and Aspire tests for behavior that lower
layers cannot establish.

## Behavior and ownership

- What is the critical success flow, and what is the cheapest test boundary that proves it?
- Which tenant owns every request, record, event, projection, and external resource?
- Which foreign-tenant probes prove that reads and mutations cannot cross that boundary?
- Which user roles, platform roles, and service tokens are allowed or denied?
- Which defaults and explicit choices must behave differently?

## State and concurrency

- Which mutations and outbox records must commit atomically?
- Which optimistic-concurrency or stale-revision conflicts must be rejected?
- What happens when two callers create, update, retry, or delete the same resource concurrently?
- How are duplicate event delivery and repeated external calls made idempotent?
- Which external side effects occur only after the database transaction commits?

## Failure and recovery

- Which expected failures have stable, typed failure codes?
- Which failures are retryable, and what proves retry, recovery, and stale-work fencing?
- For multi-target work, how is partial failure represented and how is only the failed target
  retried without mutating a healthy target?
- Which timeouts are bounded, and what sanitized diagnostics are retained?
- How do tests prove that secrets, tokens, connection strings, and credentials are not reflected
  in API errors, events, or diagnostics?

## Placement and infrastructure

- What proves default, explicit, multi-target, and topology-aware placement, or why is each `N/A`?
- For Kubernetes resources, which manifest tests prove names, namespaces, labels, selectors, ports,
  storage, ingress, secret references, and scheduling shape?
- Which behaviors require a live cluster rather than a manifest or fake Kubernetes API?
- Which trace and correlation identifiers must cross asynchronous boundaries?

## Cleanup and delivery

- Are disposable resources uniquely owned, deleted with a bounded wait, and verified absent?
- Which `Layer`, `Concern`, `Feature`, and `Scope` traits select each test lane?
- What exact commands and prerequisites reproduce the evidence?
- Which infrastructure-dependent lanes were skipped, and what must be available before claiming
  they pass?

Copy the completed checklist into the issue plan or the feature-owned design document. Keep the
answers beside the decision they justify instead of creating a second source of truth.
