using System.Text.Json;
using MongoDB.Driver;
using Pole.Features.Functions.Application;
using Pole.Features.Functions.Application.Mongo;
using Pole.Features.GitOps.Contracts;
using Pole.Features.Secrets.Contracts;

namespace Pole.Features.Functions.Application.Declarations;

public enum FunctionDeclarationError
{
    AcceptedSnapshotRequired,
    ResourceDeleting,
    ResourceGroupUnavailable,
    IsolationScopeUnavailable,
    DeploymentTargetUnavailable,
    ComputeProfileUnavailable,
    PlacementUnavailable,
    ResourceRegistrationUnavailable,
    QuotaUnavailable,
    ExplicitAdoptionRequired,
    UnexpectedOperation,
    FunctionRouteUnavailable,
    ConfigurationUnavailable,
    PendingRevision,
    SecretReferenceUnavailable
}

public sealed record FunctionDeclarationRejected(FunctionDeclarationError Reason) : IDeclarationFailure;

public sealed record FunctionDeclarationSecretRejected(ConsumerSecretReferenceError Reason) : IDeclarationFailure;

internal sealed class DeclarativeFunctionProvider(
    FunctionDeclarationPlanning planning,
    FunctionDeclarationReferences references,
    FunctionDeclarationExecution execution,
    FunctionDeclarationLifecycle lifecycle,
    FunctionDeclarationAdoption adoption) : ITransactionalDeclarativeResourceProvider,
    IDeclarativeReferencePreparation,
    IDeclarativeRegisteredResourceProvider,
    IDeclarativeResourceLifecycleProvider,
    IDeclarativeLifecycleDependencyProvider,
    IDeclarativePreviewReferenceProvider,
    IDeclarativeAdoptionPreviewProvider
{
    public const string ResourceKind = FunctionDeclarations.Kind;
    public string Kind => ResourceKind;
    public int MaximumNameLength => 63;
    public Pole.Workloads.ResourceTypeId ResourceType => FunctionDeclarations.ResourceType;
    public string DependencyOwner => lifecycle.DependencyOwner;

    public string DescribeFailure(IDeclarationFailure failure) => FunctionDeclarations.DescribeFailure(failure);
    public JsonElement NormalizeAndValidate(JsonElement spec) => FunctionDeclarations.NormalizeAndValidate(spec);
    public DeclarationFacts Facts(JsonElement spec) => FunctionDeclarations.Facts(spec);
    public IReadOnlyList<DeclarationStep> Steps(ResourceAddress address, JsonElement spec) =>
        FunctionDeclarations.Steps(address, spec);

    public Task<string?> ResolveAsync(ResolveDeclaredResourceRequest request,
        IClientSessionHandle session, CancellationToken ct) => planning.ResolveAsync(request, session, ct);

    public Task<ReadCurrentDeclarationOutcome> ReadCurrentAsync(ReadCurrentDeclarationRequest request,
        IClientSessionHandle session, CancellationToken ct) => planning.ReadCurrentAsync(request, session, ct);

    public Task<ProjectDesiredDeclarationOutcome> ProjectDesiredAsync(ProjectDesiredDeclarationRequest request,
        IClientSessionHandle session, CancellationToken ct) => planning.ProjectDesiredAsync(request, session, ct);

    public Task<FenceDeclarationOutcome> FenceAsync(FenceDeclarationRequest request,
        IClientSessionHandle session, CancellationToken ct) => planning.FenceAsync(request, session, ct);

    public Task<PrepareDeclarationReferencesOutcome> PrepareReferencesAsync(GitOperationInput operation,
        CancellationToken ct) => references.PrepareReferencesAsync(operation, ct);

    public Task<ReadPreviewReferencesOutcome> ReadPreviewReferencesAsync(ReadPreviewReferencesRequest request,
        IClientSessionHandle session, CancellationToken ct) => references.ReadPreviewReferencesAsync(request,
            session, ct);

    public Task<GitOperationResult> ApplyAsync(GitOperation operation, CancellationToken ct) =>
        execution.ApplyAsync(operation, ct);

    public Task<ReadLifecycleResourceOutcome> ReadLifecycleAsync(ReadLifecycleResourceRequest request,
        IClientSessionHandle session, CancellationToken ct) => lifecycle.ReadLifecycleAsync(request, session, ct);

    public Task<RemoveLifecycleResourceOutcome> RemoveAsync(RemoveLifecycleResourceRequest request,
        IClientSessionHandle session, CancellationToken ct) => lifecycle.RemoveAsync(request, session, ct);

    public Task<IReadOnlyList<GitLifecycleDependency>> ReadDependenciesAsync(ReadLifecycleDependenciesRequest request,
        IClientSessionHandle session, CancellationToken ct) => lifecycle.ReadDependenciesAsync(request, session, ct);

    public Task<FenceDeclarationOutcome> FenceDependenciesAsync(FenceLifecycleDependenciesRequest request,
        IClientSessionHandle session, CancellationToken ct) => lifecycle.FenceDependenciesAsync(request, session, ct);

    public Task<PreviewLifecycleAdoptionOutcome> PreviewAdoptionAsync(PreviewLifecycleAdoptionRequest request,
        IClientSessionHandle session, CancellationToken ct) => adoption.PreviewAdoptionAsync(request, session, ct);
}
