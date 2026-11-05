using Pole.Features.ResourceGroups.Application.References;
using Pole.Features.ResourceGroups.Application.Mongo;
using Pole.Features.GitOps.Contracts;

namespace Pole.Features.ResourceGroups.Application.ResourceGroups;

public enum ResourceGroupDeclarationError
{
    NameTooLong,
    ExplicitAdoptionRequired,
    ResourceDeleting,
    ExpectedRevisionConflict
}

public sealed record ResourceGroupDeclarationRejected(ResourceGroupDeclarationError Reason) : IDeclarationFailure;

public sealed record ResourceGroupDeclarationApplied(
    string ResourceId,
    bool Changed);
public union ApplyResourceGroupDeclarationResult(
    ResourceGroupDeclarationApplied,
    ResourceGroupDeclarationRejected);
