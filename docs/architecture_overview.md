## Architecture & stack

Pole's cross-cutting code design conventions are documented in [engineering guidelines](engineering-guidelines.md).

### Backend

* **.NET 10 / ASP.NET Core**
* Aspire for local development
* Custom control-plane APIs (not kubectl wrappers)
* Database-backed Kubernetes deployment targets with encrypted remote credentials and a zero-configuration local bootstrap; see [deployment targets](deployment-targets.md)
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

### Workload placement

Tenant organization and Kubernetes placement are separate concepts. A `ResourceGroup`
owns one default `IsolationScope`; a `RuntimeCell` materializes a scope in one
`DeploymentTarget` and owns that cluster's Kubernetes namespace. Runtime cells are created
lazily through a caller-owned Mongo transaction, then reconciled through the durable outbox.

WebService, Redis, and PostgreSQL persist their logical-to-physical binding in
`ResourceDeployment`, including the target, runtime cell, namespace, resolved storage/ingress,
and an immutable compute snapshot. Functions remains the next adopter. See [workload
placement](workload-placement.md) for ownership, lifecycle, deletion, and reconciliation
boundaries.

Platform-managed [ComputeProfiles](../src/features/ComputeProfiles/README.md) separate portable
CPU, memory, extended-resource, and scheduling policy from logical resource specifications. A
profile is resolved against the tenant, resource type, and deployment target before mutation;
the resulting snapshot is fenced and stored with the deployment so later catalog edits do not
silently alter running workloads.

### Sensitive values

Pole-held sensitive values use versioned envelope encryption with authenticated tenant/platform,
object, field, and value-version context. The default `local-key-file` provider is fully
self-hosted and keeps KEKs outside MongoDB and normal application configuration. See
[envelope encryption operations](envelope-encryption.md) for the threat boundary, bootstrap,
backup/restore, multi-replica, and rotation procedures.

### Quotas

Tenant quota admission uses feature-owned typed profiles with shared CPU, RAM, and workload definitions, then commits quota usage together with the resource mutation and outbox records. See [tenant quotas](quotas.md) for the model and extension workflow.

### Observability

Pole carries `TraceId` and `CorrelationId` through HTTP, logs, outbox records, RabbitMQ headers, and consumers. See [observability](observability.md) for the full flow.
