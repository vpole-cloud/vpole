using System.Text.Json;
using Pole.Features.GitOps.Primitives;
using MongoDB.Driver;

namespace Pole.Features.GitOps.Contracts;

// Identity fields need replacement; configuration fields can be updated in place.
// References in both projections are resolved IDs, never values from secret storage.
public sealed record DeclarationProjection(
    JsonElement Identity,
    JsonElement Configuration,
    DeclarationVersionToken? Dependencies = null);

public sealed record DeclarationDocumentVersion(
    string DocumentId,
    long Revision);

public sealed record DeclarationVersionToken(IReadOnlyList<DeclarationDocumentVersion> Documents);

public enum DeclarationConfigurationState { Prepared, Configured }

public sealed record CurrentDeclaration(
    string ResourceId,
    DeclarationVersionToken Version,
    DeclarationProjection Projection,
    DeclarationConfigurationState State);

public sealed record CurrentDeclarationMissing;

public sealed record DeclarationReadBlocked(IDeclarationFailure Failure);

public union ReadCurrentDeclarationOutcome(
    CurrentDeclaration,
    CurrentDeclarationMissing,
    DeclarationReadBlocked);

public union ProjectDesiredDeclarationOutcome(
    DeclarationProjection,
    DeclarationReadBlocked);

public sealed record ReadCurrentDeclarationRequest(
    string TenantId,
    ResourceAddress Address,
    GitOperationInput? Operation = null);

public sealed record ProjectDesiredDeclarationRequest(
    string TenantId,
    CompiledDeclaration Declaration,
    GitOperationInput? Operation = null);

public interface IDeclarativeResourcePlanningProvider : IDeclarativeResourceProvider
{
    Task<ReadCurrentDeclarationOutcome> ReadCurrentAsync(
        ReadCurrentDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken);

    Task<ProjectDesiredDeclarationOutcome> ProjectDesiredAsync(
        ProjectDesiredDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken);
}

public sealed record DeclarationPlanningScope(
    string TenantId,
    string SourceId,
    string? AcceptedResourceId,
    DeclarationPlanningAuthority? Authority = null);

public sealed record PlanDeclarationRequest(
    DeclarationPlanningScope Scope,
    CompiledDeclaration Declaration);

public closed record ResourceDeclarationPlan;

public sealed record CreateDeclarationPlan(DeclarationProjection Desired) : ResourceDeclarationPlan;

public sealed record UpdateDeclarationPlan(
    CurrentDeclaration Current,
    DeclarationProjection Desired) : ResourceDeclarationPlan;

public sealed record NoOpDeclarationPlan(
    CurrentDeclaration Current,
    DeclarationProjection Desired) : ResourceDeclarationPlan;

public sealed record BlockedDeclarationPlan(IDeclarationFailure Failure) : ResourceDeclarationPlan;

public sealed record AdoptionDeclarationPlan(
    CurrentDeclaration Current,
    DeclarationProjection Desired) : ResourceDeclarationPlan;

public sealed record ReplacementDeclarationPlan(
    CurrentDeclaration Current,
    DeclarationProjection Desired) : ResourceDeclarationPlan;

// Only the later lifecycle planner may emit removal, after complete snapshot and prune checks.
public sealed record RemovalDeclarationPlan(CurrentDeclaration Current) : ResourceDeclarationPlan;

public sealed record DeclarationPlanningUnsupported(string Kind) : IDeclarationFailure;

public sealed record DeclarationIdentityChanged : IDeclarationFailure;

public sealed record DeclarationReferenceUnavailable(ResourceAddress Address) : IDeclarationFailure;

// An advisory name lookup has no execution authority. Accepted execution always supplies this context.
public sealed record DeclarationPlanningAuthority(
    GitOperationInput Operation,
    string? OwnerSourceId,
    GitResourceMaterialization Materialization);

public sealed record DeclarationExecutionPlan(
    long Revision,
    ResourceDeclarationPlan Decision);

public sealed record FenceDeclarationRequest(
    GitOperationInput Operation,
    DeclarationVersionToken Expected);

public sealed record DeclarationFenced;

public sealed record DeclarationPreconditionsChanged : IDeclarationFailure;

public union FenceDeclarationOutcome(
    DeclarationFenced,
    DeclarationPreconditionsChanged,
    DeclarationReadBlocked);

public interface ITransactionalDeclarativeResourceProvider : IDeclarativeResourcePlanningProvider
{
    Task<FenceDeclarationOutcome> FenceAsync(
        FenceDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken);
}

public sealed record DeclarationDependencyBlocked(
    ResourceAddress Address,
    DeclarationPhase Phase) : IDeclarationFailure;

public sealed record DeclarationManagedResourceMissing : IDeclarationFailure;

public sealed record DeclarationAdoptionRequired : IDeclarationFailure;

public sealed record DeclarationReplacementRequired : IDeclarationFailure;

public sealed record DeclarationPostconditionFailed : IDeclarationFailure;

public sealed record DeclarationReferencesPrepared;

public union PrepareDeclarationReferencesOutcome(
    DeclarationReferencesPrepared,
    DeclarationReadBlocked,
    StaleGitExecution);

// Providers with technical references pin them before the planning transaction starts.
public interface IDeclarativeReferencePreparation
{
    Task<PrepareDeclarationReferencesOutcome> PrepareReferencesAsync(
        GitOperationInput operation,
        CancellationToken cancellationToken);
}
