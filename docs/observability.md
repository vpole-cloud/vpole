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

Domain events implement `IPoleEvent`, which exposes `TraceId` and `CorrelationId`.

C# events inherit `BaseEvent`, which initializes the IDs from the current `TraceContext`. F# events initialize the same fields when their event factory functions run.

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

Persisted event names come from required, unique `[EntityName]` attributes. Pole validates
the configured event catalog during startup instead of falling back to CLR type names.

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
