using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Persistence.Connectors;

internal static class DeploymentTargetConnectorFilters
{
    public static FilterDefinition<DeploymentTargetConnector> ByIdAndTarget(
        string connectorId,
        string targetId) => Builders<DeploymentTargetConnector>.Filter.And(
            Builders<DeploymentTargetConnector>.Filter.Eq(connector => connector.Id, connectorId),
            Builders<DeploymentTargetConnector>.Filter.Eq(connector => connector.TargetId, CanonicalId(targetId)));

    public static FilterDefinition<DeploymentTargetConnector> ByTarget(string targetId) =>
        Builders<DeploymentTargetConnector>.Filter.Eq(connector => connector.TargetId, CanonicalId(targetId));

    public static FilterDefinition<DeploymentTargetConnector> ByCertificateFingerprint(
        string certificateFingerprintSha256) => Builders<DeploymentTargetConnector>.Filter.Eq(
            connector => connector.CertificateFingerprintSha256,
            certificateFingerprintSha256);

    public static FilterDefinition<DeploymentTargetConnector> ActiveAtGeneration(
        string targetId,
        string connectorId,
        long generation) => Builders<DeploymentTargetConnector>.Filter.And(
            ByIdAndTarget(connectorId, targetId),
            Builders<DeploymentTargetConnector>.Filter.Eq(item => item.Generation, generation),
            Builders<DeploymentTargetConnector>.Filter.Eq(
                "State._t",
                nameof(ActiveDeploymentTargetConnectorState)));

    public static FilterDefinition<DeploymentTargetConnector> ActiveAtRevision(
        DeploymentTargetConnector connector) => Builders<DeploymentTargetConnector>.Filter.And(
            ActiveAtGeneration(connector.TargetId, connector.Id, connector.Generation),
            Builders<DeploymentTargetConnector>.Filter.Eq(item => item.Revision, connector.Revision));

    public static FilterDefinition<DeploymentTargetConnector> ContactOlderThan(
        string targetId,
        string connectorId,
        long generation,
        DateTime observedAtUtc) => Builders<DeploymentTargetConnector>.Filter.And(
            ActiveAtGeneration(targetId, connectorId, generation),
            Builders<DeploymentTargetConnector>.Filter.Or(
                Builders<DeploymentTargetConnector>.Filter.Eq(item => item.Observation, null),
                Builders<DeploymentTargetConnector>.Filter.Lt(
                    "Observation.LastContactAtUtc",
                    observedAtUtc)));

    public static FilterDefinition<DeploymentTargetConnector> ActiveOnTarget(
        string targetId) => Builders<DeploymentTargetConnector>.Filter.And(
            Builders<DeploymentTargetConnector>.Filter.Eq(connector => connector.TargetId, CanonicalId(targetId)),
            Builders<DeploymentTargetConnector>.Filter.Eq(
                "State._t",
                nameof(ActiveDeploymentTargetConnectorState)));

    public static FilterDefinition<DeploymentTargetConnectorEnrollment> UnconsumedToken(
        string targetId,
        string tokenDigestSha256,
        DateTime nowUtc) => Builders<DeploymentTargetConnectorEnrollment>.Filter.And(
            Builders<DeploymentTargetConnectorEnrollment>.Filter.Eq(item => item.TargetId, CanonicalId(targetId)),
            Builders<DeploymentTargetConnectorEnrollment>.Filter.Eq(item => item.TokenDigestSha256, tokenDigestSha256),
            Builders<DeploymentTargetConnectorEnrollment>.Filter.Eq(item => item.ConsumedAtUtc, null),
            Builders<DeploymentTargetConnectorEnrollment>.Filter.Gt(item => item.ExpiresAtUtc, nowUtc));

    public static FilterDefinition<DeploymentTargetConnectorEnrollment> ByConnector(
        string targetId,
        string connectorId) => Builders<DeploymentTargetConnectorEnrollment>.Filter.And(
            Builders<DeploymentTargetConnectorEnrollment>.Filter.Eq(item => item.TargetId, CanonicalId(targetId)),
            Builders<DeploymentTargetConnectorEnrollment>.Filter.Eq(item => item.ConnectorId, connectorId));

    private static string CanonicalId(string value) =>
        ObjectId.TryParse(value, out var id) ? id.ToString() : value;
}
