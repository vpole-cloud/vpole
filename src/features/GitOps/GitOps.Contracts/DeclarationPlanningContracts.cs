using System.Text.Json;
using MongoDB.Driver;

namespace Pole.Features.GitOps.Contracts;

// Identity fields need replacement; configuration fields can be updated in place.
// References in both projections are resolved IDs, never values from secret storage.
public sealed record DeclarationProjection(
    JsonElement Identity,
    JsonElement Configuration);

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
    ResourceAddress Address);

public sealed record ProjectDesiredDeclarationRequest(
    string TenantId,
    CompiledDeclaration Declaration);

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
    string? AcceptedResourceId);

public sealed record PlanDeclarationRequest(
    DeclarationPlanningScope Scope,
    CompiledDeclaration Declaration);

public closed record ResourceDeclarationPlan;

public sealed record CreateDeclarationPlan(DeclarationProjection Desired) : ResourceDeclarationPlan;

public sealed record UpdateDeclarationPlan(
    CurrentDeclaration Current,
    DeclarationProjection Desired) : ResourceDeclarationPlan;

public sealed record NoOpDeclarationPlan(CurrentDeclaration Current) : ResourceDeclarationPlan;

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
