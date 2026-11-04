using Pole.Features.GitOps.Contracts;

namespace Pole.Features.Secrets.Application.Persistence;

public enum SecretDeclarationError
{
    ResourceGroupUnavailable,
    ExplicitAdoptionRequired,
    ImmutablePlacementChange,
    ResourceScopeUnavailable,
    ResourceRegistrationUnavailable,
    SecretKeysInUse
}

public sealed record SecretDeclarationRejected(SecretDeclarationError Reason) : IDeclarationFailure;
