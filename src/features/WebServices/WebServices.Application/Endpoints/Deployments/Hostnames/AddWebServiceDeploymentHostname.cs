using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Dns;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Domains.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Configuration;
using Pole.Features.WebServices.Application.SourceDelivery;
using Pole.Features.WebServices.Application.Connections;
using Pole.Features.WebServices.Application.Queries;
using Pole.Features.WebServices.Application.Releases;
using Pole.Features.WebServices.Domain;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Validation;

using static Pole.Features.WebServices.Application.Common.WebServiceHttpMappings;

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
                        [AsParameters] AddWebServiceDeploymentHostnameRequest request,
                        CancellationToken cancellationToken) =>
                        (await mediator.Send(request, cancellationToken))
                        .ToHttpResult(outcome => outcome switch
                        {
                            PlacementSummaryDto value => Results.Accepted(value: value),
                            WebServiceDeploymentHostnameError error =>
                                MapError(error)
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
    [FromTenantHeader] string TenantId)
    : IMediatorRequest<AddWebServiceDeploymentHostnameOutcome>, ITenantWriteOperation;

public sealed record AddWebServiceDeploymentHostnameBody(
    string TenantDomainZoneId,
    string RelativeName,
    string TechnicalName);

public union AddWebServiceDeploymentHostnameOutcome(
    PlacementSummaryDto,
    WebServiceDeploymentHostnameError);

public sealed class AddWebServiceDeploymentHostnameHandler(
    LoadWebServiceById loadWebService,
    LoadExactResourceDeployment loadExactResourceDeployment,
    CreateCustomHostnameBinding createCustomHostnameBinding,
    LoadHostnameBindingsForDeploymentInTransaction
        loadHostnameBindingsForDeploymentInTransaction,
    RequestResourceDeploymentSetApply requestResourceDeploymentSetApply,
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
        if (webService.Exposure.Mode is WebServiceExposureMode.Private ||
            webService.Lifecycle is not (ActiveWebServiceLifecycle or AwaitingFirstArtifactWebServiceLifecycle))
        {
            return Error(new WebServiceHostnameAttachmentUnavailable(webService.Id));
        }

        var deployment = await loadExactResourceDeployment(
            new LoadExactResourceDeploymentRequest(
                TenantId: request.TenantId,
                ResourceType: WebServiceResourceDeployment.ResourceType,
                ResourceId: request.Id,
                DeploymentId: request.DeploymentId),
            cancellationToken: cancellationToken);
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
                MongoDB.Bson.ObjectId.GenerateNewId().ToString(),
                request.TenantId,
                request.Body.TenantDomainZoneId,
                deployment.DeploymentTargetId,
                WebServiceResourceDeployment.ResourceType,
                request.Id,
                request.DeploymentId,
                request.Body.RelativeName,
                request.Body.TechnicalName),
            transaction.Session,
            cancellationToken);
        if (created is CustomHostnameBindingError createdError)
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
            new LoadHostnameBindingsForDeploymentInTransactionRequest(
                TenantId: request.TenantId,
                ResourceType: WebServiceResourceDeployment.ResourceType,
                ResourceId: request.Id,
                ResourceDeploymentId: request.DeploymentId),
            session: transaction.Session,
            cancellationToken: cancellationToken);
        var updated = webService.AdvancePlacementDesiredState();
        var applied = await requestResourceDeploymentSetApply(
            new ResourceDeploymentSetApplyRequest(
                request.TenantId,
                WebServiceResourceDeployment.ResourceType,
                request.Id,
                updated.ActiveRevision,
                [new(current.DeploymentId, current.DeploymentRevision, current.Compute.Profile,
                    new ReplaceIngress(WebServiceDeploymentWorkflow.WithHostnames(
                        current, snapshots.Select(binding => binding.Hostname))))],
                updated.Lifecycle is AwaitingFirstArtifactWebServiceLifecycle
                    ? ResourceDeploymentActivation.KeepPrepared : ResourceDeploymentActivation.Reconcile),
            transaction.Session,
            cancellationToken);
        if (applied is ResourceDeploymentMutationError failure)
            return Abort(transaction, new WebServiceDeploymentHostnameError(failure));
        var changed = ((MutatedResourceDeployments)applied.Value).Placements
            .Single(item => item.DeploymentId == request.DeploymentId);

        var advanced = await advanceResourceDeploymentRevision(
            new ResourceDeploymentResourceRevisionRequest(
                request.TenantId,
                WebServiceResourceDeployment.ResourceType,
                request.Id,
                updated.ActiveRevision),
            transaction.Session,
            cancellationToken);
        if (advanced is AdvanceResourceDeploymentRevisionError revisionError)
            return Abort(transaction, new WebServiceDeploymentHostnameError(revisionError));
        var deployments = ((MutatedResourceDeployments)advanced.Value).Placements;

        var persisted = await persistWebService(
            transaction.Session,
            webService,
            updated,
            updated.ToUpdatedEvent(
                WebServiceDeploymentWorkflow.ToEventDeployments(deployments)),
            cancellationToken);
        if (persisted is WebServiceUpdateConflict persistenceError)
            return Abort(transaction, new WebServiceDeploymentHostnameError(persistenceError));
        if (persisted is WebServiceConnectionConflict connectionConflict)
            return Abort(transaction, new WebServiceDeploymentHostnameError(connectionConflict));
        if (persisted is WebServiceDependencyUnavailable dependencyUnavailable)
            return Abort(transaction, new WebServiceDeploymentHostnameError(dependencyUnavailable));

        if (updated.Lifecycle is ActiveWebServiceLifecycle)
        {
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
                    timeProvider.GetUtcNow().UtcDateTime),
                updated.Exposure);
            await persistRelease(transaction.Session, release, cancellationToken);
        }

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
        RuleFor(request => request.Body.TechnicalName)
            .NotEmpty().Must(Pole.Workloads.ResourceTechnicalName.IsValid);
        RuleFor(request => request.Body.RelativeName)
            .NotEmpty()
            .MaximumLength(253)
            .Must(IsValidRelativeName);
    }

    private static bool IsValidRelativeName(string value)
    {
        try
        {
            RelativeDnsName.Create(value);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
