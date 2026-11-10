using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.Secrets.Application.Mongo;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Domain;

namespace Pole.Features.Secrets.Application.References;

internal sealed class SecretPlanningFence(
    IMongoCollection<Secret> secrets
)
{
    public static DeclarationDocumentVersion[] Versions(Secret secret) =>
    [
        new(SecretVersionKeys.Revision(secret.Id), secret.Revision),
        new(SecretVersionKeys.ReferenceFence(secret.Id), secret.ReferenceFence),
    ];

    public static bool Matches(Secret secret, DeclarationVersionToken expected) =>
        Versions(secret).All(expected.Documents.Contains);

    public async Task<bool> FenceAsync(
        Secret secret,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var result = await secrets.UpdateOneAsync(
            session,
            SecretFilters.AtPlanningVersion(secret),
            Builders<Secret>.Update.Inc(item => item.ReferenceFence, 1),
            cancellationToken: ct);
        return result.ModifiedCount == 1;
    }
}
