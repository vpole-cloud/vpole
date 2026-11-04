using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.GitOps.Contracts;
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
            FunctionSecretBindingRemovalRequired bindings => MapSecretBindingRemovalRequired(bindings),
            FunctionAppGitOwnedConfiguration owned => MapGitOwned(owned),
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
    FunctionSecretBindingRemovalRequired,
    FunctionAppNotFound,
    FunctionAppGitOwnedConfiguration,
    FunctionAppResourceDeploymentMissing,
    FunctionAppDeletionChanged,
    ResourceDeploymentMutationError,
    QuotaExecutionError);

public union DeleteFunctionAppOutcome(
    DeleteFunctionAppResponse,
    DeleteFunctionAppError);

public sealed class DeleteFunctionAppHandler(
    LoadFunctionAppById loadFunctionAppById,
    ApplyFunctionAppDeletion deleteWorkflow,
    LoadExactResourceDeploymentRuntimePlacement loadDeploymentPlacement,
    IQuotaAdmissionExecutor quotaAdmissionExecutor,
    IGitOpsMutations git)
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

        if (await git.StatusAsync(new(app.TenantId, "FunctionApp", app.Id), cancellationToken) is not null)
            return new DeleteFunctionAppError(new FunctionAppGitOwnedConfiguration(app.Id));

        var deployment = await loadDeploymentPlacement(
            new LoadExactResourceDeploymentRuntimePlacementRequest(
                TenantId: app.TenantId,
                ResourceType: FunctionResourceDeployment.ResourceType,
                ResourceId: app.Id,
                DeploymentId: app.Placement.ResourceDeploymentId),
            cancellationToken: cancellationToken);
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
            new ExecuteQuotaAdmissionRequest<FunctionQuotaAllocation, DeleteFunctionAppOutcome>(
                TenantId: app.TenantId,
                ResourceKey: FunctionAppQuotaResourceKey.Create(app.Id),
                Mutation: new DeleteQuotaMutation<FunctionQuotaAllocation>(app.AdmittedQuota),
                QuotaError: quotaError => new DeleteFunctionAppError(quotaError),
                MutationCallback: async (transaction, ct) =>
                {
                    var deleted = await deleteWorkflow(new(app,
                        new ResourceDeploymentExactMutationRequest(
                            app.TenantId,
                            FunctionResourceDeployment.ResourceType,
                            app.Id,
                            app.Placement.ResourceDeploymentId,
                            deployment.DeploymentRevision,
                            new ComputeProfileReference(
                                deployment.Compute.ComputeProfileId,
                                deployment.Compute.ComputeProfileRevision),
                            ResourceRevision: app.NextRevisionNumber)), transaction.Session, ct);
                    if (deleted is FunctionSecretBindingRemovalRequired bindings)
                        return transaction.Abort(new DeleteFunctionAppError(bindings));
                    if (deleted is FunctionAppDeletionChanged changed)
                        return transaction.Abort(new DeleteFunctionAppError(changed));
                    if (deleted is FunctionAppGitOwnedConfiguration owned)
                        return transaction.Abort(new DeleteFunctionAppError(owned));
                    if (deleted is ResourceDeploymentMutationError deploymentError)
                        return transaction.Abort(new DeleteFunctionAppError(deploymentError));

                    return transaction.Commit(new DeleteFunctionAppResponse(app.Id));
                }),
            cancellationToken: cancellationToken);
    }
}

public sealed class DeleteFunctionAppRequestValidator
    : AbstractValidator<DeleteFunctionAppRequest>
{
    public DeleteFunctionAppRequestValidator() =>
        RuleFor(request => request.Id).MongoObjectId("Function App ID");
}
