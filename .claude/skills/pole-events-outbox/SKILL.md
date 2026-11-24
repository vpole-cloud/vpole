---
name: pole-events-outbox
description: Implement or review Pole integration events, transactional outbox writes, event consumers, delivery, and trace or tenant propagation.
---

# Pole Events Outbox

Use this skill to keep resource mutations, emitted events, and asynchronous side effects reliable.

Read the Events and Application sections of `docs/module-boundaries.md` and the outbox sections of `docs/observability.md`. Inspect the nearest current mutation plus:

- `src/shared/Events/Abstractions/EventCore.cs`
- `src/features/Outbox/Outbox.Contracts/OutboxWrite.cs`
- `src/features/Outbox/Outbox.Contracts/StoreOutboxWrites.cs`
- `src/shared/Database/MongoTransactionPersistence.cs`
- `src/features/Outbox/Outbox.Application/OutboxProcessorJob.cs`

## Event Shape

- Integration events live in the provider's Events project, implement `IPoleEvent`, and expose `EventCore`.
- Treat published events as historical serialized contracts: preserve existing field names and meanings; version breaking changes.
- Carry stable ids and immutable snapshots consumers need, never provider domain aggregates.
- Create tenant-owned events with `EventCore.Create(tenantId)`.

## Mutation Transaction

- Persist the domain mutation and every corresponding `OutboxWrite` in the same Mongo session and transaction.
- Call `StoreOutboxWrites(session, writes, cancellationToken)` from the feature-owned persistence operation. Use the existing `MongoRunTransaction` or caller-owned session pattern rather than nesting transactions.
- An aborted transaction must publish nothing. Keep Kubernetes, messaging, search, and other external effects outside the transaction and trigger them from consumers after commit.

## Consumers

- Make consumers safe under retries and duplicate delivery.
- Use `context.Message.EventCore.TenantId` for tenant-owned lookups; do not depend on ambient HTTP state.
- Preserve event trace and correlation context when publishing derived messages.
- Do not assume ordering unless persisted workflow state enforces it.

## Observability

- Public HTTP flow uses `X-Correlation-ID` and response `X-Trace-ID`.
- Do not accept client-provided `X-Trace-ID`; use OpenTelemetry/W3C propagation for trace continuity.
- `TraceContext` is in-process glue. Durable boundaries must retain the relevant ids in `EventCore` and the outbox record.
- When debugging a workflow, use `CorrelationId` across API logs, outbox rows, and consumers.

## Tests

- Prove mutation and outbox atomicity, including abort or conflict paths.
- Assert event type plus tenant, trace, correlation, and pending-key metadata when relevant.
- Test consumer idempotency and tenant filters when behavior changes.
- Use the `pole-test-runner` skill to select the focused Mongo, consumer, or observability checks.
