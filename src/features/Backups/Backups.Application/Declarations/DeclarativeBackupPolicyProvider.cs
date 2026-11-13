using System.Text.Json;
using MongoDB.Driver;
using Pole.Features.Backups.Application.Archives;
using Pole.Features.Backups.Application.Configuration;
using Pole.Features.Backups.Application.Mongo;
using Pole.Features.Backups.Application.Policies;
using Pole.Features.Backups.Contracts;
using Pole.Features.Backups.Domain;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Scheduling;
using Pole.Workloads;

namespace Pole.Features.Backups.Application.Declarations;

internal sealed record BackupPolicyDeclaration(
    ResourceAddress Service,
    string Destination,
    string Schedule,
    bool Suspended,
    int RetentionDays
);

internal sealed class DeclarativeBackupPolicyProvider(
    IMongoCollection<BackupPolicy> policies,
    IMongoCollection<BackupDestination> destinations,
    IGitOpsMutations git,
    BackupPolicyWrites writes,
    BeginBackupPolicyDeletion deletion,
    IEnumerable<IBackupProvider> services,
    LoadResourceRegistrationPlanningSnapshot loadRegistration,
    FenceResourceRegistrationPlanningSnapshot fenceRegistration,
    LoadResourceDeploymentPlanningSnapshot loadPlacements,
    FenceResourceDeploymentPlanningSnapshot fencePlacements
) : BackupDeclarationProvider<BackupPolicy, BackupPolicyInput>(policies, git)
{
    private readonly IReadOnlyDictionary<string, IBackupProvider> providers = services.ToDictionary(
        s => s.DeclarationKind,
        StringComparer.Ordinal);

    public override string Kind => BackupDeclarationKinds.Policy;
    protected override GitLifecycleProtection Protection => GitLifecycleProtection.ConfigurationOnly;

    protected override UpdateDefinition<BackupPolicy> PlanningFence =>
        Builders<BackupPolicy>.Update.Inc(p => p.PlanningFence, 1);

    protected override bool Deleting(BackupPolicy policy) => policy.Deleting;

    public override JsonElement NormalizeAndValidate(JsonElement spec)
    {
        var value = DeclarationJson.Read<BackupPolicyDeclaration>(spec);
        DeclarationJson.Require(
            value.Service is not null && DeclarationNames.Valid(value.Service.Name) && value.Service.Kind is not null &&
            providers.ContainsKey(value.Service.Kind),
            "spec.service");
        DeclarationJson.Require(DeclarationNames.Valid(value.Destination), "spec.destination");
        DeclarationJson.Require(
            value.Schedule is { Length: <= 120 } && UtcCronSchedule.TryParse(value.Schedule, out _),
            "spec.schedule");
        var cron = UtcCronSchedule.Parse(value.Schedule).Expression;
        DeclarationJson.Require(
            providers[value.Service.Kind].ValidateSchedule(new(cron, value.Suspended, value.RetentionDays)) is
                BackupScheduleAccepted,
            "spec.retentionDays");
        return DeclarationJson.Write(value with { Schedule = cron });
    }

    public override DeclarationFacts Facts(JsonElement spec)
    {
        var value = DeclarationJson.Read<BackupPolicyDeclaration>(spec);
        return new(
            null,
            [],
            [
                new(value.Service, "spec.service"),
                new(new(BackupDeclarationKinds.Destination, value.Destination), "spec.destination")
            ],
            [new("ServiceBackupPolicy", value.Service, value.Service)],
            []);
    }

    public override IReadOnlyList<DeclarationStep> Steps(ResourceAddress address, JsonElement spec) =>
    [
        new(
            DeclarationPhase.Configure,
            Facts(spec)
                .References
                .Select(r => new DeclarationDependency(r.Target, DeclarationPhase.Configure))
                .ToArray())
    ];

    protected override DeclarationProjection Project(BackupPolicy entity) => Project(
        new(
            entity.Name,
            entity.ResourceType,
            entity.ResourceId,
            entity.DestinationId,
            entity.Schedule,
            entity.Suspended,
            entity.RetentionDays));

    private static DeclarationProjection Project(BackupPolicyInput policy) => new(
        DeclarationJson.Write(new { policy.ResourceType, policy.ResourceId }),
        DeclarationJson.Write(new { policy.DestinationId, policy.Schedule, policy.Suspended, policy.RetentionDays }));

    protected override async Task<ResolvedBackupDeclaration<BackupPolicyInput>?> ResolveAsync(
        BackupDeclarationRead read,
        bool fence)
    {
        var spec = DeclarationJson.Read<BackupPolicyDeclaration>(read.Declaration.Spec);
        var resourceId = await read.ResolveAsync(spec.Service);
        var destinationId = await read.ResolveAsync(new(BackupDeclarationKinds.Destination, spec.Destination));
        if (resourceId is null || destinationId is null || !providers.TryGetValue(spec.Service.Kind, out var provider))
            return null;
        var type = ResourceTypeId.Create(provider.ResourceType);
        var destination = await destinations
            .Find(read.Session, BackupsFilters<BackupDestination>.ById(read.TenantId, destinationId))
            .SingleOrDefaultAsync(read.CancellationToken);
        var registration = await loadRegistration(
            new(read.TenantId, type, resourceId),
            read.Session,
            read.CancellationToken);
        if (destination is null || registration?.Resource.ResourceGroupId != destination.ResourceGroupId ||
            await loadPlacements(new(read.TenantId, type, resourceId), read.Session, read.CancellationToken) is not
                ResourceDeploymentPlanningSnapshot { Items.Count: 1 } placements)
            return null;
        var placement = placements.Items[0];
        if (placement.State != ResourceDeploymentPlanningState.Active ||
            placement.IsolationScopeId != destination.IsolationScopeId)
            return null;
        if (fence)
        {
            var write = await destinations.UpdateOneAsync(
                read.Session,
                BackupConfigurationFilters<BackupDestination>.AtRevision(
                    read.TenantId,
                    destinationId,
                    destination.Revision),
                Builders<BackupDestination>.Update.Inc(d => d.ReferenceFence, 1),
                cancellationToken: read.CancellationToken);
            if (write.ModifiedCount != 1 ||
                await fenceRegistration(registration, read.Session, read.CancellationToken) is not
                    ResourceRegistrationSnapshotFenced ||
                await fencePlacements(placements, read.Session, read.CancellationToken) is not
                    ResourceDeploymentPlanningFenced)
                return null;
        }

        var input = new BackupPolicyInput(
            read.Declaration.Address.Name,
            type.Value,
            resourceId,
            destinationId,
            spec.Schedule,
            spec.Suspended,
            spec.RetentionDays);
        return new(
            input,
            Project(input) with
            {
                Dependencies = new(
                [
                    new(
                        DeclarationVersionKeys.Resource(BackupDeclarationKinds.Destination, destinationId),
                        destination.Revision),
                    new(
                        ResourceGroupVersionKeys.RegistrationReferenceFence(registration.Resource.RegistrationId),
                        registration.ReferenceFence),
                    new(
                        ResourceDeploymentVersionKeys.DesiredGeneration(placement.DeploymentId),
                        placement.DesiredGeneration),
                    new(
                        ResourceDeploymentVersionKeys.ResourceRevision(placement.DeploymentId),
                        placement.ResourceRevision)
                ])
            });
    }

    protected override async Task<GitOperationResult> ApplyResolvedAsync(
        BackupConfigurationWrite<BackupPolicyInput> write,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        if (write.ExpectedRevision == 0)
        {
            var result = await writes.CreateAcceptedAsync(new(write.TenantId, write.Id, write.Input), session, ct);
            return result switch
            {
                BackupPolicy policy => new GitOperationResult(policy.Id, Changed: true),
                BackupSourceUnavailable => Rejected("source-unavailable"),
                BackupSourceUnsupported => Rejected("unsupported-provider"),
                BackupSourcePlacementUnavailable => Rejected("placement-unavailable"),
                BackupSourceRevisionChanged => Rejected("source-revision-changed"),
                BackupDestinationPlacementUnavailable => Rejected("destination-placement-unavailable"),
                BackupRetentionInvalid => Rejected("backup-positive-retention-required"),
                BackupPolicyDestinationUnavailable => Rejected("destination-unavailable"),
                BackupPolicyNameAlreadyExists => Rejected("policy-name-exists"),
                BackupPolicySourceAlreadyBound => Rejected("source-policy-exists"),
            };
        }

        var updated = await writes.UpdateAcceptedAsync(
            new(write.TenantId, write.Id, write.ExpectedRevision, write.Input),
            session,
            ct);
        return updated switch
        {
            BackupPolicy policy => new GitOperationResult(
                policy.Id,
                Changed: policy.Revision != write.ExpectedRevision),
            BackupSourceUnavailable => Rejected("source-unavailable"),
            BackupSourceUnsupported => Rejected("unsupported-provider"),
            BackupSourcePlacementUnavailable => Rejected("placement-unavailable"),
            BackupSourceRevisionChanged => Rejected("source-revision-changed"),
            BackupDestinationPlacementUnavailable => Rejected("destination-placement-unavailable"),
            BackupRetentionInvalid => Rejected("backup-positive-retention-required"),
            BackupPolicyDestinationUnavailable => Rejected("destination-unavailable"),
            BackupPolicyMissing => Rejected("not-found"),
            BackupPolicyRevisionConflict => Rejected("revision-conflict"),
            BackupPolicyIdentityImmutable => Rejected("policy-identity-immutable"),
            BackupPolicyStopping => Rejected("policy-stopping"),
        };

        GitOperationResult Rejected(string diagnostic) => new(write.Id, new BackupDeclarationRejected(diagnostic));
    }

    protected override async Task<RemoveLifecycleResourceOutcome> RemoveAcceptedAsync(
        string tenantId,
        string id,
        long revision,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var result = await deletion.ExecuteAcceptedAsync(new(tenantId, id, revision), session, ct);
        return result switch
        {
            BackupPolicyDeletionRequested => new GitLifecycleRemovalRequested(),
            BackupPolicyMissing => new DeclarationReadBlocked(new BackupDeclarationRejected("not-found")),
            BackupPolicyRevisionConflict => new DeclarationReadBlocked(
                new BackupDeclarationRejected("revision-conflict")),
        };
    }
}
