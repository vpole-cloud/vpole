using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Features.DeploymentTargets.Domain;

public sealed record DeploymentTargetSettings(
    [property: BsonId]
    string Id,
    string DefaultDeploymentTargetId,
    long Revision,
    DateTime UpdatedAtUtc)
{
    public const string SingletonId = "deployment-target-settings";

    public static DeploymentTargetSettings Create(
        string defaultDeploymentTargetId,
        DateTime createdAtUtc)
    {
        var normalizedTargetId = DeploymentTargetValueGuard.ObjectId(
            defaultDeploymentTargetId,
            nameof(defaultDeploymentTargetId));
        var normalizedCreatedAtUtc = DeploymentTargetValueGuard.Utc(
            createdAtUtc,
            nameof(createdAtUtc));

        return new DeploymentTargetSettings(
            SingletonId,
            normalizedTargetId,
            Revision: 1,
            normalizedCreatedAtUtc);
    }

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
