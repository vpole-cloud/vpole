using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Domain;
using Pole.ResultType;

namespace Pole.Features.ComputeProfiles.Application.Validation;

public delegate Task<Result> ValidateComputeProfileTenantAccess(
    ComputeProfileTenantAccess tenantAccess,
    CancellationToken cancellationToken = default);

public sealed class MongoComputeProfileTenantAccessValidator(IMongoDatabase database)
{
    private const string TenantCollectionName = "Tenants";

    public async Task<Result> InvokeAsync(
        ComputeProfileTenantAccess tenantAccess,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenantAccess);
        if (tenantAccess is AllTenantsComputeProfileAccess)
            return Result.Success();
        if (tenantAccess is not SelectedTenantsComputeProfileAccess selected)
            throw new InvalidOperationException("The compute-profile tenant access kind is not supported.");

        var tenants = database.GetCollection<BsonDocument>(TenantCollectionName);
        var existingIds = await tenants
            .Find(Builders<BsonDocument>.Filter.In(
                "_id",
                selected.TenantIds.Select(ObjectId.Parse)))
            .Project(Builders<BsonDocument>.Projection.Include("_id"))
            .ToListAsync(cancellationToken);
        var found = existingIds
            .Select(document => document["_id"].AsObjectId.ToString())
            .ToHashSet(StringComparer.Ordinal);
        var missing = selected.TenantIds
            .Where(tenantId => !found.Contains(tenantId))
            .Order(StringComparer.Ordinal)
            .ToArray();
        return missing.Length == 0
            ? Result.Success()
            : Failures.SemanticValidation(new Dictionary<string, string[]>
            {
                ["TenantAccess.TenantIds"] =
                ["One or more selected tenants do not exist."]
            });
    }
}
