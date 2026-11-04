using Pole.Features.GitOps.Domain;
using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;

namespace Pole.Features.GitOps.Application.Inventory.Mongo.Filters;

internal static class ManagedDeclarationFilters
{
    public static string AddressId(string tenant, ResourceAddress address) => $"{tenant}/{address.Kind}/{address.Name}";

    public static FilterDefinition<ManagedDeclaration> Address(string tenant, ResourceAddress address) =>
        Builders<ManagedDeclaration>.Filter.Eq(item => item.Id, AddressId(tenant, address));

    public static FilterDefinition<ManagedDeclaration> Resource(string tenant, string kind, string id) =>
        Builders<ManagedDeclaration>.Filter.Eq(item => item.TenantId, tenant) &
        Builders<ManagedDeclaration>.Filter.Eq(item => item.Kind, kind) &
        Builders<ManagedDeclaration>.Filter.Eq(item => item.ResourceId, id);

    public static FilterDefinition<ManagedDeclaration> Resources(
        string tenant,
        string kind,
        IReadOnlyCollection<string> ids) =>
        Builders<ManagedDeclaration>.Filter.Eq(item => item.TenantId, tenant) &
        Builders<ManagedDeclaration>.Filter.Eq(item => item.Kind, kind) &
        Builders<ManagedDeclaration>.Filter.In(item => item.ResourceId, ids);

    public static FilterDefinition<ManagedDeclaration> SourceInventory(string tenant, string source) =>
        Builders<ManagedDeclaration>.Filter.Eq(item => item.TenantId, tenant) &
        Builders<ManagedDeclaration>.Filter.Eq(item => item.SourceId, source);

    public static FilterDefinition<ManagedDeclaration> CreatedResources(string tenant, string source,
        IReadOnlyCollection<string> ids) => SourceInventory(tenant, source) &
        Builders<ManagedDeclaration>.Filter.Eq(item => item.Materialization, GitResourceMaterialization.Created) &
        Builders<ManagedDeclaration>.Filter.In(item => item.ResourceId, ids);

    public static FilterDefinition<ManagedDeclaration> PendingRemoval(string tenant, string source) =>
        SourceInventory(tenant, source) & Builders<ManagedDeclaration>.Filter.Eq(item => item.PendingRemoval, true);
}
