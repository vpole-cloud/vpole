using Pole.Features.GitOps.Contracts;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.WebServices.Application.Common;
using MongoDB.Driver;
using Pole.Features.WebServices.Application.Persistence.Mutations;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Persistence.Lifecycle;

public sealed record WebServiceDeletionAccepted;

public union BeginWebServiceDeletionResult(
    WebServiceDeletionAccepted,
    WebServiceUpdateConflict);

public delegate Task<BeginWebServiceDeletionResult>
    BeginWebServiceDeletionOutcome(
        IClientSessionHandle session,
        WebService current,
        WebService replacement,
        CancellationToken cancellationToken = default);

internal sealed class MongoBeginWebServiceDeletion(
    FenceRegisteredResource fenceRegisteredResource,
    CascadeConnectionsForResourceDeletion cascadeConnections,
    IMongoCollection<WebService> webServices,
    IGitOpsMutations git)
{
    public async Task<BeginWebServiceDeletionResult>
        InvokeOutcomeAsync(
            IClientSessionHandle session,
            WebService current,
            WebService replacement,
            CancellationToken cancellationToken = default)
    {
        WebServicePersistenceGuards.EnsureTransaction(session, "Web service deletion start");
        WebServicePersistenceGuards.EnsureSameIdentity(current, replacement);
        if (!await git.AuthorizeAsync(
            new AuthorizeGitResourceMutationRequest(
                TenantId: current.TenantId,
                Kind: "WebService",
                ResourceId: current.Id),
            session: session,
            ct: cancellationToken))
            return new WebServiceUpdateConflict(current.Id, WebServiceUpdateConflictReason.GitOwnedConfiguration);
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
                ResourceId: current.Id),
            session: session,
            cancellationToken: cancellationToken);
        WebServiceUpdateConflict? cascadeConflict = cascade switch
        {
            ResourceConnectionsCascaded => null,
            ResourceConnectionCascadeConflict => new WebServiceUpdateConflict(current.Id)
        };
        if (cascadeConflict is not null)
            return cascadeConflict;

        var result = await webServices.ReplaceOneAsync(
            session,
            WebServiceFilters.AtRevision(current),
            replacement,
            cancellationToken: cancellationToken);
        return result.ModifiedCount == 1
            ? new WebServiceDeletionAccepted()
            : new WebServiceUpdateConflict(current.Id);
    }
}
