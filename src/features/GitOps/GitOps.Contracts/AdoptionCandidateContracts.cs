using MongoDB.Driver;

namespace Pole.Features.GitOps.Contracts;

public sealed record DeclarationAdoptionCandidatePageRequest(
    int Page,
    int PageSize
);

public sealed record DeclarationAdoptionCandidateReferences(
    GitLifecycleResourceReference Related,
    GitLifecycleResourceReference Consumer
);

public sealed record FindDeclarationAdoptionCandidatesRequest(
    string TenantId,
    DeclarationAdoptionCandidateReferences References,
    DeclarationAdoptionCandidatePageRequest Page
);

public sealed record DeclarationAdoptionCandidateTargetScope(
    string Mode,
    IReadOnlyList<string> DeploymentIds
);

public sealed record DeclarationAdoptionBindingMetadata(
    GitLifecycleResourceReference Secret,
    GitLifecycleResourceReference Consumer,
    string Usage,
    DeclarationAdoptionCandidateTargetScope TargetScope
);

public sealed record DeclarationAdoptionCandidate(
    string ResourceId,
    string? Name,
    DeclarationAdoptionBindingMetadata? Binding
);

public sealed record DeclarationAdoptionCandidatePage(
    IReadOnlyList<DeclarationAdoptionCandidate> Items,
    bool HasMore
);

// The resource owner supplies metadata searches that cannot be expressed by its declaration name,
// such as a manual authorization binding that has no declaration name yet.
public interface IDeclarativeAdoptionCandidateProvider
{
    Task<DeclarationAdoptionCandidatePage> FindAdoptionCandidatesAsync(
        FindDeclarationAdoptionCandidatesRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken);
}
