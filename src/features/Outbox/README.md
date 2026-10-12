# Outbox delivery

Outbox owns durable event writes and at-least-once publication. Feature persistence stores
its mutation and `OutboxWrite` records in the same Mongo transaction. The processor preserves
pending-message CAS, capped retries, structural abandonment and bounded terminal cleanup.

## Dispatch configuration

`Outbox:ProcessingCronExpression` keeps the recurring Hangfire recovery schedule (default:
every minute). Setting `Outbox:ProcessingTriggerInterval` to `00:00:01` additionally enables
prompt background dispatch on each API replica. Pole.Server configures this one-second interval.
The component's interval remains optional; slice hosts without Hangfire storage leave it unset.

The background loop directly invokes one scoped processor batch and awaits its completion.
`PeriodicTimer` coalesces elapsed ticks, so slow publication does not accumulate persistent
Hangfire jobs or overlapping local batches. The loop and recurring job acquire the same
Hangfire distributed lock. Background contenders attempt acquisition without waiting and
leave work to the current owner. Storage failures are retried on the next tick; the cron
schedule remains recovery coverage.

Hangfire.Mongo locks use thread-local nesting state. The direct dispatcher therefore acquires
the lock, waits for asynchronous publication, disposes the scope and releases the lock on the
same worker thread. Shutdown cancellation reaches publication and persistence. Pending messages
remain available when shutdown happens before their published transition; a successful publish
followed by interrupted persistence can still be repeated, as required by at-least-once delivery.

## Verification

`tests/Pole.Tests.Integration/Outbox` covers interval validation, disabled-host compatibility,
coalesced ticks, failure recovery and cancellation. Its Mongo-backed dispatch tests verify
contention between two Hangfire storage instances, shared cron locking, release after an async
publish and absence of queued dispatch jobs. Existing `Database/OutboxProcessorLifecycleTests`
and persistence tests retain the publication, retry, CAS and transaction checks.

See [observability](../../../docs/observability.md#event-and-outbox-flow) and
[testing](../../../docs/testing.md) for the shared operational and test contracts.
