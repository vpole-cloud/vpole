# Multitenancy

Pole uses tenant-scoped, shared-storage multitenancy. A tenant is the top-level workspace boundary for users and resources. Resource groups remain the operational boundary that maps to Kubernetes namespaces, but each resource group belongs to exactly one tenant.

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

If the header is missing, the request reaches the mediator without a `TenantContext`. Tenant-scoped create operations fail because they require a concrete tenant id. Platform admins can run tenant-wide read/update operations without a tenant context, so handlers must still be careful to use tenant filters consistently.

## Resource Scoping

Tenant-owned domain documents include `TenantId`:

* `ResourceGroup`
* `WebService`
* `Redis`
* `PostgresCluster`
* `DnsRecord`
* `SearchDocument`
* `SearchSourceDocument`
* `OutboxMessage`

Handlers mark tenant-scoped requests with one of:

* `ITenantReadOperation`
* `ITenantWriteOperation`
* `ITenantCreateOperation`

`TenantAuthorizationBehavior` uses these marker interfaces to enforce role checks and to set the current `TenantOperationScope`. Handlers should still take tenant id from the request and must combine resource identity filters with tenant filters. Prefer `Builders<T>.Filter.ForTenant(request.TenantId)` or explicit `TenantId` predicates.

When creating a resource that references another resource, the referenced resource must be loaded from the same tenant. For example, web service, Redis, and Postgres creation must validate that the target resource group has the same `TenantId` as the active tenant.

Domain names are intentionally checked globally during web service creation. Even though DNS records carry `TenantId`, the actual host name is cluster-wide and ingress-wide, so two tenants cannot claim the same domain.

## Kubernetes Isolation

Resource groups own Kubernetes namespaces. Namespace names are generated from the configured prefix, tenant id, and resource group id:

```text
<prefix>-<tenant-id-prefix>-<resource-group-id>
```

Created namespaces are labeled with:

* `pole.io/tenant-id`
* `pole.io/resource-group-id`

Runtime resources use the namespace stored on the owning resource group. Events carry `EventCore.TenantId`, so consumers can preserve tenant context while creating, updating, deleting, and indexing resources asynchronously.

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
3. Add `[FromHeader(Name = TenantHeaders.TenantId)]` to request models.
4. Implement the correct tenant marker interface on request models.
5. Validate all cross-resource references with both id and tenant id.
6. Filter reads, updates, deletes, and consumer lookups by tenant id.
7. Include `TenantId` in search projections, outbox messages, and any denormalized read model.
8. Add tests for same-id or same-name resources in two tenants, cross-tenant reference rejection, and missing or invalid tenant headers.