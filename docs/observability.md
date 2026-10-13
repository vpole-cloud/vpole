# Observability

Pole carries two identifiers through the request and event flow:

- `TraceId` is the OpenTelemetry/W3C trace identifier. It is the technical ID used by tracing backends to connect spans.
- `CorrelationId` is the application workflow identifier. It is useful for support and debugging because clients can provide it with `X-Correlation-ID`.

The public HTTP contract is:

```http
X-Correlation-ID: <client-provided-or-generated-id>
X-Trace-ID: <current-opentelemetry-trace-id>
```

Clients may send `X-Correlation-ID`. Pole returns both headers on every API response. The API does not accept `X-Trace-ID`; trace continuity should come from OpenTelemetry/W3C propagation such as `traceparent`.

## Request Flow

`TraceContextMiddleware` runs near the start of the ASP.NET Core pipeline.

It:

- reads and validates `X-Correlation-ID`;
- generates a correlation ID when the incoming value is missing or unsafe;
- reads the current OpenTelemetry `Activity.TraceId`;
- stores both IDs in `TraceContext`;
- adds both IDs to the logging scope;
- writes `X-Correlation-ID` and `X-Trace-ID` to the response.

`TraceContext` uses `AsyncLocal`, so code called later in the same async operation can read the current IDs without adding tracing parameters to every method.

## Why AsyncLocal

`AsyncLocal` stores a value for the current async execution flow. It is safe for concurrent HTTP requests because each request has its own async context.

This is different from a normal static field, which would be shared by all requests and would let one request overwrite another request's IDs.

Pole uses `AsyncLocal` only as in-process glue. It is not durable storage. When work crosses process or time boundaries, the IDs are also written to event payloads, outbox records, and RabbitMQ headers.

## Event And Outbox Flow

Integration events implement `IPoleEvent`, which exposes `EventCore`. Application event factories
create `EventCore` from the current tenant and trace context before the event is stored. F# event
factory modules initialize the same values explicitly.

When an event is stored in the outbox, Pole writes the IDs in two places:

- inside the serialized event payload;
- on the `OutboxMessage` document as top-level `TraceId` and `CorrelationId` fields.

This duplication is intentional. The payload keeps the event self-contained, while the top-level fields make outbox inspection, retries, and replay debugging easier.

Each outbox document has an explicit delivery state:

- `Pending` stores the attempt count, the next due time, and the last sanitized publish error.
- `Published` stores the successful publish time.
- `Abandoned` is reserved for permanent structural failures such as an unknown persisted event name or malformed payload.

Broker and transport failures remain pending and retry with capped exponential backoff. Pole
delivers events at least once: a process crash after RabbitMQ accepts a publish but before
Mongo records `Published` can publish the same event again. Consumers must therefore remain
idempotent, and no global or per-aggregate event ordering is guaranteed.

Every persisted event has one required, unique `[EntityName]` in the form `owner/vN/name`, where
`N` is a positive integer. For example, Redis publishes
`pole.redis/v1/redis-service-created`. The same identity names the broker topology and the outbox
event type; Pole has no separate persisted-name field or legacy read aliases. The configured event
catalog is validated at startup instead of falling back to CLR type names.

## RabbitMQ Flow

When the outbox processor publishes an event, it restores `TraceContext` from the outbox record and event payload.

MassTransit publish filters then copy both IDs into message headers:

```text
X-Trace-ID
X-Correlation-ID
```

MassTransit consume filters restore `TraceContext` from headers first, then fall back to the event payload. Consumer logs therefore use the same IDs as the original API request.

Derived messages should publish while `TraceContext` is set. For example, the DNS delete filter restores the consumed message context before publishing its follow-up delete event.

The `Pole.Outbox` meter reports bounded delivery counters, publish duration, retry delay,
cleanup counts, pending/abandoned counts, and oldest-pending age. Metric tags use stable event
names, outcomes, and failure codes; they do not contain tenant IDs, message IDs, trace IDs,
correlation IDs, exception messages, or payload data.

## Debugging

Use `CorrelationId` when tracing a user-visible workflow across API logs, outbox records, and consumers.

Use `TraceId` when opening the same operation in an OpenTelemetry-compatible tracing backend.

## Realtime and native observation metrics

ServiceDefaults exports `Pole.Realtime`, `Pole.ResourceDeploymentObservations`, `Pole.StorageObservations` and
`Pole.KubernetesWatch`, alongside the existing `Pole.Outbox` meter. OTLP export uses the existing
`OTEL_EXPORTER_OTLP_ENDPOINT` configuration. The new measurements use registered/normalized
bounded topics, scopes, outcomes, recovery categories and a controlled server/client source.
They exclude tenant/resource/target/connection/session/user IDs, native resource versions,
selectors, payloads, URLs, credentials and raw exception messages.

| Instrument family | Meaning |
| --- | --- |
| `pole.realtime.delivery.attempts`, `.recipients`, `.duration` | Server publish outcomes and authorized send recipients; duration includes authorization and SignalR dispatch. |
| `pole.realtime.connections.transitions`, `.subscriptions.transitions`, `.coverage.transitions` | Server lifecycle/coverage changes; healthy identical heartbeat coverage is not another transition. |
| `pole.realtime.reconnects`, `pole.realtime.fallbacks` | Bounded optional client-reported transitions (`source=client`); server uncovered coverage uses `source=server`. |
| `pole.observation.reads`, `.sources.transitions`, `.samples`, `.sample.duration` | Shared/live/unavailable read selection, source gain/loss and sample outcomes/duration. |
| `pole.storage.observation.reads`, `.sources.transitions`, `.samples`, `.sample.duration` | Admin storage shared/live/unavailable reads, accepted source transitions and committed sample outcomes/duration; read kind is coherent/classes/volumes/claims. |
| `pole.kubernetes.watch.outcomes` | LIST/open/EOF/410/malformed/stalled-headers/cancelled/unavailable recovery categories. |
| `pole.outbox.pending.oldest_age` | Existing age of the oldest pending outbox event, refreshed by processing/cleanup passes. |

These counters are operational diagnostics, not durable audit events or a delivery acknowledgement.
An authorized recipient and a successful SignalR send do not prove browser processing. Client
reports are best-effort and rate limited; missing reports are not zero transitions. Measure
commit-to-visible latency and quiet HTTP reduction with browser journals over a stated sample and
baseline. Pod readiness, declared topology and application connectivity remain separate signals.
