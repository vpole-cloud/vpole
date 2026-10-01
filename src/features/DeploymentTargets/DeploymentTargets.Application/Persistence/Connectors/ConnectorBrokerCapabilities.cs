using System.Collections.Immutable;
using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Persistence.Connectors;

internal sealed class MongoConnectorBrokerCapabilities(
    IMongoCollection<DeploymentTargetConnector> connectors,
    IMongoCollection<DeploymentTarget> targets,
    TimeProvider clock)
{
    public async Task<ResolveDeploymentTargetConnectorOutcome> ResolveAsync(
        string certificateFingerprintSha256,
        CancellationToken cancellationToken = default)
    {
        if (!IsFingerprint(certificateFingerprintSha256))
            return new DeploymentTargetConnectorRejected();

        var connector = await connectors
            .Find(DeploymentTargetConnectorFilters.ByCertificateFingerprint(
                certificateFingerprintSha256))
            .FirstOrDefaultAsync(cancellationToken);
        if (connector is null || !connector.IsActiveAt(clock.GetUtcNow().UtcDateTime))
            return new DeploymentTargetConnectorRejected();

        var target = await targets
            .Find(DeploymentTargetFilters.ById(connector.TargetId))
            .FirstOrDefaultAsync(cancellationToken);
        if (target is null || !target.Enabled || target.DeletionRequestedAtUtc is not null)
            return new DeploymentTargetConnectorRejected();

        return new DeploymentTargetConnectorIdentity(
            connector.TargetId,
            connector.Id,
            connector.Generation,
            connector.Revision);
    }

    public async Task<RecordDeploymentTargetConnectorContactOutcome> RecordContactAsync(
        DeploymentTargetConnectorContact contact,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contact);
        var now = clock.GetUtcNow().UtcDateTime;
        if (contact.Generation < 1 ||
            contact.ObservedAtUtc.Kind != DateTimeKind.Utc ||
            contact.ObservedAtUtc < now.AddMinutes(-5) ||
            contact.ObservedAtUtc > now.AddMinutes(1) ||
            string.IsNullOrWhiteSpace(contact.Version) ||
            contact.Version.Length > 80 ||
            contact.Health is not ("Connected" or "Healthy" or "Degraded") ||
            contact.Capabilities is null ||
            contact.Capabilities.Count > 32 ||
            contact.Capabilities.Any(item => string.IsNullOrWhiteSpace(item) ||
                item.Length > 80 || !item.All(character => char.IsAsciiLetterOrDigit(character) ||
                    character is '-' or '.' or '_')))
            return new DeploymentTargetConnectorRejected();

        var target = await targets
            .Find(DeploymentTargetFilters.ById(contact.TargetId))
            .FirstOrDefaultAsync(cancellationToken);
        if (target is null || !target.Enabled || target.DeletionRequestedAtUtc is not null)
            return new DeploymentTargetConnectorRejected();

        var observation = new DeploymentTargetConnectorObservation(
            contact.Version,
            contact.Health,
            contact.Capabilities.Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToImmutableArray(),
            contact.ObservedAtUtc);
        var updated = await connectors.UpdateOneAsync(
            DeploymentTargetConnectorFilters.ContactOlderThan(
                contact.TargetId,
                contact.ConnectorId,
                contact.Generation,
                contact.ObservedAtUtc),
            Builders<DeploymentTargetConnector>.Update.Set(item => item.Observation, observation),
            cancellationToken: cancellationToken);
        return updated.ModifiedCount == 1
            ? new DeploymentTargetConnectorContactRecorded()
            : new DeploymentTargetConnectorRejected();
    }

    private static bool IsFingerprint(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F');
}
