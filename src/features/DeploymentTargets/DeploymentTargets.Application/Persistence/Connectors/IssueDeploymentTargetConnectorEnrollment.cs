using System.Security.Cryptography;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Persistence.Connectors;

public sealed record DeploymentTargetConnectorEnrollmentIssued(
    string TargetId,
    string ConnectorId,
    string Token,
    DateTime ExpiresAtUtc)
{
    public override string ToString() =>
        $"DeploymentTargetConnectorEnrollmentIssued(TargetId={TargetId}, ConnectorId={ConnectorId}, Token=[REDACTED], ExpiresAtUtc={ExpiresAtUtc:O})";
}

public union IssueDeploymentTargetConnectorEnrollmentPersistenceOutcome(
    DeploymentTargetConnectorEnrollmentIssued,
    DeploymentTargetNotFound,
    DeploymentTargetRevisionConflict,
    DeploymentTargetDeletionRejected);

public delegate Task<IssueDeploymentTargetConnectorEnrollmentPersistenceOutcome> IssueDeploymentTargetConnectorEnrollment(
    string targetId,
    long expectedTargetRevision,
    CancellationToken cancellationToken = default);

internal sealed class MongoIssueDeploymentTargetConnectorEnrollment(
    IMongoCollection<DeploymentTarget> targets,
    IMongoCollection<DeploymentTargetConnectorEnrollment> enrollments,
    RunMongoOutcomeTransaction<IssueDeploymentTargetConnectorEnrollmentPersistenceOutcome> runTransaction,
    TimeProvider clock)
{
    private static readonly TimeSpan EnrollmentLifetime = TimeSpan.FromMinutes(15);

    public async Task<IssueDeploymentTargetConnectorEnrollmentPersistenceOutcome> InvokeAsync(
        string targetId,
        long expectedTargetRevision,
        CancellationToken cancellationToken = default)
    {
        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        try
        {
            var token = Convert.ToHexString(tokenBytes);
            var tokenDigest = Convert.ToHexString(SHA256.HashData(tokenBytes));
            var connectorId = ObjectId.GenerateNewId().ToString();

            return await runTransaction(async (transaction, ct) =>
            {
                var enrollment = DeploymentTargetConnectorEnrollment.Issue(
                    targetId,
                    connectorId,
                    DeploymentTargetConnectorEnrollmentKind.Initial,
                    1,
                    tokenDigest,
                    clock.GetUtcNow().UtcDateTime,
                    EnrollmentLifetime);
                var target = await targets
                    .Find(transaction.Session, DeploymentTargetFilters.ById(targetId))
                    .FirstOrDefaultAsync(ct);
                if (target is null)
                    return transaction.Abort(new IssueDeploymentTargetConnectorEnrollmentPersistenceOutcome(
                        new DeploymentTargetNotFound(targetId)));
                if (target.DeletionRequestedAtUtc is not null)
                    return transaction.Abort(new IssueDeploymentTargetConnectorEnrollmentPersistenceOutcome(
                        new DeploymentTargetDeletionRejected(targetId)));
                if (target.Revision != expectedTargetRevision)
                    return transaction.Abort(new IssueDeploymentTargetConnectorEnrollmentPersistenceOutcome(
                        new DeploymentTargetRevisionConflict(targetId)));

                // The write contends with the target deletion fence even though enrollment does
                // not alter the target's public configuration revision.
                var fenced = await targets.UpdateOneAsync(
                    transaction.Session,
                    DeploymentTargetFilters.AtReferenceFenceBeforeDeletion(target),
                    Builders<DeploymentTarget>.Update.Inc(item => item.ReferenceFence, 1),
                    cancellationToken: ct);
                if (fenced.ModifiedCount != 1)
                    return transaction.Abort(new IssueDeploymentTargetConnectorEnrollmentPersistenceOutcome(
                        new DeploymentTargetRevisionConflict(targetId)));

                await enrollments.InsertOneAsync(transaction.Session, enrollment, cancellationToken: ct);
                return transaction.Commit(new IssueDeploymentTargetConnectorEnrollmentPersistenceOutcome(
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
}
