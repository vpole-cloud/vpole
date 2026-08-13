using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using MongoDB.Driver;
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

public static class AddWebServiceDeploymentHostname
{
    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapAddWebServiceDeploymentHostnameEndpoint()
        {
            app.MapPost(
                    "/{Id}/deployments/{DeploymentId}/hostnames",
                    async (
                        IMediator mediator,
                        [AsParameters] AddWebServiceDeploymentHostnameRequest request,
                        CancellationToken cancellationToken) =>
                        (await mediator.Send(request, cancellationToken))
                        .ToHttpResult(value => Results.Accepted(value: value)))
                .WithName("AddWebServiceDeploymentHostname")
                .Produces<PlacementSummaryDto>(StatusCodes.Status202Accepted);
            return app;
        }
    }
}

public sealed record AddWebServiceDeploymentHostnameRequest(
    [FromRoute] string Id,
    [FromRoute] string DeploymentId,
    [FromBody] AddWebServiceDeploymentHostnameBody Body,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<PlacementSummaryDto>, ITenantWriteOperation;

public sealed record AddWebServiceDeploymentHostnameBody(
    string TenantDomainZoneId,
    string RelativeName);

public sealed class AddWebServiceDeploymentHostnameHandler(
    LoadWebServiceById loadWebService,
    IResourceDeploymentSetQueryService deploymentQuery,
    CreateCustomHostnameBinding createCustomHostnameBinding,
    LoadHostnameBindingsForDeploymentInTransaction
        loadHostnameBindingsForDeploymentInTransaction,
    IResourceDeploymentSetMutationWriter deploymentWriter,
    PersistUpdatedWebService persistWebService,
    PersistCurrentWebServiceRelease persistRelease,
    RunMongoTransaction<PlacementSummaryDto> runTransaction,
    TimeProvider timeProvider)
    : IMediatorHandler<AddWebServiceDeploymentHostnameRequest, PlacementSummaryDto>
{
    public async Task<Result<PlacementSummaryDto>> HandleAsync(
        AddWebServiceDeploymentHostnameRequest request,
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
            async (session, ct) =>
            {
                var created = await createCustomHostnameBinding(
                    new CreateCustomHostnameBindingRequest(
                        request.TenantId,
                        request.Body.TenantDomainZoneId,
                        deployment.DeploymentTargetId,
                        WebServiceResourceDeployment.ResourceType,
                        request.Id,
                        request.DeploymentId,
                        request.Body.RelativeName),
                    session,
                    ct);
                if (!created.TryGetValue(out _, out var failure))
                    return failure;

                return await ApplyBindingSnapshotAsync(
                    request,
                    webService,
                    deployment,
                    session,
                    ct);
            },
            cancellationToken);
    }

    private async Task<Result<PlacementSummaryDto>> ApplyBindingSnapshotAsync(
        AddWebServiceDeploymentHostnameRequest request,
        WebService webService,
        PlacementSummaryDto current,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var snapshots = await loadHostnameBindingsForDeploymentInTransaction(
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
                current,
                updated.ActiveRevision,
                replacementIngress: WebServiceDeploymentWorkflow.WithHostnames(
                    current,
                    snapshots.Select(binding => binding.Hostname))),
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

public sealed class AddWebServiceDeploymentHostnameValidator
    : AbstractValidator<AddWebServiceDeploymentHostnameRequest>
{
    public AddWebServiceDeploymentHostnameValidator()
    {
        RuleFor(request => request.Id).MongoObjectId("Web service ID");
        RuleFor(request => request.DeploymentId)
            .MongoObjectId("Resource deployment ID");
        RuleFor(request => request.Body.TenantDomainZoneId)
            .MongoObjectId("Domain zone ID");
        RuleFor(request => request.Body.RelativeName)
            .NotEmpty()
            .MaximumLength(253);
    }
}
