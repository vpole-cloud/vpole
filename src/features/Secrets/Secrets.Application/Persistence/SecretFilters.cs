using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Secrets.Domain;

namespace Pole.Features.Secrets.Application;

internal static class SecretFilters
{
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
            x => x.ConsumerType == resourceType && x.ConsumerId == resourceId);
}
