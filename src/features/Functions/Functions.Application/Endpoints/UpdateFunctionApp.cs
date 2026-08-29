using System.Collections.Immutable;
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
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Persistence.Contracts;
using Pole.Features.Functions.Application.Persistence.Mutations;
using Pole.Features.Functions.Application.Persistence.Reads;
using Pole.Features.Functions.Domain;
using Pole.Features.Quotas.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Validation;

namespace Pole.Features.Functions.Application.Endpoints;

public static class UpdateFunctionApp
{
    public static IEndpointRouteBuilder MapUpdateFunctionAppEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPut("/{Id}", async (
                IMediator mediator,
                IHttpProblemMapper httpProblems,
                [AsParameters] UpdateFunctionAppRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    UpdateFunctionAppResponse response => Results.Accepted(value: response),
                    UpdateFunctionAppError error => httpProblems.Map(error).ToHttpResult()
                });
            })
            .WithName("UpdateFunctionApp")
            .Produces<UpdateFunctionAppResponse>(StatusCodes.Status202Accepted);
        return app;
    }
}

public sealed record UpdateFunctionAppRequest(
    [FromRoute] string Id,
    [FromBody] UpdateFunctionAppRequestBody Body,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<UpdateFunctionAppOutcome>, ITenantWriteOperation;

public sealed record UpdateFunctionAppRequestBody(
    FunctionRevisionRequest Spec,
    ImmutableHashSet<string> Tags);

public sealed record UpdateFunctionAppResponse(
    string FunctionAppId,
    string RevisionId,
    int RevisionNumber);

public union UpdateFunctionAppError(
    FunctionAppNotFound,
    FunctionAppResourceGroupNotFound,
    FunctionAppResourceGroupDeleting,
    FunctionAppHasPendingRevision,
    FunctionAppIsNotEnabled,
    PersistFunctionAppRevisionError,
    QuotaExecutionError);

public union UpdateFunctionAppOutcome(
    UpdateFunctionAppResponse,
    UpdateFunctionAppError);

public sealed class UpdateFunctionAppHandler(
    LoadFunctionAppById loadFunctionAppById,
    LoadFunctionAppRevisionReferences loadFunctionAppRevisionReferences,
    PersistFunctionAppRevision persistFunctionAppRevision,
    ResolveFunctionAppResourceGroupPlacement resolveResourceGroupPlacement,
    IQuotaAdmissionExecutor quotaAdmissionExecutor,
    TimeProvider timeProvider)
    : IMediatorOutcomeHandler<UpdateFunctionAppRequest, UpdateFunctionAppOutcome>
{
    public async Task<UpdateFunctionAppOutcome> HandleAsync(
        UpdateFunctionAppRequest request,
        CancellationToken cancellationToken = default)
    {
        var app = await loadFunctionAppById(
            request.TenantId,
            request.Id,
            cancellationToken);
        if (app is null)
            return new UpdateFunctionAppError(new FunctionAppNotFound(request.Id));
        if (app.PendingRevisionId is not null)
            return new UpdateFunctionAppError(new FunctionAppHasPendingRevision(app.Id));
        if (app.DesiredState != FunctionAppDesiredState.Enabled)
            return new UpdateFunctionAppError(new FunctionAppIsNotEnabled(app.Id));

        var placementResolution = await resolveResourceGroupPlacement(
            app.TenantId,
            app.ResourceGroupId,
            cancellationToken);
        ResourceGroupPlacement placement;
        switch (placementResolution)
        {
            case ResourceGroupPlacement value:
                placement = value;
                break;
            case FunctionAppResourceGroupNotFound notFound:
                return new UpdateFunctionAppError(notFound);
            case FunctionAppResourceGroupDeleting deleting:
                return new UpdateFunctionAppError(deleting);
            default:
                throw new InvalidOperationException(
                    "Resource group placement resolution returned an unsupported outcome.");
        }

        var references = await loadFunctionAppRevisionReferences(app, cancellationToken);
        if (app.ActiveRevisionId is not null && references.ActiveRevision is null)
        {
            throw new InvalidOperationException(
                "Function App references an active revision that does not exist.");
        }

        var requestedSpec = request.Body.Spec.ToDomain().ToRevisionSpec(placement.KubernetesNamespace);
        var handlers = FunctionTriggerReconciler.Reconcile(
            references.ActiveRevision?.Spec.Handlers ?? [],
            requestedSpec.Handlers);
        var candidateSpec = requestedSpec with { Handlers = handlers };
        var candidate = FunctionRevision.CreateProvisioning(
            ObjectId.GenerateNewId().ToString(),
            app.TenantId,
            app.Id,
            app.NextRevisionNumber,
            candidateSpec,
            timeProvider.GetUtcNow());
        var admittedQuota = FunctionQuotaAllocation.Peak(
            app.AdmittedQuota,
            FunctionQuotaAllocation.FromRevision(candidateSpec));

        return await quotaAdmissionExecutor.ExecuteOutcomeAsync<
            FunctionQuotaAllocation,
            UpdateFunctionAppOutcome>(
            app.TenantId,
            FunctionAppQuotaResourceKey.Create(app.Id),
            new UpdateQuotaMutation<FunctionQuotaAllocation>(app.AdmittedQuota, admittedQuota),
            quotaError => new UpdateFunctionAppError(quotaError),
            (transaction, ct) => PersistAsync(
                transaction,
                app,
                candidate,
                admittedQuota,
                request.Body.Tags,
                persistFunctionAppRevision,
                timeProvider.GetUtcNow(),
                ct),
            cancellationToken);
    }

    private static async Task<QuotaAdmissionTransactionDecision<UpdateFunctionAppOutcome>> PersistAsync(
        QuotaAdmissionTransactionContext<UpdateFunctionAppOutcome> transaction,
        FunctionApp current,
        FunctionRevision candidate,
        FunctionQuotaAllocation admittedQuota,
        ImmutableHashSet<string> tags,
        PersistFunctionAppRevision persistFunctionAppRevision,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken)
    {
        var persisted = await persistFunctionAppRevision(
            transaction.Session,
            current,
            candidate,
            admittedQuota,
            tags,
            updatedAtUtc,
            cancellationToken);
        return persisted.Match(
            revision => transaction.Commit(new UpdateFunctionAppResponse(
                current.Id,
                revision.Id,
                revision.RevisionNumber)),
            error => transaction.Abort(new UpdateFunctionAppError(error)));
    }
}

public sealed class UpdateFunctionAppRequestValidator : AbstractValidator<UpdateFunctionAppRequest>
{
    public UpdateFunctionAppRequestValidator()
    {
        RuleFor(request => request.Id).MongoObjectId("Function App ID");
        RuleFor(request => request.Body).NotNull().SetValidator(new UpdateFunctionAppRequestBodyValidator());
    }
}

public sealed class UpdateFunctionAppRequestBodyValidator : AbstractValidator<UpdateFunctionAppRequestBody>
{
    public UpdateFunctionAppRequestBodyValidator()
    {
        RuleFor(body => body.Spec).NotNull().SetValidator(new FunctionRevisionSpecInputValidator());
        RuleFor(body => body.Tags).NotNull();
    }
}
