using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Features.DeploymentTargets.Domain;

public sealed record DeploymentTargetSettings
{
    public const string SingletonId = "deployment-target-settings";

    [BsonConstructor]
    public DeploymentTargetSettings(
        string id,
        string defaultDeploymentTargetId,
        long revision,
        DateTime updatedAtUtc)
    {
        if (!string.Equals(id, SingletonId, StringComparison.Ordinal))
            throw new ArgumentException("The deployment-target settings ID is invalid.", nameof(id));

        Id = id;
        DefaultDeploymentTargetId = DeploymentTargetValueGuard.ObjectId(
            defaultDeploymentTargetId,
            nameof(defaultDeploymentTargetId));
        Revision = DeploymentTargetValueGuard.Positive(revision, nameof(revision));
        UpdatedAtUtc = DeploymentTargetValueGuard.Utc(updatedAtUtc, nameof(updatedAtUtc));
    }

    [BsonId]
    public string Id { get; }
    public string DefaultDeploymentTargetId { get; }
    public long Revision { get; }
    public DateTime UpdatedAtUtc { get; }

    public static DeploymentTargetSettings Create(
        string defaultDeploymentTargetId,
        DateTime createdAtUtc) =>
        new(SingletonId, defaultDeploymentTargetId, revision: 1, createdAtUtc);

    public DeploymentTargetSettings SetDefault(
        string deploymentTargetId,
        DateTime updatedAtUtc)
    {
        var normalizedTargetId = DeploymentTargetValueGuard.ObjectId(
            deploymentTargetId,
            nameof(deploymentTargetId));
        if (string.Equals(DefaultDeploymentTargetId, normalizedTargetId, StringComparison.Ordinal))
            return this;

        var normalizedUpdatedAtUtc = DeploymentTargetValueGuard.Utc(updatedAtUtc, nameof(updatedAtUtc));
        if (normalizedUpdatedAtUtc < UpdatedAtUtc)
            throw new ArgumentException("The update time cannot move backwards.", nameof(updatedAtUtc));

        return new DeploymentTargetSettings(
            SingletonId,
            normalizedTargetId,
            checked(Revision + 1),
            normalizedUpdatedAtUtc);
    }
}
