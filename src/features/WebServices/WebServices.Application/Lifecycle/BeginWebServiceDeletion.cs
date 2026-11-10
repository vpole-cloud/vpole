using MongoDB.Driver;
using Pole.Features.Domains.Contracts;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Configuration;
using Pole.Features.WebServices.Application.Mongo;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Lifecycle;

public sealed record WebServiceDeletionAccepted;

public union BeginWebServiceDeletionResult(
    WebServiceDeletionAccepted,
    WebServiceUpdateConflict,
    WebServiceHostnameAttachmentRemovalRequired);

public sealed record BeginWebServiceDeletionRequest(
    WebService Current,
    WebService Replacement,
    GitLifecycleAuthority? Lifecycle = null,
    IReadOnlyList<ResourceConnectionPlanningVersion>? ReviewedConnections = null
);

public delegate Task<BeginWebServiceDeletionResult> BeginWebServiceDeletionOutcome(
    IClientSessionHandle session,
    WebService current,
    WebService replacement,
    CancellationToken cancellationToken = default);

internal sealed class MongoBeginWebServiceDeletion(
    FenceRegisteredResource fenceRegisteredResource,
    CascadeConnectionsForResourceDeletion cascadeConnections,
    LoadHostnameBindingsForResourceInTransaction loadBindings,
    IMongoCollection<WebService> webServices,
    IGitOpsMutations git
)
{
    public Task<BeginWebServiceDeletionResult> InvokeOutcomeAsync(
        IClientSessionHandle session,
        WebService current,
        WebService replacement,
        CancellationToken cancellationToken = default) => ExecuteAsync(
        new BeginWebServiceDeletionRequest(current, replacement),
        session,
        cancellationToken);

    internal async Task<BeginWebServiceDeletionResult> ExecuteAsync(
        BeginWebServiceDeletionRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var current = request.Current;
        var replacement = request.Replacement;
        WebServicePersistenceGuards.EnsureTransaction(session, "Web service deletion start");
        WebServicePersistenceGuards.EnsureSameIdentity(current, replacement);
        if (!await git.AuthorizeAsync(
                new AuthorizeGitResourceMutationRequest(
                    TenantId: current.TenantId,
                    Kind: "WebService",
                    ResourceId: current.Id,
                    Lifecycle: request.Lifecycle,
                    LifecycleEffect: GitLifecycleMutationEffect.Remove),
                session: session,
                ct: cancellationToken))
            return new WebServiceUpdateConflict(current.Id, WebServiceUpdateConflictReason.GitOwnedConfiguration);
        var bindings = await loadBindings(
            new LoadHostnameBindingsForResourceInTransactionRequest(
                current.TenantId,
                WebServiceResourceDeployment.ResourceType,
                current.Id),
            session,
            cancellationToken);
        foreach (var binding in bindings.Where(item => item.Kind == HostnameBindingKinds.Custom))
            if (request.Lifecycle is not null || !await git.AuthorizeAsync(
                    new AuthorizeGitResourceMutationRequest(current.TenantId, "HostnameBinding", binding.Id),
                    session,
                    cancellationToken))
                return new WebServiceHostnameAttachmentRemovalRequired(current.Id, binding.Id);

        var registration = await fenceRegisteredResource(
            new FenceRegisteredResourceRequest(
                TenantId: current.TenantId,
                ResourceTypeId: WebServiceResourceDeployment.ResourceType,
                ResourceId: current.Id),
            session: session,
            cancellationToken: cancellationToken);
        if (registration is null)
            return new WebServiceUpdateConflict(current.Id);
        var cascade = await cascadeConnections(
            new CascadeConnectionsForResourceDeletionRequest(
                TenantId: current.TenantId,
                ResourceType: WebServiceResourceDeployment.ResourceType,
                ResourceId: current.Id,
                ReviewedConnections: request.ReviewedConnections),
            session: session,
            cancellationToken: cancellationToken);
        var cascadeConflict = cascade switch
        {
            ResourceConnectionsCascaded => null,
            ResourceConnectionCascadeConflict => new WebServiceUpdateConflict(current.Id),
        };
        if (cascadeConflict is not null)
            return cascadeConflict;

        var result = await webServices.ReplaceOneAsync(
            session,
            WebServiceFilters.AtPlanningVersion(current),
            replacement,
            cancellationToken: cancellationToken);
        return result.ModifiedCount == 1 ? new WebServiceDeletionAccepted() : new WebServiceUpdateConflict(current.Id);
    }
}
