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
using Pole.Features.Functions.Domain;
using Pole.Features.Quotas.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.Functions.Application.Endpoints;

public static class UpdateFunctionApp
{
    public static IEndpointRouteBuilder MapUpdateFunctionAppEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPut("/{Id}", async (
                IMediator mediator,
                [AsParameters] UpdateFunctionAppRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(response => Results.Accepted(value: response));
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
    : IMediatorRequest<UpdateFunctionAppResponse>, ITenantWriteOperation;

public sealed record UpdateFunctionAppRequestBody(
    FunctionRevisionRequest Spec,
    ImmutableHashSet<string> Tags);

public sealed record UpdateFunctionAppResponse(
    string FunctionAppId,
    string RevisionId,
    int RevisionNumber);

public sealed class UpdateFunctionAppHandler(
    IFunctionAppDbContext dbContext,
    IResourceGroupLookup resourceGroupLookup,
    IQuotaAdmissionExecutor quotaAdmissionExecutor,
    TimeProvider timeProvider)
    : IMediatorHandler<UpdateFunctionAppRequest, UpdateFunctionAppResponse>
{
    public async Task<Result<UpdateFunctionAppResponse>> HandleAsync(
        UpdateFunctionAppRequest request,
        CancellationToken cancellationToken = default)
    {
        var identityFilter = Builders<FunctionApp>.Filter.And(
            Builders<FunctionApp>.Filter.ForTenant(request.TenantId),
            Builders<FunctionApp>.Filter.Eq(app => app.Id, request.Id));
        var app = await dbContext.FunctionApps.Find(identityFilter).FirstOrDefaultAsync(cancellationToken);
        if (app is null)
            return Failures.NotFound("Function App not found.", request.Id);
        if (app.PendingRevisionId is not null)
            return Failures.Conflict("Function App already has a pending revision.");
        if (app.DesiredState != FunctionAppDesiredState.Enabled)
            return Failures.Conflict("Function App is not enabled.");

        var placementResult = await resourceGroupLookup.GetPlacementAsync(
            app.TenantId,
            app.ResourceGroupId,
            cancellationToken);
        if (!placementResult.TryGetValue(out var placement, out var placementFailure))
            return placementFailure;

        FunctionRevision? activeRevision = null;
        if (app.ActiveRevisionId is not null)
        {
            activeRevision = await dbContext.FunctionRevisions.Find(
                    revision => revision.TenantId == app.TenantId &&
                                revision.FunctionAppId == app.Id &&
                                revision.Id == app.ActiveRevisionId)
                .FirstOrDefaultAsync(cancellationToken);
            if (activeRevision is null)
                return Failures.Conflict("The active Function revision is missing.");
        }

        var requestedSpec = request.Body.Spec.ToDomain().ToRevisionSpec(placement.KubernetesNamespace);
        var handlers = FunctionTriggerReconciler.Reconcile(
            activeRevision?.Spec.Handlers ?? [],
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

        return await quotaAdmissionExecutor.ExecuteAsync<FunctionQuotaAllocation, UpdateFunctionAppResponse>(
            app.TenantId,
            FunctionAppQuotaResourceKey.Create(app.Id),
            new UpdateQuotaMutation<FunctionQuotaAllocation>(app.AdmittedQuota, admittedQuota),
            async (session, transactionCancellationToken) =>
            {
                var casFilter = Builders<FunctionApp>.Filter.And(
                    identityFilter,
                    Builders<FunctionApp>.Filter.Eq(current => current.PendingRevisionId, null),
                    Builders<FunctionApp>.Filter.Eq(current => current.NextRevisionNumber, app.NextRevisionNumber));
                var update = Builders<FunctionApp>.Update
                    .Set(current => current.PendingRevisionId, candidate.Id)
                    .Set(current => current.NextRevisionNumber, checked(app.NextRevisionNumber + 1))
                    .Set(current => current.Status, app.ActiveRevisionId is null
                        ? FunctionAppStatus.Provisioning
                        : FunctionAppStatus.Updating)
                    .Set(current => current.AdmittedQuota, admittedQuota)
                    .Set(current => current.Tags, request.Body.Tags)
                    .Set(current => current.UpdatedAtUtc, timeProvider.GetUtcNow());
                var updateResult = await dbContext.FunctionApps.UpdateOneAsync(
                    session,
                    casFilter,
                    update,
                    cancellationToken: transactionCancellationToken);
                if (updateResult.MatchedCount != 1)
                    return Failures.Conflict("Function App revision changed concurrently.");

                await dbContext.FunctionRevisions.InsertOneAsync(
                    session,
                    candidate,
                    cancellationToken: transactionCancellationToken);
                await dbContext.StoreEventAsync(
                    candidate.ToRevisionCreatedEvent(),
                    session,
                    transactionCancellationToken);
                return new UpdateFunctionAppResponse(app.Id, candidate.Id, candidate.RevisionNumber);
            },
            cancellationToken);
    }
}

public sealed class UpdateFunctionAppRequestValidator : AbstractValidator<UpdateFunctionAppRequest>
{
    public UpdateFunctionAppRequestValidator()
    {
        RuleFor(request => request.Id).NotEmpty();
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
