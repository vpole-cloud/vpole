using MongoDB.Driver;
using Pole.Workloads;
using Pole.Features.GitOps.Primitives;

namespace Pole.Features.GitOps.Contracts;

public sealed record GitLifecycleResourceReference(
    ResourceAddress Address,
    string ResourceId);

public sealed record ReadLifecycleResourceRequest(
    string TenantId,
    GitLifecycleResourceReference Resource);

// ManagedConfiguration opts into an authoritative desired-state/dependency version vector;
// its provider must cover identity, managed configuration and required mutation guards without runtime progress.
public sealed record GitLifecycleResourceSnapshot(
    GitLifecycleResourceReference Resource,
    DeclarationVersionToken Version,
    GitLifecyclePresence Presence,
    GitLifecycleProtection Protection,
    GitLifecycleVersionSemantics VersionSemantics);

public sealed record GitLifecycleResourceAbsent;

public union ReadLifecycleResourceOutcome(
    GitLifecycleResourceSnapshot,
    GitLifecycleResourceAbsent,
    DeclarationReadBlocked);

// Constructed from the stored, confirmed action and current Mongo claim, never from HTTP input.
public sealed record GitLifecycleAuthority(
    string TenantId,
    string SourceId,
    string RunId,
    long ConfigurationRevision,
    long Generation,
    long Fence,
    string WorkerId,
    long Attempt,
    string PreviewId,
    string ActionId,
    string? Commit = null);

public sealed record RemoveLifecycleResourceRequest(
    GitLifecycleAuthority Authority,
    GitLifecycleResourceReference Resource,
    DeclarationVersionToken Expected);

public sealed record GitLifecycleRemovalRequested;

public sealed record GitLifecycleResourceRemoved;

public union RemoveLifecycleResourceOutcome(
    GitLifecycleRemovalRequested,
    GitLifecycleResourceRemoved,
    DeclarationPreconditionsChanged,
    DeclarationReadBlocked,
    StaleGitExecution);

public interface IDeclarativeResourceLifecycleProvider
{
    string Kind { get; }

    Task<ReadLifecycleResourceOutcome> ReadLifecycleAsync(
        ReadLifecycleResourceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken);

    Task<RemoveLifecycleResourceOutcome> RemoveAsync(
        RemoveLifecycleResourceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken);
}

public sealed record GitLifecycleDependency(
    GitLifecycleResourceReference Dependent,
    GitLifecycleResourceReference Target,
    DeclarationVersionToken Version,
    GitLifecycleDependencyOrder Order = GitLifecycleDependencyOrder.BeforeRemoval);

public sealed record ReadLifecycleDependenciesRequest(
    string TenantId,
    GitLifecycleResourceReference Resource);

// Each owner reads its own references. The engine never reads a foreign feature collection.
public interface IDeclarativeLifecycleDependencyProvider
{
    string DependencyOwner { get; }

    Task<IReadOnlyList<GitLifecycleDependency>> ReadDependenciesAsync(
        ReadLifecycleDependenciesRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken);

    Task<FenceDeclarationOutcome> FenceDependenciesAsync(
        FenceLifecycleDependenciesRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken);
}

public sealed record FenceLifecycleDependenciesRequest(
    string TenantId,
    GitLifecycleResourceReference Resource,
    IReadOnlyList<GitLifecycleDependency> Expected);

public sealed record GitLifecycleNoChanges : IDeclarationFailure;

public sealed record GitLifecycleRetainPolicyRequired : IDeclarationFailure;

public sealed record GitLifecycleUnsupported : IDeclarationFailure;

public sealed record GitLifecycleResourceProtected : IDeclarationFailure;

public sealed record GitLifecycleDependencyRetained : IDeclarationFailure;

public sealed record GitLifecycleApprovalStale : IDeclarationFailure;

public sealed record AdoptLifecycleResourceRequest(
    GitLifecycleAuthority Authority,
    GitOperationInput Operation,
    DeclarationVersionToken Expected);

public sealed record GitLifecycleResourceAdopted;

public union AdoptLifecycleResourceOutcome(
    GitLifecycleResourceAdopted,
    DeclarationPreconditionsChanged,
    DeclarationReadBlocked,
    StaleGitExecution);

// Only owners needing an explicit metadata transition implement this capability.
// Expected includes the full approved resource and dependency vector.
// Match required owner entries and fence dependencies in this shared session.
// Do not require equality between the full vector and tokens from only the owner.
public interface IDeclarativeResourceAdoptionProvider
{
    Task<AdoptLifecycleResourceOutcome> AdoptAsync(
        AdoptLifecycleResourceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken);
}

public sealed record GitLifecycleApprovedResource(
    GitLifecycleResourceReference Resource,
    GitLifecycleAction Action,
    DeclarationVersionToken? Expected);

public sealed record GitLifecycleApprovedScope(IReadOnlyList<GitLifecycleApprovedResource> Resources);

public union ReadLifecycleApprovedScopeOutcome(
    GitLifecycleApprovedScope,
    StaleGitExecution);

public interface IGitLifecyclePlanGuard
{
    Task<ReadLifecycleApprovedScopeOutcome> ReadScopeAsync(
        GitLifecycleAuthority authority,
        IClientSessionHandle session,
        CancellationToken cancellationToken);
}

public sealed record GitPreviewResolvedReference(
    ResourceAddress Address,
    string ResourceId,
    DeclarationVersionToken? Version = null,
    GitPreviewReferenceState State = GitPreviewReferenceState.Existing);

public sealed record PreviewLifecycleAdoptionRequest(
    string TenantId,
    CompiledDeclaration Declaration,
    string ResourceId,
    IReadOnlyList<GitPreviewResolvedReference> References);

public sealed record GitLifecycleAdoptionPreview(
    CurrentDeclaration Current,
    DeclarationProjection Desired,
    IReadOnlyList<GitPreviewResolvedReference> ResolvedReferences);

public union PreviewLifecycleAdoptionOutcome(
    GitLifecycleAdoptionPreview,
    DeclarationReadBlocked);

// Planning is read-only and has no fabricated run, lease or mutation authority.
public interface IDeclarativeAdoptionPreviewProvider
{
    Task<PreviewLifecycleAdoptionOutcome> PreviewAdoptionAsync(
        PreviewLifecycleAdoptionRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken);
}


public interface IDeclarativeResourceViewProvider
{
    ResourceTypeId ResourceType { get; }
}

public interface IDeclarativeRegisteredResourceProvider : IDeclarativeResourceViewProvider;

public sealed record ReadPreviewReferencesRequest(
    string TenantId,
    CompiledDeclaration Consumer,
    IReadOnlyList<GitPreviewResolvedReference> References);

public sealed record GitPreviewReferencesSnapshot(IReadOnlyList<GitPreviewResolvedReference> References);

public union ReadPreviewReferencesOutcome(
    GitPreviewReferencesSnapshot,
    DeclarationReadBlocked);

// Owners validate their technical catalogs by accepted IDs, without giving preview a run authority.
public interface IDeclarativePreviewReferenceProvider
{
    Task<ReadPreviewReferencesOutcome> ReadPreviewReferencesAsync(
        ReadPreviewReferencesRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken);
}


public interface IDeclarativeDerivedIdentityProvider
{
    Task<IReadOnlyList<GitPreviewResolvedReference>> ReserveDerivedIdentitiesAsync(
        ReadLifecycleResourceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken);
}

public sealed record GitLifecycleDependencyCycle : IDeclarationFailure;
