using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.Secrets.Domain;
using Pole.Workloads;

namespace Pole.Tests.Integration.Infrastructure;

internal static class ConsumerSecretDeletionTestData
{
    public static async Task<Secret> InsertAsync(IMongoDatabase database, ResourceGroup group,
        ResourceTypeId type, string consumerId)
    {
        var id = ObjectId.GenerateNewId().ToString();
        var bindingId = ObjectId.GenerateNewId().ToString();
        var binding = SecretBinding.Create(bindingId, type.Value, consumerId,
            new AllSecretDeployments(), []) with
        { DeclarationName = "consumer-access-" + bindingId };
        var secret = SecretFactory.CreateMetadata(id, group.TenantId, group.Id,
            group.DefaultIsolationScopeId, "consumer-secret-" + id, "deletion protection", [], DateTime.UtcNow)
            .Bind(binding, DateTime.UtcNow);
        await database.GetCollection<Secret>("Secrets").InsertOneAsync(secret);
        return secret;
    }
}
