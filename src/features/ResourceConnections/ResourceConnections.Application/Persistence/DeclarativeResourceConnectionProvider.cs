using System.Text.Json;
using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.ResourceConnections.Domain;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Workloads;

namespace Pole.Features.ResourceConnections.Application.Persistence;

public sealed record ResourceConnectionDeclaration(
    ResourceAddress Source,
    ResourceAddress Destination,
    string Endpoint);

public sealed record ConnectionDeclarationUnavailable : IDeclarationFailure;

public sealed record ConnectionDeclarationEndpointUnavailable : IDeclarationFailure;

public sealed record ConnectionDeclarationTopologyUnavailable : IDeclarationFailure;

internal sealed partial class DeclarativeResourceConnectionProvider(
    IMongoCollection<ResourceConnection> connections,
    IEnumerable<ResourceConnectionDeclarationKind> registeredKinds,
    ResourceConnectionEndpointCatalog endpoints,
    ResourceConnectionTopologyAdmission admission,
    MongoCreateResourceConnection creation,
    LoadResourceRegistrationPlanningSnapshot loadRegistration,
    FenceResourceRegistrationPlanningSnapshot fenceRegistration,
    IGitOpsMutations git) : ITransactionalDeclarativeResourceProvider
{
    private readonly IReadOnlyDictionary<string, ResourceTypeId> kinds = registeredKinds
        .ToDictionary(item => item.Kind, item => item.ResourceType, StringComparer.Ordinal);

    public string Kind => "ResourceConnection";

    public int MaximumNameLength => 30;

    public string DescribeFailure(IDeclarationFailure failure) => failure switch
    {
        ConnectionDeclarationUnavailable => nameof(ConnectionDeclarationUnavailable),
        ConnectionDeclarationEndpointUnavailable => nameof(ConnectionDeclarationEndpointUnavailable),
        ConnectionDeclarationTopologyUnavailable => nameof(ConnectionDeclarationTopologyUnavailable),
        _ => throw new ArgumentException("The failure belongs to another declaration provider.", nameof(failure))
    };

    public JsonElement NormalizeAndValidate(JsonElement spec)
    {
        var declaration = DeclarationJson.Read<ResourceConnectionDeclaration>(spec);
        DeclarationJson.Require(declaration.Source is not null && DeclarationNames.Valid(declaration.Source.Name) &&
            kinds.TryGetValue(declaration.Source.Kind, out var source) &&
            endpoints.SupportsSource(source), "spec.source");
        DeclarationJson.Require(declaration.Destination is not null &&
            DeclarationNames.Valid(declaration.Destination.Name) &&
            kinds.TryGetValue(declaration.Destination.Kind, out var destination) &&
            endpoints.SupportsDestination(destination), "spec.destination");
        DeclarationJson.Require(!string.IsNullOrWhiteSpace(declaration.Endpoint) &&
            declaration.Endpoint.Length <= 100 && !declaration.Endpoint.Any(char.IsControl), "spec.endpoint");
        return DeclarationJson.Write(declaration);
    }

    public DeclarationFacts Facts(JsonElement spec)
    {
        var declaration = DeclarationJson.Read<ResourceConnectionDeclaration>(spec);
        return new(null, [], [new(declaration.Source, "spec.source"), new(declaration.Destination, "spec.destination")],
            [], [], ReferencesShareScope: false);
    }

    public IReadOnlyList<DeclarationStep> Steps(ResourceAddress address, JsonElement spec)
    {
        var declaration = DeclarationJson.Read<ResourceConnectionDeclaration>(spec);
        return [new(DeclarationPhase.Bind,
            [new(declaration.Source, DeclarationPhase.Prepare),
                new(declaration.Destination, DeclarationPhase.Configure)])];
    }

    public async Task<string?> ResolveAsync(
        ResolveDeclaredResourceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken) =>
        (await connections.Find(session, ResourceConnectionFilters.ByName(request.TenantId, request.Name))
            .FirstOrDefaultAsync(cancellationToken))?.Id;

    public Task<GitOperationResult> ApplyAsync(GitOperation operation, CancellationToken cancellationToken) =>
        git.ExecuteAsync(operation, async (session, ct) =>
        {
            var resolved = await ResolveDeclarationAsync(operation.Context.TenantId, operation.Declaration,
                operation.Input, session, ct);
            if (resolved is null)
                return new(operation.ResourceId, new ConnectionDeclarationUnavailable());

            var result = await creation.CreateAsync(new(operation.ResourceId, resolved.Command), session, ct);
            return result switch
            {
                ResourceConnection => new(operation.ResourceId, Changed: true),
                ConnectionNameAlreadyExists => new(operation.ResourceId, new DeclarationPreconditionsChanged()),
                ConnectionResourceMissing => new(operation.ResourceId, new ConnectionDeclarationUnavailable()),
                ConnectionPlacementDeleting => new(operation.ResourceId, new ConnectionDeclarationUnavailable()),
                ConnectionEndpointUnsupported =>
                    new(operation.ResourceId, new ConnectionDeclarationEndpointUnavailable()),
                ConnectionSourceHasNoPlacements =>
                    new(operation.ResourceId, new ConnectionDeclarationTopologyUnavailable()),
                ConnectionDestinationTargetMissing =>
                    new(operation.ResourceId, new ConnectionDeclarationTopologyUnavailable()),
                ConnectionDestinationTargetAmbiguous =>
                    new(operation.ResourceId, new ConnectionDeclarationTopologyUnavailable())
            };
        }, cancellationToken);

    private static DeclarationProjection Project(CreateResourceConnectionCommand command) => new(
        DeclarationJson.Write(new
        {
            sourceType = command.SourceResourceType.Value,
            sourceId = command.SourceResourceId,
            destinationType = command.DestinationResourceType.Value,
            destinationId = command.DestinationResourceId,
            endpoint = command.EndpointId
        }), DeclarationJson.Write(new { }));

    private async Task<ResolvedDeclaration?> ResolveDeclarationAsync(
        string tenantId,
        CompiledDeclaration declaration,
        GitOperationInput? operation,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var spec = DeclarationJson.Read<ResourceConnectionDeclaration>(declaration.Spec);
        var sourceId = await ResolveReferenceAsync(spec.Source);
        var destinationId = await ResolveReferenceAsync(spec.Destination);
        if (sourceId is null || destinationId is null)
            return null;

        var source = await loadRegistration(new(tenantId, kinds[spec.Source.Kind], sourceId),
            session, cancellationToken);
        var destination = await loadRegistration(new(tenantId, kinds[spec.Destination.Kind], destinationId),
            session, cancellationToken);
        return source is null || destination is null ? null : new(new(tenantId, declaration.Address.Name,
            kinds[spec.Source.Kind], sourceId, kinds[spec.Destination.Kind], destinationId, spec.Endpoint),
            new[] { source, destination }.DistinctBy(item => item.Resource.RegistrationId)
                .OrderBy(item => item.Resource.RegistrationId, StringComparer.Ordinal).ToArray());

        Task<string?> ResolveReferenceAsync(ResourceAddress address) => operation is null
            ? git.ResolveAsync(new(tenantId, address), session, cancellationToken)
            : git.ResolveAcceptedAsync(new(operation.Context, operation.ResourceId, address),
                session, cancellationToken);
    }

    private sealed record ResolvedDeclaration(
        CreateResourceConnectionCommand Command,
        IReadOnlyList<ResourceRegistrationPlanningSnapshot> Registrations)
    {
        public DeclarationDocumentVersion[] Versions => Registrations.Select(item =>
            new DeclarationDocumentVersion($"ResourceRegistration/{item.Resource.RegistrationId}", item.ReferenceFence))
            .ToArray();
    }
}
