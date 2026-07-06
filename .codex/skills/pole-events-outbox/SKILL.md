---
name: pole-events-outbox
description: Workflow for implementing or reviewing Pole events, outbox persistence, MassTransit consumers, trace/correlation propagation, and event-driven side effects. Use when editing `IPoleEvent`, `EventCore`, `StoreEventAsync`, `OutboxProcessorJob`, RabbitMQ filters, consumers, DNS reservation events, search projection consumers, or transaction boundaries around resource mutations.
---

# Pole Events Outbox

Use this skill to keep resource mutations, emitted events, and asynchronous side effects reliable.

## First Steps

1. Inspect the relevant feature and nearby implementation before editing.
2. Read `docs/observability.md` for trace and correlation propagation.
3. Inspect `src/shared/Events/Abstractions/EventCore.cs`, `src/shared/Database/IEventDbContext.cs`, and `src/features/Outbox/Outbox.Application/OutboxProcessorJob.cs` before changing event mechanics.

## Event Shape

- Events implement `IPoleEvent` and expose an `EventCore`.
- Tenant-owned resource events use `EventCore.Create(resource.TenantId)` or `EventCore.Create(tenantId)`.
- C# event extension methods live beside application code, such as `WebServicesEventExtensions.cs` and `RedisServicesEventExtensions.cs`.
- F# features use explicit event factory modules, such as `PostgresClusterEventExtensions.fs`.
- Event payloads should carry stable ids and snapshots needed by consumers, not foreign aggregates.

## Mutation Transaction

- Persist the domain mutation and outbox message in the same Mongo transaction with `RunInTransactionAsync`.
- Call `StoreEventAsync(..., session, cancellationToken)` inside that transaction.
- If a command writes multiple event messages that must commit together, use the enumerable overload.
- Keep external side effects out of the database transaction unless there is already an established feature-specific reason. Prefer outbox-driven consumers for Kubernetes, search indexing, and follow-up workflow actions.

## Consumers

- Make consumers idempotent where retries can happen.
- Filter lookups by `context.Message.EventCore.TenantId` for tenant-owned resources.
- Preserve trace context when publishing derived messages. The MassTransit filters and `DeleteDnsRecordFilter<T>` depend on event headers and `EventCore`.
- Do not assume event ordering beyond what the workflow explicitly enforces through persisted state.

## Observability

- Public HTTP flow uses `X-Correlation-ID` and response `X-Trace-ID`.
- Do not accept client-provided `X-Trace-ID`; use OpenTelemetry/W3C propagation for trace continuity.
- `TraceContext` is in-process glue. Durable boundaries must also store ids in event payloads and `OutboxMessage`.
- When debugging a workflow, use `CorrelationId` across API logs, outbox rows, and consumers.

## Tests

- Assert outbox message count and event type for create/update/delete commands.
- Assert `TenantId`, `TraceId`, and `CorrelationId` are stored on outbox messages when relevant.
- Add consumer tests for idempotency and tenant filters when behavior changes.
- Use existing observability tests under `tests/Pole.Tests.Integration/Observability` as the baseline for trace propagation.