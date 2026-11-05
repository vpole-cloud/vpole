using System.Text.Json;
using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.RedisServices.Application;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application.Mongo;
using Pole.Features.RedisServices.Application.Services;
using Pole.Features.RedisServices.Domain;
using Pole.Workloads;

namespace Pole.Features.RedisServices.Application.Declarations;

internal sealed class DeclarativeRedisProvider(
    IMongoCollection<Redis> resources,
    RedisDeclarationPlanner planner,
    RedisDeclarationReferences references,
    RedisDeclarationExecution execution,
    RedisDeclarationAdoption adoption,
    RedisDeclarationLifecycle lifecycle) : ITransactionalDeclarativeResourceProvider, IDeclarativeReferencePreparation,
    IDeclarativeResourceLifecycleProvider, IDeclarativeLifecycleDependencyProvider,
    IDeclarativeRegisteredResourceProvider, IDeclarativeAdoptionPreviewProvider,
    IDeclarativePreviewReferenceProvider, IDeclarativeLifecycleWarningProvider
{
    public const string ResourceKind = RedisDeclarations.Kind;
    public string Kind => ResourceKind;
    public ResourceTypeId ResourceType => RedisResourceDeployment.ResourceType;
    public int MaximumNameLength => 30;
    public string DependencyOwner => "RedisServices";

    public string DescribeFailure(IDeclarationFailure failure) => RedisDeclarations.DescribeFailure(failure);
    public JsonElement NormalizeAndValidate(JsonElement spec) => RedisDeclarations.NormalizeAndValidate(spec);
    public DeclarationFacts Facts(JsonElement spec) => RedisDeclarations.Facts(spec);
    public IReadOnlyList<DeclarationStep> Steps(
        ResourceAddress address,
        JsonElement spec) =>
        RedisDeclarations.Steps(address, spec);

    public async Task<string?> ResolveAsync(
        ResolveDeclaredResourceRequest request,
        IClientSessionHandle session,
        CancellationToken ct) =>
        (await resources.Find(session, RedisFilters.ByName(request.TenantId, request.Name))
            .FirstOrDefaultAsync(ct))?.Id;

    public Task<ReadCurrentDeclarationOutcome> ReadCurrentAsync(
        ReadCurrentDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken ct) =>
        planner.ReadCurrentAsync(request, session, ct);

    public Task<ProjectDesiredDeclarationOutcome> ProjectDesiredAsync(
        ProjectDesiredDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken ct) =>
        planner.ProjectDesiredAsync(request, session, ct);

    public Task<FenceDeclarationOutcome> FenceAsync(
        FenceDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken ct) =>
        planner.FenceAsync(request, session, ct);

    public Task<PrepareDeclarationReferencesOutcome> PrepareReferencesAsync(
        GitOperationInput operation,
        CancellationToken ct) =>
        references.PrepareReferencesAsync(operation, ct);

    public Task<GitOperationResult> ApplyAsync(
        GitOperation operation,
        CancellationToken ct) =>
        execution.ApplyAsync(operation, ct);

    public Task<PreviewLifecycleAdoptionOutcome> PreviewAdoptionAsync(
        PreviewLifecycleAdoptionRequest request,
        IClientSessionHandle session,
        CancellationToken ct) =>
        adoption.PreviewAdoptionAsync(request, session, ct);

    public Task<ReadPreviewReferencesOutcome> ReadPreviewReferencesAsync(
        ReadPreviewReferencesRequest request,
        IClientSessionHandle session,
        CancellationToken ct) =>
        references.ReadPreviewReferencesAsync(request, session, ct);

    public Task<ReadLifecycleResourceOutcome> ReadLifecycleAsync(
        ReadLifecycleResourceRequest request,
        IClientSessionHandle session,
        CancellationToken ct) =>
        lifecycle.ReadLifecycleAsync(request, session, ct);

    public Task<RemoveLifecycleResourceOutcome> RemoveAsync(
        RemoveLifecycleResourceRequest request,
        IClientSessionHandle session,
        CancellationToken ct) =>
        lifecycle.RemoveAsync(request, session, ct);

    public Task<IReadOnlyList<GitLifecycleDependency>> ReadDependenciesAsync(
        ReadLifecycleDependenciesRequest request,
        IClientSessionHandle session,
        CancellationToken ct) =>
        lifecycle.ReadDependenciesAsync(request, session, ct);

    public Task<FenceDeclarationOutcome> FenceDependenciesAsync(
        FenceLifecycleDependenciesRequest request,
        IClientSessionHandle session,
        CancellationToken ct) =>
        lifecycle.FenceDependenciesAsync(request, session, ct);

    public Task<IReadOnlyList<string>> ReadLifecycleWarningsAsync(
        ReadLifecycleWarningsRequest request,
        IClientSessionHandle session,
        CancellationToken ct) =>
        lifecycle.ReadLifecycleWarningsAsync(request, session, ct);

}
