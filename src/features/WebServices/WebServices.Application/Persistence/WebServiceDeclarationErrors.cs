using Pole.Features.GitOps.Contracts;
using Pole.Features.Secrets.Contracts;
using Pole.Features.WebServices.Application.Persistence.Mutations;

namespace Pole.Features.WebServices.Application.Persistence;

public enum WebServiceDeclarationError
{
    ExplicitAdoptionRequired,
    ResourceDeleting,
    DeploymentTargetNameAmbiguous,
    DeploymentTargetUnavailable,
    PreparedPlacementIdentityUnavailable,
    ImmutablePlacementChange,
    PreparedResourceUnavailable,
    ComputeProfileNameAmbiguous,
    ComputeProfileRevisionUnavailable,
    PinnedComputeProfileUnavailable,
    QuotaUnavailable,
    ResourceGroupUnavailable,
    ExpectedRevisionConflict,
    ResourceRegistrationUnavailable,
    PlacementUnavailable,
    PlacementChanged,
    SecretUnavailable,
    PublicIngressUnavailable
}

public sealed record WebServiceDeclarationRejected(WebServiceDeclarationError Reason) : IDeclarationFailure;

public sealed record WebServiceSecretReferenceRejected(ConsumerSecretReferenceUnavailable Error) : IDeclarationFailure;
public sealed record WebServiceDependencyRejected(WebServiceDependencyUnavailable Error) : IDeclarationFailure;
