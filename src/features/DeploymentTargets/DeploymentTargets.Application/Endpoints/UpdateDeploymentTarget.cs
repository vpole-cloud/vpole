using FluentValidation;
using Pole.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Connections;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Endpoints;

public sealed record UpdateDeploymentTargetRequestBody(
    string DisplayName,
    bool Enabled,
    bool AcceptsWorkloads,
    DeploymentTargetWorkloadConfigurationRequestBody WorkloadConfiguration,
    long Revision);

public sealed record UpdateDeploymentTargetRequest(
    [FromRoute] string Id,
    [FromBody] UpdateDeploymentTargetRequestBody Body)
    : IMediatorRequest<DeploymentTargetDto>;

public sealed class UpdateDeploymentTargetHandler(
    IDeploymentTargetDbContext dbContext,
    DeploymentTargetReader reader,
    DeploymentTargetDomainAdmission domainAdmission,
    IDeploymentTargetClientCache clientCache,
    TimeProvider timeProvider)
    : IMediatorHandler<UpdateDeploymentTargetRequest, DeploymentTargetDto>
{
    public async Task<Result<DeploymentTargetDto>> HandleAsync(
        UpdateDeploymentTargetRequest request,
        CancellationToken cancellationToken = default)
    {
        var targetResult = await reader.GetAsync(request.Id, cancellationToken);
        if (!targetResult.TryGetValue(out var target, out var targetFailure))
            return targetFailure;
        if (target.Revision != request.Body.Revision)
            return Failures.Conflict("The deployment target changed. Reload it and retry.");
        if (request.Body.AcceptsWorkloads && target.DeletionRequestedAtUtc is not null)
            return Failures.Conflict("A deployment target pending deletion cannot accept workloads.");

        var updated = target.Reconfigure(
            request.Body.DisplayName,
            request.Body.Enabled,
            request.Body.AcceptsWorkloads,
            request.Body.WorkloadConfiguration.Ingress?.ToDomain(),
            DeploymentTargetClock.UtcNowAfter(timeProvider, target.UpdatedAtUtc));

        if (updated.Revision == target.Revision)
        {
            var existingDefaultId = await reader.GetDefaultIdAsync(cancellationToken);
            return target.ToDto(target.Id == existingDefaultId);
        }

        var publicBaseDomainChanged = !string.Equals(
            target.WorkloadConfiguration.Ingress?.PublicBaseDomain,
            updated.WorkloadConfiguration.Ingress?.PublicBaseDomain,
            StringComparison.Ordinal);
        var persistedResult = publicBaseDomainChanged
            ? await ReplacePublicBaseDomainAsync(target, updated, cancellationToken)
            : await ReplaceAsync(target, updated, cancellationToken);
        if (!persistedResult.TryGetValue(out var persisted, out var persistenceFailure))
            return persistenceFailure;

        if (!persisted.Enabled)
            clientCache.Invalidate(target.Id);
        var defaultId = await reader.GetDefaultIdAsync(cancellationToken);
        return persisted.ToDto(persisted.Id == defaultId);
    }

    private async Task<Result<DeploymentTarget>> ReplaceAsync(
        DeploymentTarget target,
        DeploymentTarget updated,
        CancellationToken cancellationToken)
    {
        var persisted = await dbContext.DeploymentTargets.FindOneAndUpdateAsync(
            DeploymentTargetFilters.AtRevision(target),
            ReplacementUpdate(updated),
            new FindOneAndUpdateOptions<DeploymentTarget>
            {
                ReturnDocument = ReturnDocument.After
            },
            cancellationToken);
        return persisted is null
            ? Failures.Conflict("The deployment target changed. Reload it and retry.")
            : persisted;
    }

    private Task<Result<DeploymentTarget>> ReplacePublicBaseDomainAsync(
        DeploymentTarget target,
        DeploymentTarget updated,
        CancellationToken cancellationToken) =>
        dbContext.RunInTransactionAsync<DeploymentTarget>(async (session, ct) =>
        {
            var references = await domainAdmission.EnsureNoPublicDomainReferencesAsync(
                target.Id,
                session,
                ct);
            if (references is not ISuccess)
                return (Failure)references;

            var overlap = await domainAdmission.EnsureNoOverlappingPublicBaseDomainAsync(
                target.Id,
                updated.WorkloadConfiguration.Ingress?.PublicBaseDomain,
                session,
                ct);
            if (overlap is not ISuccess)
                return (Failure)overlap;

            var persisted = await dbContext.DeploymentTargets.FindOneAndUpdateAsync(
                session,
                DeploymentTargetFilters.AtRevisionAndReferenceFence(target),
                ReplacementUpdate(updated),
                new FindOneAndUpdateOptions<DeploymentTarget>
                {
                    ReturnDocument = ReturnDocument.After
                },
                ct);
            return persisted is null
                ? Failures.Conflict("The deployment target changed. Reload it and retry.")
                : persisted;
        }, cancellationToken: cancellationToken);

    private static UpdateDefinition<DeploymentTarget> ReplacementUpdate(DeploymentTarget updated) =>
        Builders<DeploymentTarget>.Update.Combine(
            Builders<DeploymentTarget>.Update.Set(item => item.DisplayName, updated.DisplayName),
            Builders<DeploymentTarget>.Update.Set(item => item.Enabled, updated.Enabled),
            Builders<DeploymentTarget>.Update.Set(
                item => item.AcceptsWorkloads,
                updated.AcceptsWorkloads),
            Builders<DeploymentTarget>.Update.Set(
                item => item.WorkloadConfiguration,
                updated.WorkloadConfiguration),
            Builders<DeploymentTarget>.Update.Set(item => item.Revision, updated.Revision),
            Builders<DeploymentTarget>.Update.Set(item => item.UpdatedAtUtc, updated.UpdatedAtUtc));
}

public sealed class UpdateDeploymentTargetRequestValidator
    : AbstractValidator<UpdateDeploymentTargetRequest>
{
    public UpdateDeploymentTargetRequestValidator()
    {
        RuleFor(request => request.Id)
            .MongoObjectId("Deployment target ID");
        RuleFor(request => request.Body)
            .NotNull()
            .SetValidator(new UpdateDeploymentTargetRequestBodyValidator());
    }
}

public sealed class UpdateDeploymentTargetRequestBodyValidator
    : AbstractValidator<UpdateDeploymentTargetRequestBody>
{
    public UpdateDeploymentTargetRequestBodyValidator()
    {
        RuleFor(body => body.DisplayName)
            .NotEmpty()
            .MaximumLength(80);
        RuleFor(body => body.Revision).GreaterThan(0);
        RuleFor(body => body.AcceptsWorkloads)
            .Equal(false)
            .When(body => !body.Enabled)
            .WithMessage("A disabled deployment target cannot accept workloads.");
        RuleFor(body => body.WorkloadConfiguration)
            .NotNull()
            .SetValidator(new DeploymentTargetWorkloadConfigurationRequestBodyValidator());
    }
}

public static class UpdateDeploymentTargetEndpoint
{
    public static IEndpointRouteBuilder MapUpdateDeploymentTargetEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPut("/{Id}", async (
                IMediator mediator,
                [AsParameters] UpdateDeploymentTargetRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("UpdateDeploymentTarget")
            .Produces<DeploymentTargetDto>();
        return app;
    }
}
