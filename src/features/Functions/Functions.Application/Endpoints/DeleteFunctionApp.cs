using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Persistence.Lifecycle;
using Pole.Features.Functions.Application.Persistence.Reads;
using Pole.Features.Functions.Domain;
using Pole.Features.Quotas.Contracts;
using Pole.Features.Quotas.Contracts.HttpMapping;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Contracts.HttpMapping;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Validation;
using static Pole.Features.Functions.Application.Common.FunctionAppHttpMappings;

namespace Pole.Features.Functions.Application.Endpoints;

public static class DeleteFunctionApp
{
    public static IEndpointRouteBuilder MapDeleteFunctionAppEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapDelete("/{Id}", async (
                IMediator mediator,
                [AsParameters] DeleteFunctionAppRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    DeleteFunctionAppResponse response => Results.Accepted(value: response),
                    DeleteFunctionAppError error => MapDeleteError(error)
                });
            })
            .WithName("DeleteFunctionApp")
            .Produces<DeleteFunctionAppResponse>(StatusCodes.Status202Accepted);
        return app;
    }

    private static HttpProblem MapDeleteError(DeleteFunctionAppError error) =>
        error switch
        {
            FunctionAppNotFound notFound => MapNotFound(notFound),
            FunctionAppResourceDeploymentMissing missing => MapResourceDeploymentMissing(missing),
            FunctionAppDeletionChanged changed => MapDeletionChanged(changed),
            ResourceDeploymentMutationError deployment =>
                ResourceDeploymentContractHttpMappings.MapMutationError(deployment),
            QuotaExecutionError quota => QuotaContractHttpMappings.MapExecutionError(quota)
        };
}

public sealed record DeleteFunctionAppRequest(
    [FromRoute] string Id,
    [FromTenantHeader] string TenantId)
    : IMediatorRequest<DeleteFunctionAppOutcome>, ITenantWriteOperation;

public sealed record DeleteFunctionAppResponse(string FunctionAppId);

public union DeleteFunctionAppError(
    FunctionAppNotFound,
    FunctionAppResourceDeploymentMissing,
    FunctionAppDeletionChanged,
    ResourceDeploymentMutationError,
    QuotaExecutionError);

public union DeleteFunctionAppOutcome(
    DeleteFunctionAppResponse,
    DeleteFunctionAppError);

public sealed class DeleteFunctionAppHandler(
    LoadFunctionAppById loadFunctionAppById,
    BeginFunctionAppDeletion beginFunctionAppDeletion,
    LoadExactResourceDeploymentRuntimePlacement loadDeploymentPlacement,
    RequestExactResourceDeploymentDeletion requestDeploymentDeletion,
    IQuotaAdmissionExecutor quotaAdmissionExecutor,
    TimeProvider timeProvider)
    : IMediatorOutcomeHandler<DeleteFunctionAppRequest, DeleteFunctionAppOutcome>
{
    public async Task<DeleteFunctionAppOutcome> HandleAsync(
        DeleteFunctionAppRequest request,
        CancellationToken cancellationToken = default)
    {
        var app = await loadFunctionAppById(
            request.TenantId,
            request.Id,
            cancellationToken);
        if (app is null)
            return new DeleteFunctionAppError(new FunctionAppNotFound(request.Id));
        if (app.DesiredState == FunctionAppDesiredState.Deleting)
            return new DeleteFunctionAppResponse(app.Id);

        var deployment = await loadDeploymentPlacement(
            app.TenantId,
            FunctionResourceDeployment.ResourceType,
            app.Id,
            app.Placement.ResourceDeploymentId,
            cancellationToken);
        if (deployment is null ||
            !string.Equals(
                deployment.RuntimeCellId,
                app.Placement.RuntimeCellId,
                StringComparison.Ordinal) ||
            !string.Equals(
                deployment.DeploymentTargetId,
                app.Placement.DeploymentTargetId,
                StringComparison.Ordinal))
        {
            return new DeleteFunctionAppError(
                new FunctionAppResourceDeploymentMissing(app.Id));
        }

        return await quotaAdmissionExecutor.ExecuteOutcomeAsync<
            FunctionQuotaAllocation,
            DeleteFunctionAppOutcome>(
            app.TenantId,
            FunctionAppQuotaResourceKey.Create(app.Id),
            new DeleteQuotaMutation<FunctionQuotaAllocation>(app.AdmittedQuota),
            quotaError => new DeleteFunctionAppError(quotaError),
            async (transaction, ct) =>
            {
                var deleted = await beginFunctionAppDeletion(
                    transaction.Session,
                    app,
                    timeProvider.GetUtcNow(),
                    ct);
                if (deleted is FunctionAppDeletionChanged deletionError)
                    return transaction.Abort(new DeleteFunctionAppError(deletionError));

                var deploymentResult = await requestDeploymentDeletion(
                    new ResourceDeploymentExactMutationRequest(
                        app.TenantId,
                        FunctionResourceDeployment.ResourceType,
                        app.Id,
                        app.Placement.ResourceDeploymentId,
                        deployment.DeploymentRevision,
                        new ComputeProfileReference(
                            deployment.Compute.ComputeProfileId,
                            deployment.Compute.ComputeProfileRevision),
                        ResourceRevision: app.NextRevisionNumber),
                    transaction.Session,
                    ct);
                if (deploymentResult is ResourceDeploymentMutationError deploymentError)
                {
                    return transaction.Abort(
                        new DeleteFunctionAppError(deploymentError));
                }

                return transaction.Commit(new DeleteFunctionAppResponse(app.Id));
            },
            cancellationToken);
    }
}

public sealed class DeleteFunctionAppRequestValidator
    : AbstractValidator<DeleteFunctionAppRequest>
{
    public DeleteFunctionAppRequestValidator() =>
        RuleFor(request => request.Id).MongoObjectId("Function App ID");
}
