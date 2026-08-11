# Multitenancy

Pole uses tenant-scoped, shared-storage multitenancy. A tenant is the top-level workspace boundary for users and resources. Each resource group belongs to exactly one tenant, while Kubernetes isolation and physical placement are represented separately by `IsolationScope` and `RuntimeCell`.

The current implementation uses:

* Keycloak for authentication and user identity.
* Pole MongoDB records for users, tenants, tenant membership, tenant roles, and resource ownership.
* The `X-Pole-Tenant-ID` request header to select the active tenant for resource operations.
* A shared Mongo database with `TenantId` fields on tenant-owned documents.

## Model

`Tenant` records are stored in the `Tenants` collection. A tenant contains its member list inline:

* `Id`
* `Name`
* `CreatedAtUtc`
* `Members`

Each `TenantMember` stores:

* `TenantId`
* `UserId`
* `Email`
* `Role`

`PoleUser` records are stored in the `Users` collection and use the Keycloak user id as `Id`. A user also has a `DefaultTenantId`. Creating a Pole user creates a Keycloak user first, then creates the Pole user record and a personal default tenant in one Mongo transaction.

Tenant roles are ordered by capability:

* `Viewer`: read tenant resources.
* `Member`: read and write tenant resources.
* `Admin`: member capabilities plus member management.
* `Owner`: admin capabilities plus tenant management.

The platform admin role is separate from tenant roles. Pole checks for the `pole-platform-admin` role through the `roles` claim and uses it for platform-level actions such as creating tenants and users.

## Request Flow

All `/api/v1` routes require authentication. Tenant-owned resource routes also require an active tenant selected by header:

```http
X-Pole-Tenant-ID: <tenant-id>
Authorization: Bearer <access-token>
```

The tenant middleware runs after authentication and before endpoint execution. It applies to:

* `/api/v1/resource-groups`
* `/api/v1/web-services`
* `/api/v1/redis-services`
* `/api/v1/postgres`
* `/api/v1/domain-names`
* `/api/v1/search`

For those routes, the middleware:

1. Reads `X-Pole-Tenant-ID`.
2. Loads the tenant from Mongo.
3. Looks for the current Keycloak subject in the tenant member list.
4. Allows platform admins even when they are not tenant members.
5. Stores a `TenantContext` with tenant id, tenant name, role, and platform-admin status.

If the header is missing, every request marked with `ITenantReadOperation` or
`ITenantWriteOperation` fails validation. This also applies to platform
administrators: elevated authorization bypasses tenant membership checks, but
it does not select a tenant. A platform administrator must provide
`X-Pole-Tenant-ID` when using a normal tenant-owned resource endpoint.

Global fleet operations must use an explicit platform endpoint and must not be
implemented by treating a missing tenant id as an empty MongoDB filter. Internal
workers may still scan tenant-owned collections globally when their workflow
requires it, but events and mutations remain fenced by the document tenant id.

## Resource Scoping

Tenant-owned domain documents include `TenantId`:

* `ResourceGroup`
* `IsolationScope`
* `RuntimeCell`
* `WebService`
* `Redis`
* `PostgresCluster`
* `TenantDomainZone`
* `HostnameBinding`
* `SearchDocument`
* `SearchSourceDocument`
* `OutboxMessage`

Handlers mark tenant-scoped requests with one of:

* `ITenantReadOperation`
* `ITenantWriteOperation`
* `ITenantCreateOperation`

`TenantAuthorizationBehavior` uses these marker interfaces to enforce role
checks and require explicit tenant selection. Handlers take tenant id from the
request and must combine resource identity filters with tenant filters.
`Builders<T>.Filter.ForTenant(request.TenantId)` rejects a missing tenant id
instead of producing an unscoped filter.

When creating a resource that references another resource, the referenced resource must be loaded from the same tenant. For example, web service, Redis, and Postgres creation must validate that the target resource group has the same `TenantId` as the active tenant.

Hostname ownership is intentionally unique across tenants. Tenant filters protect zone and binding
access, while a global canonical-hostname index prevents two tenants or deployment targets from
claiming the same DNS name.

## Kubernetes Isolation

Every resource group owns one default `IsolationScope`, created atomically with the group.
Additional scopes provide separate isolation boundaries without changing tenant ownership.
A `RuntimeCell` represents one scope placed in one Kubernetes deployment target and is the
authoritative owner of that target's namespace.

Runtime-cell namespace names are generated from the configured prefix, tenant id, and full
isolation-scope id:

```text
<prefix>-t-<tenant8>-is-<scope24>
```

The normalized prefix is at most 24 characters, and the complete name is a DNS label of at
most 63 characters. The same scope therefore receives the same namespace name in different
clusters. Namespace identity is still cluster-local because each runtime cell records its
deployment target.

Runtime-cell namespaces are labeled with:

* `pole.io/tenant-id`
* `pole.io/resource-group-id`
* `pole.io/isolation-scope-id`
* `pole.io/runtime-cell-id`
* `pole.io/deployment-target-id`

All scope and runtime-cell queries combine tenant, resource-group, and object identity.
Events carry `EventCore.TenantId`; consumers never depend on ambient HTTP tenant context.

WebService, Redis, and PostgreSQL persist their workload placement in `ResourceDeployment`.
Functions remains the next adopter. New placement code must use `IResourcePlacementService`; see
[workload placement](workload-placement.md).

## API Surface

Tenant and user control-plane endpoints:

* `GET /api/v1/tenants`: returns tenants where the current user is a member, including role and default-tenant flag.
* `POST /api/v1/tenants`: creates a tenant owned by the current user; requires platform admin policy.
* `GET /api/v1/tenants/{tenantId}/members`: lists members for a tenant.
* `POST /api/v1/tenants/{tenantId}/members`: adds a member.
* `PUT /api/v1/tenants/{tenantId}/members/{memberId}`: changes a member role.
* `DELETE /api/v1/tenants/{tenantId}/members/{memberId}`: removes a member.
* `POST /api/v1/users`: creates a Keycloak user, Pole user, and personal default tenant.

Tenant member endpoints are protected by mediator authorization:

* Listing members requires tenant membership with `Viewer` or higher.
* Adding, updating, and deleting members requires `Admin` or higher.
* Granting `Owner`, changing an existing `Owner`, or removing an existing `Owner` requires `Owner`.
* Platform admins bypass tenant membership checks but still cannot remove or demote the last tenant owner.

Frontend tenant behavior:

* The SPA loads `GET /api/v1/tenants` after login.
* It stores the active tenant id in `localStorage` under `pole.activeTenantId`.
* It sends `X-Pole-Tenant-ID` on resource API calls.
* It shows the active tenant and role in the top band.

## Adding Tenant-Scoped Features

For a new tenant-owned feature:

1. Add `TenantId` to the domain document.
2. Add `TenantId` to create/update/delete events through `EventCore.Create(tenantId)`.
3. Add a non-nullable `[FromHeader(Name = TenantHeaders.TenantId)]` member to
   request models.
4. Implement the correct tenant marker interface on request models.
5. Validate all cross-resource references with both id and tenant id.
6. Filter reads, updates, deletes, and consumer lookups by tenant id.
7. Include `TenantId` in search projections, outbox messages, and any denormalized read model.
8. Add tests for same-id or same-name resources in two tenants, cross-tenant reference rejection, and missing or invalid tenant headers.
