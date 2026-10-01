using MongoDB.Driver;
using Pole.Database;
using Pole.Features.PrivateAccess.Domain;

namespace Pole.Features.PrivateAccess.Application.Persistence;

internal static class PrivateAccessGatewayFilters
{
    public static FilterDefinition<PrivateAccessGateway> ByTenantTarget(
        string tenantId,
        string targetId) =>
        Builders<PrivateAccessGateway>.Filter.And(
            Builders<PrivateAccessGateway>.Filter.ForTenant(tenantId),
            Builders<PrivateAccessGateway>.Filter.Eq(item => item.DeploymentTargetId, targetId));

    public static FilterDefinition<PrivateAccessGateway> ActivePin(
        string certificateSha256Pin,
        DateTime nowUtc) =>
        Builders<PrivateAccessGateway>.Filter.And(
            Builders<PrivateAccessGateway>.Filter.Eq(
                item => item.CertificateSha256Pin, certificateSha256Pin),
            Builders<PrivateAccessGateway>.Filter.Gt(
                item => item.CertificateNotAfterUtc, nowUtc),
            Builders<PrivateAccessGateway>.Filter
                .OfType<PrivateAccessGatewayState, ActivePrivateAccessGatewayState>(
                    item => item.State));

    public static FilterDefinition<PrivateAccessGateway> Current(
        string tenantId,
        string targetId,
        string gatewayId,
        long generation,
        long revision) =>
        Builders<PrivateAccessGateway>.Filter.And(
            ByTenantTarget(tenantId, targetId),
            Builders<PrivateAccessGateway>.Filter.Eq(item => item.Id, gatewayId),
            Builders<PrivateAccessGateway>.Filter.Eq(item => item.Generation, generation),
            Builders<PrivateAccessGateway>.Filter.Eq(item => item.Revision, revision),
            Builders<PrivateAccessGateway>.Filter
                .OfType<PrivateAccessGatewayState, ActivePrivateAccessGatewayState>(
                    item => item.State));
}
