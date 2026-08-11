using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Validation;
using Pole.Workloads;

namespace Pole.Features.DeploymentTargets.Application.Endpoints;

/// <summary>
/// Tenant-visible placement discovery. This endpoint deliberately returns only display and
/// ingress-capability information; platform target administration remains under /platform.
/// </summary>
public sealed record GetAvailableDeploymentTargetsRequest(
    [FromQuery] string ResourceType,
    [FromQuery] bool RequiresPublicIngress,
    [FromQuery] string? IsolationScopeId,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<IReadOnlyCollection<AvailableDeploymentTargetDto>>,
        ITenantReadOperation;

public sealed class GetAvailableDeploymentTargetsHandler(
    IDeploymentTargetLookup deploymentTargetLookup)
    : IMediatorHandler<
        GetAvailableDeploymentTargetsRequest,
        IReadOnlyCollection<AvailableDeploymentTargetDto>>
{
    public async Task<Result<IReadOnlyCollection<AvailableDeploymentTargetDto>>> HandleAsync(
        GetAvailableDeploymentTargetsRequest request,
        CancellationToken cancellationToken = default)
    {
        var targets = await deploymentTargetLookup.ListEligibleForNewWorkloadAsync(
            new DeploymentTargetAdmissionRequest(
                request.TenantId,
                ResourceTypeId.Create(request.ResourceType),
                request.IsolationScopeId,
                ExplicitDeploymentTargetId: null,
                request.RequiresPublicIngress
                    ? DeploymentTargetRequiredCapabilities.PublicIngress
                    : DeploymentTargetRequiredCapabilities.None),
            cancellationToken);
        if (!targets.TryGetValue(out var eligible, out var failure))
            return failure;

        return eligible
            .Select(target => new AvailableDeploymentTargetDto(
                target.Id,
                target.DisplayName,
                target.WorkloadConfiguration.Revision,
                target.WorkloadConfiguration.Ingress?.PublicBaseDomain is not null,
                target.WorkloadConfiguration.Ingress?.PublicBaseDomain))
            .ToArray();
    }
}

public sealed class GetAvailableDeploymentTargetsRequestValidator
    : AbstractValidator<GetAvailableDeploymentTargetsRequest>
{
    public GetAvailableDeploymentTargetsRequestValidator()
    {
        RuleFor(request => request.ResourceType)
            .Must(value =>
            {
                try
                {
                    ResourceTypeId.Create(value);
                    return true;
                }
                catch (ArgumentException)
                {
                    return false;
                }
            })
            .WithMessage("Resource type is invalid.");
        RuleFor(request => request.TenantId)
            .MongoObjectId(TenantHeaders.TenantId);
        When(
            request => request.IsolationScopeId is not null,
            () => RuleFor(request => request.IsolationScopeId!)
                .MongoObjectId("Isolation scope ID"));
    }
}

public static class GetAvailableDeploymentTargetsEndpoint
{
    public static IEndpointRouteBuilder MapGetAvailableDeploymentTargetsEndpoint(
        this IEndpointRouteBuilder app)
    {
        app.MapGet("/available", async (
                IMediator mediator,
                [AsParameters] GetAvailableDeploymentTargetsRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetAvailableDeploymentTargets")
            .Produces<IReadOnlyCollection<AvailableDeploymentTargetDto>>();
        return app;
    }
}
