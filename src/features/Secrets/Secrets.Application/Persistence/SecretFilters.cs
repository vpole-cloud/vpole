using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Secrets.Domain;

namespace Pole.Features.Secrets.Application.Persistence;

internal static class SecretFilters
{
    public static FilterDefinition<Secret> AtPlanningVersion(Secret secret) =>
        Identity(secret.TenantId, secret.Id) &
        Builders<Secret>.Filter.Eq(item => item.Revision, secret.Revision) &
        Builders<Secret>.Filter.Eq(item => item.ReferenceFence, secret.ReferenceFence);

    public static FilterDefinition<Secret> DeclarationBindingId(string tenant, string id) =>
        Builders<Secret>.Filter.Eq(item => item.TenantId, tenant) &
        Builders<Secret>.Filter.ElemMatch(item => item.Bindings, binding => binding.Id == id);

    public static FilterDefinition<Secret> DeclarationBindingName(string tenant, string name) =>
        Builders<Secret>.Filter.Eq(item => item.TenantId, tenant) &
        Builders<Secret>.Filter.ElemMatch(item => item.Bindings, binding => binding.DeclarationName == name);
    public static FilterDefinition<Secret> TechnicalName(string tenant, string name) =>
        Builders<Secret>.Filter.Eq(item => item.TenantId, tenant) & Builders<Secret>.Filter.Eq(item => item.NormalizedName, name);

    internal static FilterDefinition<Secret> Identity(
        string tenantId,
        string id) =>
        Builders<Secret>.Filter.ForTenant(SecretIdentity.ObjectId(tenantId)) &
        Builders<Secret>.Filter.Eq(
            x => x.Id,
            SecretIdentity.ObjectId(id));

    internal static FilterDefinition<Secret> Page(
        string tenantId,
        string? after) => after is null
        ? Builders<Secret>.Filter.ForTenant(SecretIdentity.ObjectId(tenantId))
        : Builders<Secret>.Filter.ForTenant(SecretIdentity.ObjectId(tenantId)) &
            Builders<Secret>.Filter.Gt(x => x.Id, SecretIdentity.ObjectId(after));

    internal static FilterDefinition<Secret> Scope(
        string tenantId,
        string groupId,
        string scopeId) =>
        Builders<Secret>.Filter.ForTenant(SecretIdentity.ObjectId(tenantId)) &
        Builders<Secret>.Filter.Eq(
            x => x.ResourceGroupId,
            SecretIdentity.ObjectId(groupId)) &
        Builders<Secret>.Filter.Eq(x => x.IsolationScopeId, SecretIdentity.ObjectId(scopeId));

    internal static FilterDefinition<SecretVersion> Versions(
        string tenantId,
        string id) =>
        Builders<SecretVersion>.Filter.ForTenant(SecretIdentity.ObjectId(tenantId)) &
        Builders<SecretVersion>.Filter.Eq(
            x => x.SecretId,
            SecretIdentity.ObjectId(id));

    internal static FilterDefinition<SecretVersion> Version(
        string tenantId,
        string secretId,
        long version) =>
        Versions(tenantId, secretId) & Builders<SecretVersion>.Filter.Eq(x => x.Version, version);

    internal static FilterDefinition<Secret> Consumer(
        string tenantId,
        string groupId,
        string resourceType,
        string resourceId) =>
        Builders<Secret>.Filter.ForTenant(SecretIdentity.ObjectId(tenantId)) &
        Builders<Secret>.Filter.Eq(
            x => x.ResourceGroupId,
            SecretIdentity.ObjectId(groupId)) &
        Builders<Secret>.Filter.ElemMatch(
            x => x.Bindings,
            x => x.Consumer.ResourceType == resourceType && x.Consumer.ResourceId == resourceId);
}
