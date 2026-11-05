using Pole.Features.GitOps.Contracts;
using Pole.Features.Secrets.Contracts;
using Pole.Features.WebServices.Application;
using Pole.Features.WebServices.Application.Configuration;
using Pole.Features.WebServices.Application.Hostnames;
using Pole.Features.WebServices.Application.Lifecycle;
using Pole.Features.WebServices.Application.Mongo;
using Pole.Features.WebServices.Application.SourceDelivery;

namespace Pole.Features.WebServices.Application.Declarations;

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
