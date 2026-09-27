using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Dns;
using Pole.Features.Domains.Application.Common;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;

namespace Pole.Features.Domains.Application.Persistence.HostnameBindings;

internal sealed class MongoCreateCustomHostnameBinding(
    IMongoDatabase database,
    MongoHostnameBindingCleanup cleanup,
    TimeProvider timeProvider)
{
    private readonly IMongoCollection<TenantDomainZone> zones =
        database.GetCollection<TenantDomainZone>(DomainCollectionNames.TenantDomainZones);
    private readonly IMongoCollection<HostnameBinding> bindings =
        database.GetCollection<HostnameBinding>(DomainCollectionNames.HostnameBindings);

    public async Task<CreateCustomHostnameBindingOutcome> InvokeAsync(
        CreateCustomHostnameBindingRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        EnsureRequest(request);
        DomainTransactions.EnsureActive(session);
        var zone = await zones.Find(
                session,
                DomainFilters.ZoneById(request.TenantId, request.TenantDomainZoneId))
            .FirstOrDefaultAsync(cancellationToken);
        if (zone is null)
        {
            return new CustomHostnameBindingError(
                    new CustomHostnameBindingZoneNotFound(request.TenantDomainZoneId));
        }

        if (!string.Equals(
                zone.DeploymentTargetId,
                request.DeploymentTargetId,
                StringComparison.Ordinal))
        {
            return new CustomHostnameBindingError(
                    new CustomHostnameBindingTargetMismatch(
                        zone.Id,
                        request.DeploymentTargetId));
        }

        var binding = HostnameBindingFactory.CreateCustom(
            request.TenantId,
            zone.Id,
            request.DeploymentTargetId,
            request.ResourceType,
            request.ResourceId,
            request.ResourceDeploymentId,
            request.RelativeName,
            DnsName.Create(zone.Domain),
            new HostnameBindingFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                timeProvider.GetUtcNow().UtcDateTime));

        if (!await cleanup.TryAdvanceZoneRevisionsAsync([zone], session, cancellationToken))
        {
            return new CustomHostnameBindingError(
                    new CustomHostnameBindingZoneChanged([zone.Id]));
        }

        try
        {
            await bindings.InsertOneAsync(session, binding, cancellationToken: cancellationToken);
            return binding.ToSnapshot();
        }
        catch (MongoWriteException exception) when (
            exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return new CustomHostnameBindingError(
                    new CustomHostnameBindingConflict(binding.Hostname));
        }
    }

    private static void EnsureRequest(CreateCustomHostnameBindingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ObjectId.TryParse(request.TenantId, out _) ||
            !ObjectId.TryParse(request.TenantDomainZoneId, out _) ||
            !ObjectId.TryParse(request.DeploymentTargetId, out _) ||
            !ObjectId.TryParse(request.ResourceId, out _) ||
            !ObjectId.TryParse(request.ResourceDeploymentId, out _) ||
            string.IsNullOrWhiteSpace(request.ResourceType.Value) ||
            string.IsNullOrWhiteSpace(request.RelativeName))
        {
            throw new ArgumentException(
                "A custom hostname binding request must contain valid resource identifiers and a relative name.",
                nameof(request));
        }
    }
}
