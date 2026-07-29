using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Shared.Models;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Features.StorageProfiles.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.StorageProfiles.Application.Endpoints;

public sealed record GetAvailableStorageProfilesRequest(
    [FromQuery] string ResourceType,
    [FromQuery] string DeploymentTargetId,
    [FromHeader(Name = TenantHeaders.TenantId)] string? TenantId = null)
    : IMediatorRequest<IReadOnlyCollection<StorageProfileDto>>, ITenantReadOperation;

public sealed class GetAvailableStorageProfilesHandler(
    IStorageProfileDbContext dbContext,
    IStorageDiscoveryService storageDiscovery)
    : IMediatorHandler<GetAvailableStorageProfilesRequest, IReadOnlyCollection<StorageProfileDto>>
{
    public async Task<Result<IReadOnlyCollection<StorageProfileDto>>> HandleAsync(
        GetAvailableStorageProfilesRequest request,
        CancellationToken cancellationToken = default)
    {
        var deploymentTargetId = request.DeploymentTargetId.Trim();
        var resourceType = Enum.Parse<ResourceType>(
            request.ResourceType,
            ignoreCase: true);
        var storageClassesResult = await storageDiscovery.GetStorageClassNamesAsync(
            deploymentTargetId,
            cancellationToken);
        if (!storageClassesResult.TryGetValue(
                out var knownStorageClassNames,
                out var failure))
        {
            return failure;
        }

        var profiles = await dbContext.StorageProfiles
            .Find(Builders<StorageProfile>.Filter.And(
                Builders<StorageProfile>.Filter.Eq(
                    profile => profile.IsEnabled,
                    true),
                Builders<StorageProfile>.Filter.ElemMatch(
                    profile => profile.TargetMappings,
                    mapping => mapping.DeploymentTargetId == deploymentTargetId)))
            .SortByDescending(profile => profile.IsDefault)
            .ThenBy(profile => profile.DisplayName)
            .ToListAsync(cancellationToken);

        return profiles
            .Where(profile => StorageProfileResolver.AllowsTenant(
                profile,
                request.TenantId!))
            .Where(profile => StorageProfileResolver.AllowsResourceType(
                profile,
                resourceType))
            .Where(profile => profile.TargetMappings.Any(mapping =>
                string.Equals(
                    mapping.DeploymentTargetId,
                    deploymentTargetId,
                    StringComparison.Ordinal) &&
                knownStorageClassNames.Contains(mapping.StorageClassName)))
            .Select(profile => profile.ToDto())
            .ToArray();
    }
}

public sealed class GetAvailableStorageProfilesRequestValidator
    : AbstractValidator<GetAvailableStorageProfilesRequest>
{
    public GetAvailableStorageProfilesRequestValidator()
    {
        RuleFor(request => request.ResourceType)
            .Must(value =>
                Enum.TryParse<ResourceType>(
                    value,
                    ignoreCase: true,
                    out var parsed) &&
                Enum.IsDefined(parsed))
            .WithMessage("Resource type is invalid.");
        RuleFor(request => request.DeploymentTargetId)
            .Must(id => ObjectId.TryParse(id?.Trim(), out _))
            .WithMessage("Deployment target ID must be a MongoDB ObjectId.");
        RuleFor(request => request.TenantId)
            .Must(id => ObjectId.TryParse(id?.Trim(), out _))
            .WithMessage($"{TenantHeaders.TenantId} must be a MongoDB ObjectId.");
    }
}

public static class GetAvailableStorageProfilesEndpoint
{
    public static IEndpointRouteBuilder MapGetAvailableStorageProfilesEndpoint(
        this IEndpointRouteBuilder app)
    {
        app.MapGet("/", async (
                [AsParameters] GetAvailableStorageProfilesRequest request,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("GetAvailableStorageProfiles")
            .Produces<IReadOnlyCollection<StorageProfileDto>>();
        return app;
    }
}
