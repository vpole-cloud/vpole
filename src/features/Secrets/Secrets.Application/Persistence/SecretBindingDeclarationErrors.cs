using Pole.Features.GitOps.Contracts;

namespace Pole.Features.Secrets.Application.Persistence;

public enum SecretBindingDeclarationError
{
    BindingPrerequisiteUnavailable,
    SecretUnavailable,
    ImmutableBindingChange,
    ExplicitAdoptionRequired,
    SecretKeyUnavailable,
    BindingScopeUnavailable,
    SecretBindingLimitReached,
    SecretKeysInUse
}

public sealed record SecretBindingDeclarationRejected(SecretBindingDeclarationError Reason) : IDeclarationFailure;
