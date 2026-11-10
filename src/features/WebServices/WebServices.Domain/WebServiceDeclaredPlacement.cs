namespace Pole.Features.WebServices.Domain;

// Names are the user's desired references; IDs pin their resolved identities across retries.
public sealed record WebServiceDeclaredPlacement(
    string DeploymentTargetName,
    string DeploymentTargetId,
    string ComputeProfileName,
    string ComputeProfileId
);
