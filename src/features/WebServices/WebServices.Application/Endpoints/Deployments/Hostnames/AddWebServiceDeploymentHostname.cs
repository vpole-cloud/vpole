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
using Pole.Features.WebServices.Application.Persistence.Mutations;
using Pole.Features.WebServices.Application.Persistence.Reads;
using Pole.Features.WebServices.Application.Persistence.Releases;
using Pole.Features.WebServices.Domain;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Validation;

namespace Pole.Features.WebServices.Application.Endpoints.Deployments.Hostnames;

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
                        IHttpProblemMapper httpProblems,
                        [AsParameters] AddWebServiceDeploymentHostnameRequest request,
                        CancellationToken cancellationToken) =>
                        (await mediator.Send(request, cancellationToken))
                        .ToHttpResult(outcome => outcome switch
                        {
                            PlacementSummaryDto value => Results.Accepted(value: value),
                            WebServiceDeploymentHostnameError error =>
                                httpProblems.Map(error).ToHttpResult()
                        }))
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
    : IMediatorRequest<AddWebServiceDeploymentHostnameOutcome>, ITenantWriteOperation;

public sealed record AddWebServiceDeploymentHostnameBody(
    string TenantDomainZoneId,
    string RelativeName);

public union AddWebServiceDeploymentHostnameOutcome(
    PlacementSummaryDto,
    WebServiceDeploymentHostnameError);

public sealed class AddWebServiceDeploymentHostnameHandler(
    LoadWebServiceById loadWebService,
    LoadExactResourceDeployment loadExactResourceDeployment,
    CreateCustomHostnameBindingOutcome createCustomHostnameBinding,
    LoadHostnameBindingsForDeploymentInTransaction
        loadHostnameBindingsForDeploymentInTransaction,
    RequestExactResourceDeploymentApply requestExactResourceDeploymentApply,
    AdvanceResourceDeploymentRevision advanceResourceDeploymentRevision,
    PersistUpdatedWebServiceOutcome persistWebService,
    PersistCurrentWebServiceRelease persistRelease,
    RunMongoOutcomeTransaction<AddWebServiceDeploymentHostnameOutcome> runTransaction,
    TimeProvider timeProvider)
    : IMediatorOutcomeHandler<
        AddWebServiceDeploymentHostnameRequest,
        AddWebServiceDeploymentHostnameOutcome>
{
    public async Task<AddWebServiceDeploymentHostnameOutcome> HandleAsync(
        AddWebServiceDeploymentHostnameRequest request,
        CancellationToken cancellationToken = default)
    {
        var webService = await loadWebService(
            request.TenantId,
            request.Id,
            cancellationToken);
        if (webService is null)
        {
            return Error(new WebServiceDeploymentMutationStateError(
                new WebServiceNotFound(request.Id)));
        }

        var deployment = await loadExactResourceDeployment(
            request.TenantId,
            WebServiceResourceDeployment.ResourceType,
            request.Id,
            request.DeploymentId,
            cancellationToken);
        if (deployment is null)
        {
            return Error(new WebServiceDeploymentMutationStateError(
                new WebServiceResourceDeploymentNotFound(
                    request.Id,
                    request.DeploymentId)));
        }

        return await runTransaction(
            (transaction, ct) => CreateAndApplySnapshotAsync(
                request,
                webService,
                deployment,
                transaction,
                ct),
            cancellationToken);
    }

    private async Task<MongoTransactionDecision<AddWebServiceDeploymentHostnameOutcome>>
        CreateAndApplySnapshotAsync(
            AddWebServiceDeploymentHostnameRequest request,
            WebService webService,
            PlacementSummaryDto deployment,
            MongoTransactionContext<AddWebServiceDeploymentHostnameOutcome> transaction,
            CancellationToken cancellationToken)
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
            transaction.Session,
            cancellationToken);
        if (!created.TryGetValue(out _, out var createdError))
            return Abort(transaction, new WebServiceDeploymentHostnameError(createdError));

        return await ApplyBindingSnapshotAsync(
            request,
            webService,
            deployment,
            transaction,
            cancellationToken);
    }

    private async Task<MongoTransactionDecision<AddWebServiceDeploymentHostnameOutcome>>
        ApplyBindingSnapshotAsync(
        AddWebServiceDeploymentHostnameRequest request,
        WebService webService,
        PlacementSummaryDto current,
        MongoTransactionContext<AddWebServiceDeploymentHostnameOutcome> transaction,
        CancellationToken cancellationToken)
    {
        var snapshots = await loadHostnameBindingsForDeploymentInTransaction(
            request.TenantId,
            WebServiceResourceDeployment.ResourceType,
            request.Id,
            request.DeploymentId,
            transaction.Session,
            cancellationToken);
        var updated = webService.AdvancePlacementDesiredState();
        var applied = await requestExactResourceDeploymentApply(
            WebServiceDeploymentWorkflow.ToExactApplyRequest(
                request.TenantId,
                request.Id,
                current,
                updated.ActiveRevision,
                replacementIngress: WebServiceDeploymentWorkflow.WithHostnames(
                    current,
                    snapshots.Select(binding => binding.Hostname))),
            transaction.Session,
            cancellationToken);
        if (!applied.TryGetValue(out var changed, out var failure))
            return Abort(transaction, new WebServiceDeploymentHostnameError(failure));

        var advanced = await advanceResourceDeploymentRevision(
            new ResourceDeploymentResourceRevisionRequest(
                request.TenantId,
                WebServiceResourceDeployment.ResourceType,
                request.Id,
                updated.ActiveRevision),
            transaction.Session,
            cancellationToken);
        if (!advanced.TryGetValue(out var deployments, out var revisionError))
            return Abort(transaction, new WebServiceDeploymentHostnameError(revisionError));

        var persisted = await persistWebService(
            transaction.Session,
            webService,
            updated,
            updated.ToUpdatedEvent(
                WebServiceDeploymentWorkflow.ToEventDeployments(deployments)),
            cancellationToken);
        if (!persisted.TryGetValue(out _, out var persistenceError))
            return Abort(transaction, new WebServiceDeploymentHostnameError(persistenceError));

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
        await persistRelease(transaction.Session, release, cancellationToken);
        return transaction.Commit(new AddWebServiceDeploymentHostnameOutcome(changed));
    }

    private static AddWebServiceDeploymentHostnameOutcome Error(
        WebServiceDeploymentHostnameError error) => new(error);

    private static MongoTransactionDecision<AddWebServiceDeploymentHostnameOutcome> Abort(
        MongoTransactionContext<AddWebServiceDeploymentHostnameOutcome> transaction,
        WebServiceDeploymentHostnameError error) =>
        transaction.Abort(Error(error));
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
