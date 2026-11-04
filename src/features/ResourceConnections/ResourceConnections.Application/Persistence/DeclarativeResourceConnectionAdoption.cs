using System.Text.Json;
using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.ResourceConnections.Domain;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Workloads;

namespace Pole.Features.ResourceConnections.Application.Persistence;

internal sealed partial class DeclarativeResourceConnectionProvider
{
    public async Task<PreviewLifecycleAdoptionOutcome> PreviewAdoptionAsync(
        PreviewLifecycleAdoptionRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var current = await connections.Find(session,
                ResourceConnectionFilters.ByTenantAndId(request.TenantId, request.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null || current.Lifecycle is not ActiveResourceConnection)
            return new DeclarationReadBlocked(new ConnectionDeclarationUnavailable());

        if (request.Declaration.Address.Kind != Kind || current.Name != request.Declaration.Address.Name)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());

        var spec = DeclarationJson.Read<ResourceConnectionDeclaration>(request.Declaration.Spec);
        var sourceId = ReferenceId(spec.Source);
        var destinationId = ReferenceId(spec.Destination);
        if (sourceId is null || destinationId is null || !kinds.TryGetValue(spec.Source.Kind, out var sourceType) ||
            !kinds.TryGetValue(spec.Destination.Kind, out var destinationType))
            return new DeclarationReadBlocked(new DeclarationReferenceUnavailable(
                sourceId is null ? spec.Source : spec.Destination));

        var command = new CreateResourceConnectionCommand(request.TenantId, request.Declaration.Address.Name,
            sourceType, sourceId, destinationType, destinationId, spec.Endpoint);
        var projection = Project(new(current.TenantId, current.Name,
            ResourceTypeId.Create(current.SourceResourceTypeId), current.SourceResourceId,
            ResourceTypeId.Create(current.DestinationResourceTypeId), current.DestinationResourceId,
            current.EndpointId));
        var desiredProjection = Project(command);
        var currentDeclaration = new CurrentDeclaration(current.Id, LifecycleVersion(current), projection,
            DeclarationConfigurationState.Configured);
        var source = await loadRegistration(new(request.TenantId, sourceType, sourceId), session, cancellationToken);
        var destination = await loadRegistration(new(request.TenantId, destinationType, destinationId),
            session, cancellationToken);
        if (source is null || destination is null)
        {
            // The engine proves reserved IDs against the complete reviewed snapshot. A replacement
            // can project those identities before their prerequisite resources exist; actual endpoint
            // and placement admission remains mandatory in the normal Bind phase.
            return !JsonElement.DeepEquals(projection.Identity, desiredProjection.Identity)
                ? new GitLifecycleAdoptionPreview(currentDeclaration, desiredProjection, [])
                : new DeclarationReadBlocked(new DeclarationReferenceUnavailable(
                    source is null ? spec.Source : spec.Destination));
        }

        var resolved = new ResolvedDeclaration(command,
            new[] { source, destination }.DistinctBy(item => item.Resource.RegistrationId)
                .OrderBy(item => item.Resource.RegistrationId, StringComparer.Ordinal).ToArray());
        var desired = await ProjectResolvedAsync(resolved, session, cancellationToken);
        return desired switch
        {
            DeclarationProjection accepted => new GitLifecycleAdoptionPreview(currentDeclaration, accepted, []),
            DeclarationReadBlocked blocked => blocked
        };

        string? ReferenceId(ResourceAddress address)
        {
            var references = request.References.Where(item => item.Address == address).ToArray();
            return references.Length == 1 ? references[0].ResourceId : null;
        }
    }
}
