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
using Pole.Features.Functions.Application.Persistence.Mutations;
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
                return result.ToHttpResult(outcome => outcome switch
                {
                    UpdateFunctionAppResponse response => Results.Accepted(value: response),
                    UpdateFunctionAppError error => MapUpdateError(error)
                });
            })
            .WithName("UpdateFunctionApp")
            .Produces<UpdateFunctionAppResponse>(StatusCodes.Status202Accepted);
        return app;
    }

    private static HttpProblem MapUpdateError(UpdateFunctionAppError error) =>
        error switch
        {
            FunctionAppNotFound notFound => MapNotFound(notFound),
            FunctionAppResourceDeploymentMissing missing => MapResourceDeploymentMissing(missing),
            FunctionAppHasPendingRevision pending => MapPendingRevision(pending),
            FunctionAppIsNotEnabled disabled => MapNotEnabled(disabled),
            FunctionAppRevisionChanged changed => MapRevisionChanged(changed),
            ResourceDeploymentMutationError deployment =>
                ResourceDeploymentContractHttpMappings.MapMutationError(deployment),
            QuotaExecutionError quota => QuotaContractHttpMappings.MapExecutionError(quota)
        };
}

public sealed record UpdateFunctionAppRequest(
    [FromRoute] string Id,
    [FromBody] UpdateFunctionAppRequestBody Body,
    [FromTenantHeader] string TenantId)
    : IMediatorRequest<UpdateFunctionAppOutcome>, ITenantWriteOperation;

public sealed record UpdateFunctionAppRequestBody(
    FunctionRevisionRequest Spec,
    ImmutableHashSet<string> Tags);

public sealed record UpdateFunctionAppResponse(
    string FunctionAppId,
    string RevisionId,
    int RevisionNumber,
    PlacementSummaryDto Placement);

public union UpdateFunctionAppError(
    FunctionAppNotFound,
    FunctionAppResourceDeploymentMissing,
    FunctionAppHasPendingRevision,
    FunctionAppIsNotEnabled,
    FunctionAppRevisionChanged,
    ResourceDeploymentMutationError,
    QuotaExecutionError);

public union UpdateFunctionAppOutcome(
    UpdateFunctionAppResponse,
    UpdateFunctionAppError);

public sealed class UpdateFunctionAppHandler(
    LoadFunctionAppById loadFunctionAppById,
    LoadFunctionAppRevisionReferences loadFunctionAppRevisionReferences,
    PersistFunctionAppRevision persistFunctionAppRevision,
    LoadExactResourceDeploymentRuntimePlacement loadDeploymentPlacement,
    RequestExactResourceDeploymentApply requestDeploymentApply,
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
            return new UpdateFunctionAppError(
                new FunctionAppResourceDeploymentMissing(app.Id));
        }

        var references = await loadFunctionAppRevisionReferences(app, cancellationToken);
        if (app.ActiveRevisionId is not null && references.ActiveRevision is null)
        {
            throw new InvalidOperationException(
                "Function App references an active revision that does not exist.");
        }

        var requestedSpec = request.Body.Spec.ToDomain().ToRevisionSpec();
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
                requestDeploymentApply,
                deployment,
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
        RequestExactResourceDeploymentApply requestDeploymentApply,
        ResourceDeploymentRuntimePlacementDto deployment,
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
        if (persisted is FunctionAppRevisionChanged persistenceError)
            return transaction.Abort(new UpdateFunctionAppError(persistenceError));

        var revision = (FunctionRevision)persisted.Value;

        var deploymentResult = await requestDeploymentApply(
            new ResourceDeploymentExactMutationRequest(
                current.TenantId,
                FunctionResourceDeployment.ResourceType,
                current.Id,
                current.Placement.ResourceDeploymentId,
                deployment.DeploymentRevision,
                new ComputeProfileReference(
                    deployment.Compute.ComputeProfileId,
                    deployment.Compute.ComputeProfileRevision),
                deployment.Compute with
                {
                    Requests = new ResourceDeploymentComputeResourceValues(
                        candidate.Spec.Resources.Requests.CpuMillis,
                        candidate.Spec.Resources.Requests.MemoryMi),
                    Limits = new ResourceDeploymentComputeResourceValues(
                        candidate.Spec.Resources.Limits.CpuMillis,
                        candidate.Spec.Resources.Limits.MemoryMi)
                },
                ResourceRevision: candidate.RevisionNumber),
            transaction.Session,
            cancellationToken);
        if (deploymentResult is ResourceDeploymentMutationError deploymentError)
            return transaction.Abort(new UpdateFunctionAppError(deploymentError));
        var placement = (PlacementSummaryDto)deploymentResult.Value;

        return transaction.Commit(new UpdateFunctionAppResponse(
            current.Id,
            revision.Id,
            revision.RevisionNumber,
            placement));
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
