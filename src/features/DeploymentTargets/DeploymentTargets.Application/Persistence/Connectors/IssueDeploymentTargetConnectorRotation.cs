using System.Security.Cryptography;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Persistence.Connectors;

public sealed record DeploymentTargetConnectorNotFound(string ConnectorId);
public sealed record DeploymentTargetConnectorRevisionConflict(string ConnectorId);

public union IssueDeploymentTargetConnectorRotationPersistenceOutcome(
    DeploymentTargetConnectorEnrollmentIssued,
    DeploymentTargetNotFound,
    DeploymentTargetDeletionRejected,
    DeploymentTargetConnectorNotFound,
    DeploymentTargetConnectorRevisionConflict);

public delegate Task<IssueDeploymentTargetConnectorRotationPersistenceOutcome> IssueDeploymentTargetConnectorRotation(
    string targetId,
    string connectorId,
    long expectedConnectorRevision,
    CancellationToken cancellationToken = default);

internal sealed class MongoIssueDeploymentTargetConnectorRotation(
    IMongoCollection<DeploymentTarget> targets,
    IMongoCollection<DeploymentTargetConnector> connectors,
    IMongoCollection<DeploymentTargetConnectorEnrollment> enrollments,
    RunMongoOutcomeTransaction<IssueDeploymentTargetConnectorRotationPersistenceOutcome> runTransaction,
    TimeProvider clock)
{
    public async Task<IssueDeploymentTargetConnectorRotationPersistenceOutcome> InvokeAsync(
        string targetId,
        string connectorId,
        long expectedConnectorRevision,
        CancellationToken cancellationToken = default)
    {
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        try
        {
            var token = Convert.ToHexString(tokenBytes);
            var tokenDigest = Convert.ToHexString(SHA256.HashData(tokenBytes));
            return await runTransaction(async (transaction, ct) =>
            {
                var now = clock.GetUtcNow().UtcDateTime;
                var target = await targets
                    .Find(transaction.Session, DeploymentTargetFilters.ById(targetId))
                    .FirstOrDefaultAsync(ct);
                if (target is null)
                    return transaction.Abort(Error(new DeploymentTargetNotFound(targetId)));
                if (target.DeletionRequestedAtUtc is not null)
                    return transaction.Abort(Error(new DeploymentTargetDeletionRejected(targetId)));

                var connector = await connectors
                    .Find(transaction.Session,
                        DeploymentTargetConnectorFilters.ByIdAndTarget(connectorId, targetId))
                    .FirstOrDefaultAsync(ct);
                if (connector is null || connector.State is RevokedDeploymentTargetConnectorState)
                    return transaction.Abort(Error(new DeploymentTargetConnectorNotFound(connectorId)));
                if (connector.Revision != expectedConnectorRevision)
                    return transaction.Abort(Error(new DeploymentTargetConnectorRevisionConflict(connectorId)));

                var fenced = await targets.UpdateOneAsync(
                    transaction.Session,
                    DeploymentTargetFilters.AtReferenceFenceBeforeDeletion(target),
                    Builders<DeploymentTarget>.Update.Inc(item => item.ReferenceFence, 1),
                    cancellationToken: ct);
                if (fenced.ModifiedCount != 1)
                    return transaction.Abort(Error(new DeploymentTargetDeletionRejected(targetId)));

                var enrollment = DeploymentTargetConnectorEnrollment.Issue(
                    target.Id,
                    connector.Id,
                    DeploymentTargetConnectorEnrollmentKind.Rotation,
                    checked(connector.Generation + 1),
                    tokenDigest,
                    now,
                    TimeSpan.FromMinutes(15));
                await enrollments.InsertOneAsync(transaction.Session, enrollment, cancellationToken: ct);
                return transaction.Commit(new IssueDeploymentTargetConnectorRotationPersistenceOutcome(
                    new DeploymentTargetConnectorEnrollmentIssued(
                        enrollment.TargetId,
                        enrollment.ConnectorId,
                        token,
                        enrollment.ExpiresAtUtc)));
            }, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tokenBytes);
        }
    }

    private static IssueDeploymentTargetConnectorRotationPersistenceOutcome Error(
        DeploymentTargetNotFound error) => error;
    private static IssueDeploymentTargetConnectorRotationPersistenceOutcome Error(
        DeploymentTargetDeletionRejected error) => error;
    private static IssueDeploymentTargetConnectorRotationPersistenceOutcome Error(
        DeploymentTargetConnectorNotFound error) => error;
    private static IssueDeploymentTargetConnectorRotationPersistenceOutcome Error(
        DeploymentTargetConnectorRevisionConflict error) => error;
}
