using Pole.Features.Secrets.Application.Values;
using Pole.Features.Secrets.Application.Secrets;
using Pole.Features.Secrets.Application.References;
using Pole.Features.Secrets.Application.Projections;
using Pole.Features.Secrets.Application.Mongo;
using Pole.Features.Secrets.Application.Managed;
using Pole.Features.Secrets.Application.Lifecycle;
using Pole.Features.Secrets.Application.Bootstrap;
using Pole.Features.Secrets.Application.Bindings;
using Pole.Features.Secrets.Application;
using Pole.Features.GitOps.Contracts;

namespace Pole.Features.Secrets.Application.Declarations;

public enum SecretDeclarationError
{
    ResourceGroupUnavailable,
    ExplicitAdoptionRequired,
    ImmutablePlacementChange,
    ResourceScopeUnavailable,
    ResourceRegistrationUnavailable,
    SecretKeysInUse,
    ProviderManagedSecret
}

public sealed record SecretDeclarationRejected(SecretDeclarationError Reason) : IDeclarationFailure;
