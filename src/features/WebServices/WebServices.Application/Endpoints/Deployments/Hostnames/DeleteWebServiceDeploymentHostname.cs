using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Domains.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Features.WebServices.Domain;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Validation;

namespace Pole.Features.WebServices.Application.Endpoints;

public static class DeleteWebServiceDeploymentHostname
{
    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapDeleteWebServiceDeploymentHostnameEndpoint()
        {
            app.MapDelete(
                    "/{Id}/deployments/{DeploymentId}/hostnames/{BindingId}",
                    async (
                        IMediator mediator,
                        [AsParameters] DeleteWebServiceDeploymentHostnameRequest request,
                        CancellationToken cancellationToken) =>
                        (await mediator.Send(request, cancellationToken))
                        .ToHttpResult(value => Results.Accepted(value: value)))
                .WithName("DeleteWebServiceDeploymentHostname")
                .Produces<PlacementSummaryDto>(StatusCodes.Status202Accepted);
            return app;
        }
    }
}

public sealed record DeleteWebServiceDeploymentHostnameRequest(
    [FromRoute] string Id,
    [FromRoute] string DeploymentId,
    [FromRoute] string BindingId,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<PlacementSummaryDto>, ITenantWriteOperation;

public sealed class DeleteWebServiceDeploymentHostnameHandler(
    LoadWebServiceById loadWebService,
    IResourceDeploymentSetQueryService deploymentQuery,
    DeleteCustomHostnameBinding deleteCustomHostnameBinding,
    LoadHostnameBindingsForDeploymentInTransaction
        loadHostnameBindingsForDeploymentInTransaction,
    IResourceDeploymentSetMutationWriter deploymentWriter,
    PersistUpdatedWebService persistWebService,
    PersistCurrentWebServiceRelease persistRelease,
    RunMongoTransaction<PlacementSummaryDto> runTransaction,
    TimeProvider timeProvider)
    : IMediatorHandler<DeleteWebServiceDeploymentHostnameRequest, PlacementSummaryDto>
{
    public async Task<Result<PlacementSummaryDto>> HandleAsync(
        DeleteWebServiceDeploymentHostnameRequest request,
        CancellationToken cancellationToken = default)
    {
        var webService = await loadWebService(
            request.TenantId,
            request.Id,
            cancellationToken);
        if (webService is null)
            return Failures.NotFound("Web service not found.", request.Id);

        var deployment = await deploymentQuery.GetExactAsync(
            request.TenantId,
            WebServiceResourceDeployment.ResourceType,
            request.Id,
            request.DeploymentId,
            cancellationToken);
        if (deployment is null)
        {
            return Failures.NotFound(
                "Resource deployment not found.",
                request.DeploymentId);
        }

        return await runTransaction(
            (session, ct) => DeleteAndApplySnapshotAsync(
                request,
                webService,
                deployment,
                session,
                ct),
            cancellationToken);
    }

    private async Task<Result<PlacementSummaryDto>> DeleteAndApplySnapshotAsync(
        DeleteWebServiceDeploymentHostnameRequest request,
        WebService webService,
        PlacementSummaryDto deployment,
        MongoDB.Driver.IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var deleted = await deleteCustomHostnameBinding(
            new DeleteCustomHostnameBindingRequest(
                request.TenantId,
                WebServiceResourceDeployment.ResourceType,
                request.Id,
                request.DeploymentId,
                request.BindingId),
            session,
            cancellationToken);
        if (deleted is not ISuccess)
            return (Failure)deleted;

        var currentBindings = await loadHostnameBindingsForDeploymentInTransaction(
            request.TenantId,
            WebServiceResourceDeployment.ResourceType,
            request.Id,
            request.DeploymentId,
            session,
            cancellationToken);
        var updated = webService.AdvancePlacementDesiredState();
        var applied = await deploymentWriter.RequestExactApplyAsync(
            WebServiceDeploymentWorkflow.ToExactApplyRequest(
                request.TenantId,
                request.Id,
                deployment,
                updated.ActiveRevision,
                replacementIngress: WebServiceDeploymentWorkflow.WithHostnames(
                    deployment,
                    currentBindings.Select(binding => binding.Hostname))),
            session,
            cancellationToken);
        if (!applied.TryGetValue(out var changed, out var failure))
            return failure;

        var advanced = await deploymentWriter.AdvanceResourceRevisionAsync(
            new ResourceDeploymentResourceRevisionRequest(
                request.TenantId,
                WebServiceResourceDeployment.ResourceType,
                request.Id,
                updated.ActiveRevision),
            session,
            cancellationToken);
        if (!advanced.TryGetValue(out var deployments, out failure))
            return failure;

        var persisted = await persistWebService(
            session,
            webService,
            updated,
            updated.ToUpdatedEvent(
                WebServiceDeploymentWorkflow.ToEventDeployments(deployments)),
            cancellationToken);
        if (persisted is not ISuccess)
            return (Failure)persisted;

        var release = WebServiceReleaseFactory.CreatePending(
            updated.TenantId,
            updated.Id,
            updated.ActiveRevision,
            WebServiceReleaseAction.HostnameChanged,
            null,
            null,
            updated.Spec,
            WebServiceDeploymentWorkflow.ToReleaseDeployments(deployments),
            null,
            null,
            new WebServiceReleaseFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                timeProvider.GetUtcNow().UtcDateTime));
        await persistRelease(session, release, cancellationToken);
        return changed;
    }
}

public sealed class DeleteWebServiceDeploymentHostnameValidator
    : AbstractValidator<DeleteWebServiceDeploymentHostnameRequest>
{
    public DeleteWebServiceDeploymentHostnameValidator()
    {
        RuleFor(request => request.Id).MongoObjectId("Web service ID");
        RuleFor(request => request.DeploymentId)
            .MongoObjectId("Resource deployment ID");
        RuleFor(request => request.BindingId)
            .MongoObjectId("Hostname binding ID");
    }
}
