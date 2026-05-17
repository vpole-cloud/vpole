## Architecture & stack

### Backend

* **.NET 10 / ASP.NET Core**
* Aspire for local development
* Custom control-plane APIs (not kubectl wrappers)
* Kubernetes official **C# client**
* **MongoDB**
* **RabbitMQ** (events, outbox patterns)
* **Hangfire + background hosted services**
* OpenTelemetry-based tracing with Pole-level correlation IDs
* Strong focus on:

    * Transactions
    * Event reliability
    * Idempotency
    * Resource-oriented APIs

### Auth

* **Keycloak**
* OIDC / JWT
* Invite-only user model
* Pole-managed tenant and membership records for multitenant authorization

### Frontend

* Vite-based SPA
* Talks only to Pole API
* Clean separation from auth provider

### Multitenancy

Pole uses tenant-scoped shared Mongo collections. Resource APIs select the active tenant with `X-Pole-Tenant-ID`, and tenant-owned documents carry `TenantId`. See [multitenancy](multitenancy.md) for the full model, request flow, and implementation rules.

### Observability

Pole carries `TraceId` and `CorrelationId` through HTTP, logs, outbox records, RabbitMQ headers, and consumers. See [observability](observability.md) for the full flow.
