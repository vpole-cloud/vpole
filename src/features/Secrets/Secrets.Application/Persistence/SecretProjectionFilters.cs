using MongoDB.Driver;
using Pole.Features.Secrets.Domain;

namespace Pole.Features.Secrets.Application.Persistence;

internal static class SecretProjectionFilters
{
    public static FilterDefinition<SecretProjection> Identity(string tenantId, string id) =>
        Builders<SecretProjection>.Filter.Eq(x => x.TenantId, tenantId) &
        Builders<SecretProjection>.Filter.Eq(x => x.Id, id);

    public static FilterDefinition<SecretProjection> Revision(long revision) =>
        Builders<SecretProjection>.Filter.Eq(x => x.StorageRevision, revision);

    public static FilterDefinition<SecretProjection> Lease(SecretProjectionLease lease) =>
        Builders<SecretProjection>.Filter.Eq(x => x.Lease, lease);

    public static FilterDefinition<Secret> SourceCandidates(string? afterSecretId) =>
        afterSecretId is null ? Builders<Secret>.Filter.Empty : Builders<Secret>.Filter.Gt(x => x.Id, afterSecretId);

    public static FilterDefinition<SecretProjection> RecoveryCandidates(string? afterSecretId, DateTime deletedAuditBefore)
    {
        var filter = Builders<SecretProjection>.Filter.Not(
                Builders<SecretProjection>.Filter.OfType<SecretProjectionState, DeletedSecretProjection>(x => x.State)) |
            Builders<SecretProjection>.Filter.Lt(x => x.LastAttemptAtUtc, deletedAuditBefore);
        return afterSecretId is null ? filter : filter & Builders<SecretProjection>.Filter.Gt(x => x.SecretId, afterSecretId);
    }
}
