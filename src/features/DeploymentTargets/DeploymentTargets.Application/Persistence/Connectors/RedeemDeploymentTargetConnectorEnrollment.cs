using MongoDB.Driver;
using Pole.Database;
using Pole.Features.DeploymentTargets.Domain;

namespace Pole.Features.DeploymentTargets.Application.Persistence.Connectors;

public sealed record DeploymentTargetConnectorEnrolled(
    string TargetId,
    string ConnectorId,
    long Generation,
    long Revision);

public sealed record DeploymentTargetConnectorEnrollmentRejected;
public sealed record DeploymentTargetConnectorCertificateConflict;
public sealed record DeploymentTargetConnectorCertificateRejected;

public union RedeemDeploymentTargetConnectorEnrollmentOutcome(
    DeploymentTargetConnectorEnrolled,
    DeploymentTargetConnectorEnrollmentRejected,
    DeploymentTargetConnectorCertificateConflict,
    DeploymentTargetConnectorCertificateRejected);

public delegate Task<RedeemDeploymentTargetConnectorEnrollmentOutcome> RedeemDeploymentTargetConnectorEnrollment(
    string targetId,
    string tokenDigestSha256,
    string certificateFingerprintSha256,
    string publicKeySpkiSha256,
    DateTime certificateNotAfterUtc,
    CancellationToken cancellationToken = default);

internal sealed class MongoRedeemDeploymentTargetConnectorEnrollment(
    IMongoCollection<DeploymentTarget> targets,
    IMongoCollection<DeploymentTargetConnectorEnrollment> enrollments,
    IMongoCollection<DeploymentTargetConnector> connectors,
    RunMongoOutcomeTransaction<RedeemDeploymentTargetConnectorEnrollmentOutcome> runTransaction,
    TimeProvider clock)
{
    public async Task<RedeemDeploymentTargetConnectorEnrollmentOutcome> InvokeAsync(
        string targetId,
        string tokenDigestSha256,
        string certificateFingerprintSha256,
        string publicKeySpkiSha256,
        DateTime certificateNotAfterUtc,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await runTransaction(async (transaction, ct) =>
            {
                var now = clock.GetUtcNow().UtcDateTime;
                if (certificateNotAfterUtc <= now.AddHours(1))
                {
                    return transaction.Abort(new RedeemDeploymentTargetConnectorEnrollmentOutcome(
                        new DeploymentTargetConnectorCertificateRejected()));
                }

                var tokenFilter = DeploymentTargetConnectorFilters.UnconsumedToken(
                    targetId,
                    tokenDigestSha256,
                    now);
                var enrollment = await enrollments
                    .Find(transaction.Session, tokenFilter)
                    .FirstOrDefaultAsync(ct);
                if (enrollment is null)
                    return transaction.Abort(Rejected());

                var target = await targets
                    .Find(transaction.Session, DeploymentTargetFilters.ById(targetId))
                    .FirstOrDefaultAsync(ct);
                if (target is null || target.DeletionRequestedAtUtc is not null)
                    return transaction.Abort(Rejected());

                // Contend with the target's deletion fence before creating an active identity.
                var fenced = await targets.UpdateOneAsync(
                    transaction.Session,
                    DeploymentTargetFilters.AtReferenceFenceBeforeDeletion(target),
                    Builders<DeploymentTarget>.Update.Inc(item => item.ReferenceFence, 1),
                    cancellationToken: ct);
                if (fenced.ModifiedCount != 1)
                    return transaction.Abort(Rejected());

                DeploymentTargetConnectorEnrolled enrolled;
                switch (enrollment.Kind)
                {
                    case DeploymentTargetConnectorEnrollmentKind.Initial:
                        {
                            var connector = DeploymentTargetConnector.Enroll(
                                enrollment,
                                certificateFingerprintSha256,
                                publicKeySpkiSha256,
                                certificateNotAfterUtc,
                                now);
                            await connectors.InsertOneAsync(
                                transaction.Session,
                                connector,
                                cancellationToken: ct);
                            enrolled = new DeploymentTargetConnectorEnrolled(
                                connector.TargetId,
                                connector.Id,
                                connector.Generation,
                                connector.Revision);
                            break;
                        }
                    case DeploymentTargetConnectorEnrollmentKind.Rotation:
                        {
                            var current = await connectors
                                .Find(transaction.Session,
                                    DeploymentTargetConnectorFilters.ByIdAndTarget(
                                        enrollment.ConnectorId,
                                        targetId))
                                .FirstOrDefaultAsync(ct);
                            if (current is null ||
                                current.State is not ActiveDeploymentTargetConnectorState ||
                                current.Generation + 1 != enrollment.Generation)
                                return transaction.Abort(Rejected());
                            if (string.Equals(current.PublicKeySpkiSha256, publicKeySpkiSha256,
                                    StringComparison.Ordinal))
                            {
                                return transaction.Abort(new RedeemDeploymentTargetConnectorEnrollmentOutcome(
                                    new DeploymentTargetConnectorCertificateRejected()));
                            }

                            var nextRevision = checked(current.Revision + 1);
                            var updated = await connectors.UpdateOneAsync(
                                transaction.Session,
                                DeploymentTargetConnectorFilters.ActiveAtRevision(current),
                                Builders<DeploymentTargetConnector>.Update
                                    .Set(item => item.CertificateFingerprintSha256, certificateFingerprintSha256)
                                    .Set(item => item.PublicKeySpkiSha256, publicKeySpkiSha256)
                                    .Set(item => item.CertificateNotAfterUtc, certificateNotAfterUtc)
                                    .Set(item => item.Generation, enrollment.Generation)
                                    .Set(item => item.Revision, nextRevision)
                                    .Set(item => item.UpdatedAtUtc, now)
                                    .Set(item => item.Observation, null),
                                cancellationToken: ct);
                            if (updated.ModifiedCount != 1)
                                return transaction.Abort(Rejected());
                            enrolled = new DeploymentTargetConnectorEnrolled(
                                current.TargetId,
                                current.Id,
                                enrollment.Generation,
                                nextRevision);
                            break;
                        }
                    default:
                        return transaction.Abort(Rejected());
                }

                var consumed = await enrollments.UpdateOneAsync(
                    transaction.Session,
                    tokenFilter,
                    Builders<DeploymentTargetConnectorEnrollment>.Update.Set(
                        item => item.ConsumedAtUtc,
                        now),
                    cancellationToken: ct);
                if (consumed.ModifiedCount != 1)
                    return transaction.Abort(Rejected());

                return transaction.Commit(new RedeemDeploymentTargetConnectorEnrollmentOutcome(
                    enrolled));
            }, cancellationToken);
        }
        catch (MongoWriteException exception) when (
            exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return new DeploymentTargetConnectorCertificateConflict();
        }
    }

    private static RedeemDeploymentTargetConnectorEnrollmentOutcome Rejected() =>
        new DeploymentTargetConnectorEnrollmentRejected();
}
