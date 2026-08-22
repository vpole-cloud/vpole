using FluentValidation;
using System.Collections.Immutable;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Features.WebServices.Domain;
using MongoDB.Bson;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Validation;

namespace Pole.Features.WebServices.Application.Endpoints;

public static class RetryWebServiceDeployment
{
    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapRetryWebServiceDeploymentEndpoint()
        {
            app.MapPost("/{Id}/deployments/{DeploymentId}/retry", async (
                    IMediator mediator,
                    IHttpProblemMapper httpProblems,
                    [AsParameters] RetryWebServiceDeploymentRequest request,
                    CancellationToken cancellationToken) =>
                (await mediator.Send(request, cancellationToken))
                    .ToHttpResult(outcome => outcome switch
                    {
                        PlacementSummaryDto value =>
                            Results.Accepted(value: value),
                        RetryWebServiceDeploymentError error =>
                            httpProblems.Map(error).ToHttpResult()
                    }))
                .WithName("RetryWebServiceDeployment")
                .Produces<PlacementSummaryDto>(StatusCodes.Status202Accepted);
            return app;
        }
    }
}

public sealed record RetryWebServiceDeploymentRequest(
    [FromRoute] string Id,
    [FromRoute] string DeploymentId,
    [FromBody] RetryWebServiceDeploymentBody Body,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<RetryWebServiceDeploymentOutcome>, ITenantWriteOperation;

public sealed record RetryWebServiceDeploymentBody(
    long ExpectedDeploymentRevision,
    string ExpectedOperation);

public sealed class RetryWebServiceDeploymentHandler(
    LoadWebServiceById loadWebService,
    LoadExactResourceDeployment loadExactResourceDeployment,
    RetryExactResourceDeployment retryExactResourceDeployment,
    AdvanceResourceDeploymentRevision advanceResourceDeploymentRevision,
    PersistUpdatedWebServiceOutcome persistWebService,
    PersistCurrentWebServiceRelease persistRelease,
    TimeProvider timeProvider,
    RunMongoOutcomeTransaction<RetryWebServiceDeploymentOutcome> runTransaction)
    : IMediatorOutcomeHandler<
        RetryWebServiceDeploymentRequest,
        RetryWebServiceDeploymentOutcome>
{
    public async Task<RetryWebServiceDeploymentOutcome> HandleAsync(
        RetryWebServiceDeploymentRequest request,
        CancellationToken cancellationToken = default)
    {
        var webService = await loadWebService(
            request.TenantId,
            request.Id,
            cancellationToken);
        if (webService is null)
            return Error(new WebServiceDeploymentMutationStateError(
                new WebServiceNotFound(request.Id)));

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

        var operation = ParseOperation(request.Body.ExpectedOperation);
        return await runTransaction(
            (transaction, ct) => RetryAsync(
                request,
                webService,
                operation,
                transaction,
                ct),
            cancellationToken);
    }

    private async Task<MongoTransactionDecision<RetryWebServiceDeploymentOutcome>> RetryAsync(
        RetryWebServiceDeploymentRequest request,
        WebService webService,
        ResourceDeploymentRequestedOperation operation,
        MongoTransactionContext<RetryWebServiceDeploymentOutcome> transaction,
        CancellationToken cancellationToken)
    {
        var updated = webService.AdvancePlacementDesiredState();
        var retried = await retryExactResourceDeployment(
            new ResourceDeploymentExactRetryRequest(
                request.TenantId,
                WebServiceResourceDeployment.ResourceType,
                request.Id,
                request.DeploymentId,
                request.Body.ExpectedDeploymentRevision,
                operation),
            transaction.Session,
            cancellationToken);
        if (!retried.TryGetValue(out _, out var failure))
            return transaction.Abort(Error(
                new RetryWebServiceDeploymentError(failure)));

        var stamped = await advanceResourceDeploymentRevision(
            new ResourceDeploymentResourceRevisionRequest(
                request.TenantId,
                WebServiceResourceDeployment.ResourceType,
                request.Id,
                updated.ActiveRevision),
            transaction.Session,
            cancellationToken);
        if (!stamped.TryGetValue(out var deployments, out var revisionFailure))
            return transaction.Abort(Error(
                new RetryWebServiceDeploymentError(revisionFailure)));

        var retriedDeployment = deployments.Single(item =>
            item.DeploymentId == request.DeploymentId);
        var retryOperation = operation == ResourceDeploymentRequestedOperation.Delete
            ? WebServiceReleaseDeploymentOperation.Delete
            : WebServiceReleaseDeploymentOperation.Apply;
        var retainedDeployments = deployments.Where(item =>
            item.DeploymentId != request.DeploymentId);
        var eventDeployments = WebServiceDeploymentWorkflow
            .ToEventDeployments(retainedDeployments)
            .AddRange(WebServiceDeploymentWorkflow.ToEventDeployments(
                [retriedDeployment],
                retryOperation))
            .OrderBy(item => item.ResourceDeploymentId, StringComparer.Ordinal)
            .ToImmutableList();
        var persisted = await persistWebService(
            transaction.Session,
            webService,
            updated,
            updated.ToUpdatedEvent(eventDeployments),
            cancellationToken);
        if (!persisted.TryGetValue(out _, out var persistenceError))
            return transaction.Abort(Error(
                new RetryWebServiceDeploymentError(persistenceError)));

        var retainedReleaseEntries = WebServiceDeploymentWorkflow
            .ToReleaseDeployments(retainedDeployments);
        var releaseDeployments = retainedReleaseEntries
            .Concat(WebServiceDeploymentWorkflow.ToReleaseDeployments(
                [retriedDeployment],
                retryOperation))
            .OrderBy(item => item.ResourceDeploymentId, StringComparer.Ordinal)
            .ToArray();
        var release = WebServiceReleaseFactory.CreatePending(
            updated.TenantId,
            updated.Id,
            updated.ActiveRevision,
            WebServiceReleaseAction.RetryDeployment,
            null,
            null,
            updated.Spec,
            releaseDeployments,
            null,
            null,
            new WebServiceReleaseFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                timeProvider.GetUtcNow().UtcDateTime));
        await persistRelease(transaction.Session, release, cancellationToken);
        return transaction.Commit(new RetryWebServiceDeploymentOutcome(retriedDeployment));
    }

    private static ResourceDeploymentRequestedOperation ParseOperation(
        string operation) =>
        string.Equals(operation, "Delete", StringComparison.OrdinalIgnoreCase)
            ? ResourceDeploymentRequestedOperation.Delete
            : ResourceDeploymentRequestedOperation.Apply;

    private static RetryWebServiceDeploymentOutcome Error(
        RetryWebServiceDeploymentError error) =>
        new(error);
}

public sealed class RetryWebServiceDeploymentValidator : AbstractValidator<RetryWebServiceDeploymentRequest>
{
    public RetryWebServiceDeploymentValidator()
    {
        RuleFor(request => request.Id).MongoObjectId("Web service ID");
        RuleFor(request => request.DeploymentId)
            .MongoObjectId("Resource deployment ID");
        RuleFor(request => request.Body.ExpectedDeploymentRevision)
            .GreaterThan(0);
        RuleFor(request => request.Body.ExpectedOperation)
            .Must(value =>
                string.Equals(value, "Apply", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "Delete", StringComparison.OrdinalIgnoreCase))
            .WithMessage("Expected operation must be Apply or Delete.");
    }
}
