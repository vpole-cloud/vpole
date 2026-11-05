using MongoDB.Driver;
using Pole.Features.PrivateAccess.Application.Admission;
using Pole.Features.PrivateAccess.Application.Catalog;
using Pole.Features.PrivateAccess.Application.Grants;
using Pole.Features.PrivateAccess.Application.Mongo;
using Pole.Features.PrivateAccess.Application.Sessions;
using Pole.Features.PrivateAccess.Contracts;
using Pole.Features.PrivateAccess.Domain;

namespace Pole.Features.PrivateAccess.Application.Gateway;

internal sealed class PrivateAccessGatewayIdentityReads(
    IMongoCollection<PrivateAccessGateway> gateways,
    TimeProvider timeProvider)
{
    public async Task<PrivateAccessGatewayIdentity?> ResolveAsync(
        string certificateSha256Pin,
        CancellationToken cancellationToken = default)
    {
        var filter = PrivateAccessGatewayFilters.ActivePin(
            certificateSha256Pin, timeProvider.GetUtcNow().UtcDateTime);
        var gateway = await gateways.Find(filter).FirstOrDefaultAsync(cancellationToken);
        return gateway is null ? null : new PrivateAccessGatewayIdentity(
            gateway.TenantId,
            gateway.DeploymentTargetId,
            gateway.Id,
            gateway.Generation,
            gateway.Revision);
    }

    public async Task<bool> RecordContactAsync(
        PrivateAccessGatewayIdentity identity,
        string version,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(version) || version.Length > 100)
            return false;
        var filter = PrivateAccessGatewayFilters.Current(
            identity.TenantId, identity.DeploymentTargetId,
            identity.GatewayId, identity.Generation, identity.Revision);
        var update = Builders<PrivateAccessGateway>.Update
            .Set(item => item.LastContactAtUtc, timeProvider.GetUtcNow().UtcDateTime)
            .Set(item => item.Version, version);
        var result = await gateways.UpdateOneAsync(filter, update,
            cancellationToken: cancellationToken);
        return result.MatchedCount == 1;
    }
}
