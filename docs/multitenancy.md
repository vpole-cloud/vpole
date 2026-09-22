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

Tenant context is declared locally with `TenantEndpointMetadata`, normally on the owning route
group. The middleware runs after authentication/authorization and before request binding. It has
no route-prefix list. The declaration has three modes:

| Mode | Header absent | Header present |
| --- | --- | --- |
| `Required` (default) | 400 validation problem | Resolve and authorize the selection |
| `Optional` | Continue without an active context | Resolve and authorize the selection |
| `None` | Continue without an active context | Ignore the header; it is not an active selection for this operation |

Resolution requires authentication, exactly one ObjectId header, an existing tenant, and tenant
membership or the platform-admin claim. Missing, blank, malformed, repeated, or comma-joined
headers return the same structured 400 validation problem before ASP.NET binds endpoint arguments.
An unknown tenant returns 404; an unjoined tenant returns 403. Uppercase ObjectIds are accepted and
canonicalized to the persisted tenant identity before binding. Unexpected Mongo failures retain the
standard infrastructure exception path.

Use `[FromTenantHeader]` in C# or `[<Pole.ASP.Helpers.FromTenantHeader>]` in F# for the request's
active selection. This is an ASP.NET header-binding attribute with one centrally owned header name;
it is not a service locator or an ambient-context binder. Middleware validates and canonicalizes the
header first. `ITenantReadOperation`, `ITenantWriteOperation`, and `ITenantCreateOperation` retain
the active `TenantId` and role requirements. The Mediator rejects an absent resolved context or a
request whose tenant differs from that context, even for platform admins. Programmatic callers
must establish the context explicitly; a supplied tenant string and admin claim are not a substitute.

The API composition roots apply `ValidateTenantContextDeclarations` to their route groups. This
bounded endpoint convention inspects mapped handler parameter types and fails endpoint construction
when an `ITenantOperation` request lacks a required-context declaration. It covers C# lambdas and
F# `MapMethod` handlers, including nested groups. Features still own their local declaration;
new roots that map tenant endpoints must install the convention too.

### Active selection versus target identity

`ITenantTargetOperation.TargetTenantId` describes a target independently of the active selection.
The supported policies are explicit request interfaces rather than an implicit admin bypass:

- `ITenantMatchingTargetOperation` consumes an active selection and requires its separate target to
  match it. Platform admins cannot override this fence.
- `IPlatformTenantOperation` permits independently addressed targets only for an authenticated
  platform administrator. Quota-policy endpoints use this policy and declare no active context.
  A stale or malformed active header does not redirect their route target.
- `ITenantMemberOperation` is an independently authorized target operation. Member endpoints use
  the route tenant, declare no active context, and apply the existing Viewer/Admin/Owner rules in
  `TenantMemberAuthorizationBehavior`; last-owner invariants remain in the member mutation.

Target policies do not grant unrestricted data access. Persistence remains explicitly scoped to the
chosen target, and the target-owning operation retains its validation and resource invariants.

### Anonymous deployment credentials

The WebService image-tag endpoint explicitly declares `TenantCredentialMetadata("pole_ci_")` and
`AllowAnonymous`. Only that declared destination may defer active-context resolution when the
request presents a single bearer header with that prefix. This selects the credential flow; it does
not authenticate the token. The feature validates its hash, revocation, resource, allowed operation,
and any supplied tenant assertion. Invalid tokens never acquire tenant membership. Other anonymous
endpoints and normal tenant endpoints cannot use this exception. Authenticated user calls to the
image-tag endpoint still resolve the required active context and enforce the write role.

Global fleet operations must use an explicit platform endpoint and must not be
implemented by treating a missing tenant id as an empty MongoDB filter. Internal
workers may still scan tenant-owned collections globally when their workflow
requires it, but events and mutations remain fenced by the document tenant id.

## Resource Scoping

Tenant-owned domain documents include `TenantId`:

* `ResourceGroup`
* `ResourceRegistration`
* `IsolationScope`
* `RuntimeCell`
* `WebService`
* `Redis`
* `PostgresCluster`
* `TenantDomainZone`
* `HostnameBinding`
* `SearchDocument`
* `SearchSourceDocument`
* `SearchProjectionVersionDocument`
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

Resource registration identities and ResourceGroup membership queries always include `TenantId`.
The unique identity index is tenant-prefixed, so tenants may use the same provider resource ID
without colliding, while a resource cannot be registered into two groups within one tenant.

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

WebService, Redis, PostgreSQL, and Functions persist their workload placement in
`ResourceDeployment`. New placement code must use the ResourceDeployment placement capabilities;
see [workload placement](workload-placement.md).

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
3. Declare required `TenantEndpointMetadata` locally and add a non-nullable `[FromTenantHeader]`
   member to request models. Use an explicit target-operation policy when the route addresses a
   separate tenant.
4. Implement the correct tenant marker interface on request models.
5. Validate all cross-resource references with both id and tenant id.
6. Filter reads, updates, deletes, and consumer lookups by tenant id.
7. Include `TenantId` in search projections, outbox messages, and any denormalized read model.
8. Add tests for same-id or same-name resources in two tenants, cross-tenant reference rejection, and missing or invalid tenant headers.
