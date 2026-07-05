# Naming Refactor Plan

Status: agreed plan

## Goal

Make feature project, folder, file, class, event, and namespace names explicit and consistent across the modular monolith.

This refactor is intentionally limited to naming and physical project layout. It should not move source files between projects, change feature ownership, or introduce new architectural boundaries.

## Agreed Conventions

### Feature project layout

Feature project folders and project files should include the feature name and layer name.

Target shape:

```text
src/features/WebServices/WebServices.Application/WebServices.Application.csproj
src/features/WebServices/WebServices.Domain/WebServices.Domain.csproj
src/features/WebServices/WebServices.Events/WebServices.Events.csproj
```

Apply the same convention to every feature that has those layers:

```text
src/features/DnsRecords/DnsRecords.Application/DnsRecords.Application.csproj
src/features/DnsRecords/DnsRecords.Domain/DnsRecords.Domain.csproj
src/features/DnsRecords/DnsRecords.Events/DnsRecords.Events.csproj

src/features/RedisServices/RedisServices.Application/RedisServices.Application.csproj
src/features/RedisServices/RedisServices.Domain/RedisServices.Domain.csproj
src/features/RedisServices/RedisServices.Events/RedisServices.Events.csproj

src/features/ResourceGroups/ResourceGroups.Application/ResourceGroups.Application.csproj
src/features/ResourceGroups/ResourceGroups.Domain/ResourceGroups.Domain.csproj
src/features/ResourceGroups/ResourceGroups.Events/ResourceGroups.Events.csproj

src/features/Tenants/Tenants.Application/Tenants.Application.csproj
src/features/Tenants/Tenants.Domain/Tenants.Domain.csproj

src/features/Users/Users.Application/Users.Application.csproj
src/features/Users/Users.Domain/Users.Domain.csproj

src/features/Postgres/Postgres.Application/Postgres.Application.fsproj
src/features/Postgres/Postgres.Domain/Postgres.Domain.fsproj
src/features/Postgres/Postgres.Events/Postgres.Events.fsproj

src/features/Search/Search.Application/Search.Application.fsproj
src/features/Search/Search.Domain/Search.Domain.fsproj

src/features/Outbox/Outbox.Application/Outbox.Application.csproj
```

### Assembly names and root namespaces

Application projects should include `.Application` in both assembly name and root namespace.

Examples:

```xml
<AssemblyName>Pole.Features.WebServices.Application</AssemblyName>
<RootNamespace>Pole.Features.WebServices.Application</RootNamespace>
```

Domain and Events projects should keep their layer suffixes:

```xml
<AssemblyName>Pole.Features.WebServices.Domain</AssemblyName>
<RootNamespace>Pole.Features.WebServices.Domain</RootNamespace>

<AssemblyName>Pole.Features.WebServices.Events</AssemblyName>
<RootNamespace>Pole.Features.WebServices.Events</RootNamespace>
```

`DnsRecords` must use the plural `Pole.Features.DnsRecords.*` namespace, not the current singular `Pole.Feature.DnsRecords.*`.

### Endpoint slice files

Endpoint slice files should include the resource name and operation name, while keeping the current single-file vertical slice structure.

Examples:

```text
Endpoints/CreateWebService.cs
Endpoints/UpdateWebService.cs
Endpoints/DeleteWebService.cs
Endpoints/GetWebServices.cs
Endpoints/GetWebServiceById.cs
```

Do not split handlers, requests, responses, validators, and endpoint mapping into separate files in this refactor.

### Endpoint marker classes

Static endpoint mapping classes should include the operation and resource name.

Examples:

```csharp
public static class CreateWebService
public static class UpdateWebService
public static class DeleteWebService
```

API operation names such as `.WithName("CreateWebService")` can stay command-style and should not be changed as part of this rename.

### Event contracts

Event types should be named as domain facts.

Examples:

```text
WebServiceCreatedEvent
WebServiceUpdatedEvent
WebServiceDeletedEvent

RedisServiceCreatedEvent
RedisServiceUpdatedEvent
RedisServiceDeletedEvent

ResourceGroupCreatedEvent
ResourceGroupUpdatedEvent
ResourceGroupDeletedEvent
```

`UpdateEventChanges` should also be domain-specific:

```text
WebServiceUpdateChanges
RedisServiceUpdateChanges
ResourceGroupUpdateChanges
```

### MassTransit entity names

MassTransit entity names should match the domain-fact event names in kebab-case.

Examples:

```text
web-service-created
web-service-updated
web-service-deleted

redis-service-created
redis-service-updated
redis-service-deleted

resource-group-created
resource-group-updated
resource-group-deleted
```

This is a broker contract change. It should be called out explicitly in the PR and deployment notes.

### Consumer names

Consumers should be named by the reaction they perform, not only by the event they consume.

Examples:

```text
ProvisionWebServiceOnWebServiceCreatedConsumer
AddWebServiceToResourceGroupOnWebServiceCreatedConsumer
ProvisionRedisServiceOnRedisServiceCreatedConsumer
ProvisionResourceGroupOnResourceGroupCreatedConsumer
```

## Scope

Included:

* Rename feature project folders and project files.
* Update `.slnx` project paths.
* Update all `ProjectReference` paths.
* Update assembly names and root namespaces.
* Update C# and F# namespaces/usings/open statements affected by the renames.
* Rename endpoint slice files and static endpoint classes.
* Rename feature event files and event types.
* Rename event change record types.
* Rename consumers according to their reaction.
* Update MassTransit `EntityName` values.
* Update tests and documentation references.

Excluded:

* Moving source files between projects.
* Splitting endpoint slice files into multiple files.
* Introducing `Contracts` projects.
* Renaming shared projects such as `shared/Features/Features.csproj`.
* Changing API operation names such as `.WithName("CreateWebService")`.
* Changing endpoint routes or HTTP behavior.

## Suggested Order

1. Rename feature project folders and project files.
2. Update `Pole.slnx` and all `ProjectReference` paths.
3. Update `<AssemblyName>` and `<RootNamespace>` values.
4. Normalize `DnsRecords` namespaces from `Pole.Feature.DnsRecords.*` to `Pole.Features.DnsRecords.*`.
5. Rename event files and event types.
6. Rename event change record types.
7. Rename consumers by reaction.
8. Rename endpoint slice files and static endpoint classes.
9. Update tests and any documentation references.
10. Build and test.

## Verification

Run:

```powershell
dotnet build Pole.slnx
dotnet test tests\Pole.Tests.Integration\Pole.Tests.Integration.csproj
```

Also search for stale names:

```powershell
rg "Pole\.Feature\.DnsRecords|CreateEvent|UpdateEvent|DeleteEvent|UpdateEventChanges|/Core/|\\Core\\"
```
