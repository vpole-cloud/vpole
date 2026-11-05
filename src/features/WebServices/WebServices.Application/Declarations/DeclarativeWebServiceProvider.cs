using System.Text.Json;
using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.WebServices.Application;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Configuration;
using Pole.Features.WebServices.Application.Hostnames;
using Pole.Features.WebServices.Application.Lifecycle;
using Pole.Features.WebServices.Application.Mongo;
using Pole.Features.WebServices.Domain;
using Pole.Workloads;

namespace Pole.Features.WebServices.Application.Declarations;

internal sealed class DeclarativeWebServiceProvider(
    IMongoCollection<WebService> services,
    WebServiceDeclarationPlanner planner,
    WebServiceDeclarationFences fences,
    WebServiceDeclarationReferences references,
    WebServiceDeclarationExecution execution,
    WebServiceDeclarationAdoption adoption,
    WebServiceDeclarationLifecycle lifecycle) : ITransactionalDeclarativeResourceProvider,
    IDeclarativeReferencePreparation,
    IDeclarativeResourceLifecycleProvider, IDeclarativeRegisteredResourceProvider,
    IDeclarativeLifecycleDependencyProvider, IDeclarativeAdoptionPreviewProvider,
    IDeclarativeResourceAdoptionProvider, IDeclarativePreviewReferenceProvider
{
    public string Kind => WebServiceDeclarations.Kind;
    public ResourceTypeId ResourceType => WebServiceResourceDeployment.ResourceType;
    public int MaximumNameLength => WebServiceDeclarationRules.MaximumNameLength;
    public string DependencyOwner => "WebServices";

    public string DescribeFailure(IDeclarationFailure failure) => WebServiceDeclarations.DescribeFailure(failure);
    public JsonElement NormalizeAndValidate(JsonElement spec) => WebServiceDeclarations.NormalizeAndValidate(spec);
    public DeclarationFacts Facts(JsonElement spec) => WebServiceDeclarations.Facts(spec);
    public IReadOnlyList<DeclarationStep> Steps(
        ResourceAddress address,
        JsonElement spec) =>
        WebServiceDeclarations.Steps(address, spec);

    public async Task<string?> ResolveAsync(
        ResolveDeclaredResourceRequest request,
        IClientSessionHandle session,
        CancellationToken ct) =>
        (await services.Find(
            session,
            WebServiceFilters.ByTechnicalName(request.TenantId, request.Name)).FirstOrDefaultAsync(ct))?.Id;

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
        fences.FenceAsync(request, session, ct);

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

    public Task<AdoptLifecycleResourceOutcome> AdoptAsync(
        AdoptLifecycleResourceRequest request,
        IClientSessionHandle session,
        CancellationToken ct) =>
        adoption.AdoptAsync(request, session, ct);

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

}
