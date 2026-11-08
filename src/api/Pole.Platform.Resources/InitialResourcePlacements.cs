using System.Collections.Immutable;
using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;

namespace Pole.Platform.Resources;

public sealed record InitialResourcePlacementRequest(
    string DisplayName,
    ImmutableArray<CreateResourceDeploymentRequest> Deployments);

public union InitialResourcePlacementOutcome(
    CreatedResourceDeployments,
    ResourceRegistrationMutationError,
    CreateResourceDeploymentError);

/// <summary>Creates the group entry and initial placements in the caller's transaction.
/// The caller must abort the transaction on either failure outcome.</summary>
public sealed class InitialResourcePlacements(
    IResourceRegistrar registrar,
    CreateResourceDeploymentSet createDeployments)
{
    public async Task<InitialResourcePlacementOutcome> CreateAsync(
        InitialResourcePlacementRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DisplayName);
        if (request.Deployments.IsDefaultOrEmpty || request.Deployments.Any(item => item is null))
            throw new ArgumentException("Initial placements require at least one deployment and no null entries.", nameof(request));

        var resource = request.Deployments[0];
        if (request.Deployments.Any(item =>
                item.TenantId != resource.TenantId || item.ResourceGroupId != resource.ResourceGroupId ||
                item.ResourceType != resource.ResourceType || item.ResourceId != resource.ResourceId))
            throw new ArgumentException("Initial placements must belong to one tenant, resource group and resource.", nameof(request));

        var registration = await registrar.RegisterAsync(
            new RegisterResourceRequest(
                resource.TenantId,
                resource.ResourceGroupId,
                new ResourceRegistrationDescriptor(resource.ResourceType, resource.ResourceId, request.DisplayName)),
            session,
            ct);
        return registration switch
        {
            ResourceRegistrationMutationError error => error,
            RegisteredResource => (await createDeployments(new(request.Deployments), session, ct)) switch
            {
                CreatedResourceDeployments created => created,
                CreateResourceDeploymentError error => error
            }
        };
    }
}
