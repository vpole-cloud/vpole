# Pole Functions

Pole Functions runs small event-driven applications from pre-built OCI images. A Function App is a
tenant-owned control-plane resource with immutable revisions, one or more named handlers, and a
scaling policy. Pole currently supports HTTP and timer triggers and can scale active revisions to
zero.

This document is the canonical entry point for the Functions feature. The [.NET SDK and Agent
contract](https://github.com/lsbV/Pole.Functions) documents how user code runs inside an image. The
deeper documents describe the [runtime architecture](docs/runtime-architecture.md) and [revision
lifecycle](docs/revision-lifecycle.md).

## Supported scope

| Area | Current behavior |
| --- | --- |
| Packaging | A pre-built OCI image that implements the Pole Agent protocol |
| Official runtime | .NET SDK and ASP.NET Core Agent host |
| Triggers | HTTP and UTC cron timer |
| Delivery | Timer invocations are at-least-once; HTTP execution avoids an automatic retry after an ambiguous start |
| Revisions | Immutable revision specs with validated promotion and draining of the previous revision |
| Scaling | `minInstances`/`maxInstances`, app and handler concurrency targets, and scale-to-zero |
| Topology | Multiple `Pole.Server` instances coordinated through Orleans |
| Usage | Per-hour invocation, attempt, outcome, skip, and execution-duration buckets |

The current implementation is experimental and API contracts may still change.

## User workflow

1. Build a web application with `Pole.Functions.Sdk` and register named handlers.
2. Publish that application as an OCI image. The first version does not build user source code.
3. Create a Function App through the control-plane API with the image, resources, scaling policy,
   and trigger definitions. Handler names, kinds, and concurrency limits must match the image.
4. Pole creates a Kubernetes Deployment and ClusterIP Service for the revision, starts one instance
   for validation when necessary, and reads Agent metadata.
5. After the revision becomes ready, Pole promotes it atomically and starts routing HTTP and timer
   invocations to it.

A minimal user application looks like this:

```csharp
builder.Services
    .AddPoleFunctions(
        builder.Configuration.GetSection(PoleFunctionsOptions.SectionName))
    .AddHttpHandler("hello", (request, context, cancellationToken) =>
        ValueTask.FromResult(PoleHttpResponse.Text("Hello")))
    .AddTimerHandler("heartbeat", (timer, context, cancellationToken) =>
    {
        Console.WriteLine("Heartbeat at {ScheduledForUtc:O}", timer.ScheduledForUtc);
        return ValueTask.CompletedTask;
    });

var app = builder.Build();
app.MapPoleFunctionsAgent();
app.Run();
```

See the [sample
application](https://github.com/lsbV/Pole.Functions/tree/main/samples/Pole.Functions.Sample) and [SDK
documentation](https://github.com/lsbV/Pole.Functions) for the complete Agent contract,
authentication, concurrency, shutdown, and attempt semantics.

## Function App specification

The create request describes one initial immutable revision:

- `image`: registry server, image name, and tag;
- `configuration`: plain environment variables and Kubernetes Secret references;
- `resources`: CPU and memory requests and limits;
- `scaling`: minimum and maximum instances, target and hard concurrency per instance, scale-down
  delay, and activation timeout;
- `handlers`: a unique name, per-handler concurrency limit, and an HTTP or timer trigger.

An HTTP trigger defines a relative route, allowed methods, and `Public` or `Internal` exposure.
Runtime traffic uses:

```text
/functions/{functionRoute}/{handlerRoute}
```

`functionRoute` is a normalized, cluster-wide reservation because it is part of the shared gateway
URL; two tenants cannot claim the same value.

The gateway listens on separate public and internal ports and only matches handlers with the same
exposure. Route parameters such as `users/{id}` are supported; ambiguous method/route combinations
are rejected when the revision is created.

Exposure selects a network listener; it is not end-user authentication. The current gateway does
not authenticate Function callers and deliberately does not forward `Authorization`, `Cookie`,
`Host`, or Pole tenant headers. Other request headers must be in the gateway's configured allowlist.

A timer trigger defines a cron expression, `Allow` or `Forbid` overlap policy, `Skip` or `FireOnce`
misfire policy, and retry attempts/backoff. Clients should send both policies explicitly; omitted
enum values currently bind as `Allow` and `Skip`. Timer schedules use UTC only.

Updating a Function App creates a new revision. It never mutates the active revision in place.

## Control-plane API

All endpoints require authentication and are tenant-scoped. Normal tenant clients include
`X-Pole-Tenant-ID` as described in the [multitenancy documentation](../../../docs/multitenancy.md).

| Method | Route | Purpose |
| --- | --- | --- |
| `POST` | `/api/v1/function-apps` | Create an app and its first revision |
| `GET` | `/api/v1/function-apps` | List apps |
| `GET` | `/api/v1/function-apps/{id}` | Read an app and current revision details |
| `PUT` | `/api/v1/function-apps/{id}` | Create a pending revision |
| `DELETE` | `/api/v1/function-apps/{id}` | Start app deletion |
| `GET` | `/api/v1/function-apps/{id}/revisions` | List immutable revisions |
| `GET` | `/api/v1/function-apps/{id}/invocations` | Inspect durable timer invocations |
| `POST` | `/api/v1/function-apps/{id}/invocations/{invocationId}:retry` | Retry an eligible failed timer invocation |
| `GET` | `/api/v1/function-apps/{id}/usage` | Read hourly usage buckets |

Create and update return `202 Accepted` because Kubernetes provisioning happens asynchronously
through the outbox and a MassTransit consumer. Delete also returns `202 Accepted` after persisting
the deletion intent and event; runtime cleanup for that event is not implemented yet.

## Module boundaries

- [Functions.Domain](Functions.Domain/Functions.Domain.csproj) owns Function Apps, revision state,
  trigger specifications, durable timer/invocation state, and metering models.
- [Functions.Application](Functions.Application/Functions.Application.csproj) owns tenant-scoped
  endpoints, validation, Mongo transactions, quota admission, lifecycle transitions, migrations,
  and outbox events.
- [Functions.Events](Functions.Events/Functions.Events.csproj) owns lifecycle event contracts and
  immutable revision snapshots.
- [Functions.Runtime](Functions.Runtime/Functions.Runtime.csproj) owns Kubernetes reconciliation,
  the HTTP gateway, timers, Agent calls, endpoint discovery, Orleans coordination, scaling, and
  usage aggregation.
- [Pole.Functions.Protocol](https://www.nuget.org/packages/Pole.Functions.Protocol) owns the
  language-neutral Agent HTTP/JSON contract.
- [Pole.Functions.Sdk](https://www.nuget.org/packages/Pole.Functions.Sdk) is the official .NET
  handler API and in-process Agent runtime. Its source, sample, and tests live in the separate
  MIT-licensed [Pole.Functions repository](https://github.com/lsbV/Pole.Functions).

Function application data is stored in the main Pole Mongo database and scoped by `TenantId`.
Cross-instance runtime coordination is stored separately by the Orleans Mongo provider. See
[runtime architecture](docs/runtime-architecture.md#state-and-storage) for the ownership split.

Tenant quota admission reserves the Function App count, maximum workload instances, and CPU/memory
requests at `maxInstances`; during a rollout it temporarily admits the larger old/new allocation.
Invocation usage is metering, not an invocation-count quota. See [tenant
quotas](../../../docs/quotas.md) for the shared quota model.

Timer meter facts commit with durable invocation transitions. For HTTP, Pole requires the admitted
fact before dispatch but writes started/completed facts best-effort after user code may have begun;
usage is therefore operational telemetry rather than billing-grade accounting.

## Dependencies and configuration

The feature uses:

- MongoDB for Function resources, timers, invocations, usage, Orleans membership, and grain state;
- RabbitMQ and the Pole outbox for asynchronous revision provisioning and deletion events;
- Kubernetes Deployments, Services, EndpointSlices, probes, and scale subresources;
- Orleans, co-hosted in every `Pole.Server` instance, for distributed ownership and coordination;
- an RSA signing key for authenticated Pole-to-Agent protocol calls.

Function runtime options live under `Functions:Gateway`, `Functions:Coordination`,
`Functions:Scaler`, `Functions:Timers`, `Functions:Orleans`, and
`Functions:AgentAuthentication`. Bound gateway, coordination, scaler, timer, and Orleans options are
validated during startup. The Agent authentication section is intentionally opt-in; without it the
Function gateway returns `function_runtime_unavailable`, and the provisioner, scaler,
timer/dispatcher, usage aggregation, and HTTP runtime services are not registered. Agent
authentication secures Pole-to-Pod protocol calls; it does not authenticate callers of a user
Function.

Operational details, ports, storage, RBAC, and failure behavior are documented in [runtime
architecture](docs/runtime-architecture.md#operations).

## Current limitations

- Pole accepts a ready image; build-from-source integration is not part of Functions yet.
- The public API stores an image tag and does not currently resolve it to an immutable digest.
- The create API does not expose an image pull-secret field, even though the internal revision model
  can represent one.
- HTTP and timer are the only triggers. RabbitMQ is used by Pole internally but is not a Function
  trigger.
- The official SDK and sample are .NET-only; a project template is not implemented. The protocol
  project is language-neutral, but another language needs its own compatible Agent implementation.
- HTTP request and response bodies are buffered and limited to 1 MiB by default. Streaming,
  WebSockets, and arbitrary header forwarding are not supported.
- Public/internal exposure is a network boundary only. Function caller authentication and forwarding
  standard `Authorization` or cookie credentials are not implemented.
- Timer execution is at-least-once, so handlers must be idempotent or deduplicate by invocation or
  occurrence identity.
- Invocation usage is observable through hourly buckets but is not currently enforced as a request
  or monthly billing quota.
- HTTP attempt state lives in one Agent process. Pole keeps reserve, execute, and status calls on
  the selected Pod and does not retry user code after an ambiguous start.
- Cold starts add activation and image startup latency. A request fails when the configured
  activation or execution deadline is exceeded.
- Delete records and publishes the deletion request, but Kubernetes and Mongo cleanup for that event
  is not implemented yet. Suspend/resume also has no public API.
- Scaling is owned by the Pole runtime; KEDA, Knative, and Kubernetes HPA are not integrated.
- There is no Functions UI yet; the feature is exposed through the API.

## Tests

Integration coverage is split between [Function App control-plane
tests](../../../tests/Pole.Tests.Integration/FunctionApps) and [runtime, SDK, and protocol
tests](../../../tests/Pole.Tests.Integration/Functions). Kubernetes manifests are tested without
requiring a live cluster. General server startup can be exercised with the [Aspire local
workflow](../../../docs/aspire-local-development.md); Function Agent signing keys still have to be
configured explicitly.
