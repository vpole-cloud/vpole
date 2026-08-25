using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Domain;

namespace Pole.Features.Tenants.Application.Persistence;

internal static class TenantFilters
{
    public static FilterDefinition<Tenant> ById(string tenantId) =>
        Builders<Tenant>.Filter.Eq(tenant => tenant.Id, tenantId);

    public static FilterDefinition<Tenant> ByIds(
        IReadOnlyCollection<string> tenantIds) =>
        Builders<Tenant>.Filter.In(tenant => tenant.Id, tenantIds);

    public static FilterDefinition<Tenant> ForMemberUser(string userId) =>
        Builders<Tenant>.Filter.ElemMatch(
            tenant => tenant.Members,
            member => member.UserId == userId);

    public static FilterDefinition<Tenant> ByIdWithoutMemberUser(
        string tenantId,
        string userId) =>
        Builders<Tenant>.Filter.And(
            ById(tenantId),
            Builders<Tenant>.Filter.Not(ForMemberUser(userId)));

    public static FilterDefinition<Tenant> ByIdWithMember(
        string tenantId,
        string memberId) =>
        Builders<Tenant>.Filter.And(
            ById(tenantId),
            Builders<Tenant>.Filter.ElemMatch(
                tenant => tenant.Members,
                member => member.Id == memberId));

    public static FilterDefinition<Tenant> ByIdWithMemberAndAnotherOwner(
        string tenantId,
        string memberId) =>
        Builders<Tenant>.Filter.And(
            ByIdWithMember(tenantId, memberId),
            AtLeastTwoOwners());

    private static FilterDefinition<Tenant> AtLeastTwoOwners() =>
        new BsonDocumentFilterDefinition<Tenant>(
            new BsonDocument(
                "$expr",
                new BsonDocument(
                    "$gt",
                    new BsonArray
                    {
                        new BsonDocument(
                            "$size",
                            new BsonDocument(
                                "$filter",
                                new BsonDocument
                                {
                                    ["input"] = "$Members",
                                    ["as"] = "member",
                                    ["cond"] = new BsonDocument(
                                        "$eq",
                                        new BsonArray
                                        {
                                            "$$member.Role",
                                            (int)TenantRole.Owner
                                        })
                                })),
                        1
                    })));
}
