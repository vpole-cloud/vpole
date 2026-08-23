namespace Pole.Features.DeploymentTargets.Application.Common;

public sealed record DeploymentTargetRevisionConflict(string DeploymentTargetId);

public sealed record DeploymentTargetDeletionRejected(string DeploymentTargetId);

public sealed record DefaultDeploymentTargetDeletionRejected(string DeploymentTargetId);

public sealed record DeploymentTargetHasDurableReferences(string DeploymentTargetId);

public sealed record DeploymentTargetPublicBaseDomainReferenced(string DeploymentTargetId);

public sealed record DeploymentTargetPublicBaseDomainConflict(string DeploymentTargetId);

public sealed record DeploymentTargetIdentityConflict;

public sealed record DeploymentTargetConnectionRejected(
    IReadOnlyDictionary<string, string[]> Errors);

public sealed record DeploymentTargetCredentialRewrapConflict;
