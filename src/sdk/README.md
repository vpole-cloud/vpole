# Pole Functions .NET SDK

This directory contains the first executable version of the Pole Function Agent contract.

This page owns the user-code and Agent protocol contract. See the [Pole Functions feature
documentation](../features/Functions/README.md) for the control-plane resource, provisioning,
revisions, triggers, scaling, and multi-instance runtime architecture.

- `Pole.Functions.Protocol` contains language-neutral HTTP/JSON DTOs and protocol constants. It has no dependency on ASP.NET Core or Pole control-plane models.
- `Pole.Functions.Sdk` contains the .NET handler API and the in-process Agent runtime.

## User application

```csharp
builder.Services
    .AddPoleFunctions(
        builder.Configuration.GetSection(PoleFunctionsOptions.SectionName))
    .AddHttpHandler("hello", (request, context, cancellationToken) =>
        ValueTask.FromResult(PoleHttpResponse.Text("Hello")));

var app = builder.Build();
app.MapPoleFunctionsAgent();
app.Run();
```

The SDK supports delegate handlers and scoped DI handlers through `IPoleHttpFunctionHandler` and
`IPoleTimerFunctionHandler`. One image can register multiple handlers.

## Attempt lifecycle

The coordinator must send reserve, execute, and status requests to the same Agent instance.
Attempt state is kept in memory and is not shared through a Kubernetes Service.

1. `POST /_pole/v1/attempts:reserve` atomically acquires the app and handler concurrency slots without running user code.
2. `POST /_pole/v1/attempts/{attemptId}:execute` atomically starts user code once and immediately returns `202 Accepted`.
3. `GET /_pole/v1/attempts/{attemptId}?waitUpToMs=...` observes or long-polls the result. Cancelling this poll does not cancel user code.

Repeated reserve and execute requests for the same immutable attempt are idempotent. A changed identity or payload is rejected. Reservation leases and terminal attempt retention are bounded.

## Security and shutdown

Agent protocol endpoints deny requests by default. Health endpoints remain available for probes.
The SDK registers `PoleFunctionJwtAgentAuthenticator` by default. Production applications must bind
the complete `PoleFunctions` configuration section injected by Pole so tenant identity, Pod target,
trusted keys, and concurrency settings are not dropped. Its authorization context includes the
operation and attempt identity, allowing the signed runtime token to be bound to tenant, revision,
handler, invocation, and attempt claims. Use `UseAgentAuthenticator<T>()` only for a custom
authentication scheme. The sample permits unauthenticated requests only in the Development
environment.

During shutdown the Agent becomes unready, rejects new executions, and waits for already accepted handlers. Handler deadline cancellation is cooperative because .NET cannot safely terminate arbitrary in-process user code. The maximum accepted invocation deadline is bounded by configuration; platform termination remains the final boundary for a non-cooperative handler.
