using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Pole.Workloads;

namespace Pole.Features.ResourceConnections.Domain;

// The endpoint and resource IDs identify the declaration. The resolved placement
// is server-owned policy authority and remains available after resource deletion.
public sealed record ResourceConnection(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string TenantId,
    string Name,
    string SourceResourceTypeId,
    string SourceResourceId,
    string DestinationResourceTypeId,
    string DestinationResourceId,
    string EndpointId,
    long Revision,
    long Generation,
    ResourceConnectionLifecycle Lifecycle)
{
    public long ReferenceFence { get; init; }

    public bool IsCleanupComplete => Lifecycle is DeletingResourceConnection deleting &&
        deleting.Targets.All(target => target.Status is CompletedConnectionCleanup);

    public ResourceConnection BeginDeletion(DateTime requestedAtUtc)
    {
        if (Lifecycle is DeletingResourceConnection)
            return this;

        var active = (ActiveResourceConnection)Lifecycle;
        var cleanupTargets = active.Targets
            .Select(target => new CleanupConnectionTarget(
                target.Snapshot,
                new PendingConnectionCleanup()))
            .Concat(active.RetiringTargets
                .Select(target => target with { Status = new PendingConnectionCleanup() }))
            .ToImmutableList();
        return this with
        {
            Lifecycle = new DeletingResourceConnection(
                RequireUtc(requestedAtUtc, nameof(requestedAtUtc)),
                cleanupTargets),
            Revision = checked(Revision + 1),
            Generation = checked(Generation + 1)
        };
    }

    // A provider may change a port while retaining the same endpoint ID. Every
    // target receives a new desired generation; older acknowledgements cannot win.
    public ResourceConnection RefreshResolvedTargets(
        IEnumerable<ConnectionTargetSnapshot> resolvedTargets)
    {
        if (Lifecycle is not ActiveResourceConnection active)
            throw new InvalidOperationException("A deleting connection cannot be reactivated.");

        var copied = ResourceConnectionFactory.CopyTargets(resolvedTargets);
        var currentLocations = copied
            .Select(snapshot => snapshot.PolicyLocation)
            .ToHashSet();
        var retiring = active.RetiringTargets
            .Where(target => !currentLocations.Contains(target.Snapshot.PolicyLocation))
            .Select(target => target with { Status = new PendingConnectionCleanup() })
            .Concat(active.Targets
                .Where(target => !currentLocations.Contains(target.Snapshot.PolicyLocation))
                .Select(target => new CleanupConnectionTarget(
                    target.Snapshot,
                    new PendingConnectionCleanup())))
            .ToImmutableList();

        return this with
        {
            Lifecycle = new ActiveResourceConnection(
                copied
                    .Select(snapshot => new ApplyConnectionTarget(
                        snapshot,
                        new PendingConnectionApply()))
                    .ToImmutableList(),
                retiring),
            Revision = checked(Revision + 1),
            Generation = checked(Generation + 1)
        };
    }

    public ConnectionApplyOutcome RecordApplied(
        string targetId,
        long expectedGeneration,
        DateTime appliedAtUtc) =>
        ChangeApplyStatus(
            targetId,
            expectedGeneration,
            new AppliedConnectionApply(RequireUtc(appliedAtUtc, nameof(appliedAtUtc))));

    public ConnectionApplyOutcome RecordApplyFailure(
        string targetId,
        long expectedGeneration,
        string reason,
        DateTime failedAtUtc) =>
        ChangeApplyStatus(
            targetId,
            expectedGeneration,
            new FailedConnectionApply(
                RequireValue(reason, nameof(reason)),
                RequireUtc(failedAtUtc, nameof(failedAtUtc))));

    // A fresh policy read may reveal drift after an earlier successful apply.
    // Persistence must fence this transition by the exact observed revision.
    public ConnectionApplyOutcome RecordVerificationFailure(
        string targetId,
        long expectedGeneration,
        string reason,
        DateTime failedAtUtc) =>
        ChangeApplyStatus(
            targetId,
            expectedGeneration,
            new FailedConnectionApply(
                RequireValue(reason, nameof(reason)),
                RequireUtc(failedAtUtc, nameof(failedAtUtc))),
            allowAppliedDowngrade: true);

    public ConnectionCleanupOutcome RecordCleanupCompleted(
        ConnectionPolicyLocation location,
        long expectedGeneration,
        DateTime completedAtUtc) =>
        ChangeCleanupStatus(
            location,
            expectedGeneration,
            new CompletedConnectionCleanup(
                RequireUtc(completedAtUtc, nameof(completedAtUtc))));

    public ConnectionCleanupOutcome RecordCleanupFailure(
        ConnectionPolicyLocation location,
        long expectedGeneration,
        string reason,
        DateTime failedAtUtc) =>
        ChangeCleanupStatus(
            location,
            expectedGeneration,
            new FailedConnectionCleanup(
                RequireValue(reason, nameof(reason)),
                RequireUtc(failedAtUtc, nameof(failedAtUtc))));

    // A retired location remains cleanup authority after an earlier successful
    // delete. A later read can find an old in-flight write at that location.
    public ConnectionCleanupOutcome RecordCleanupVerificationFailure(
        ConnectionPolicyLocation location,
        long expectedGeneration,
        string reason,
        DateTime failedAtUtc) =>
        ChangeCleanupStatus(
            location,
            expectedGeneration,
            new FailedConnectionCleanup(
                RequireValue(reason, nameof(reason)),
                RequireUtc(failedAtUtc, nameof(failedAtUtc))),
            allowCompletedDowngrade: true);

    private ConnectionApplyOutcome ChangeApplyStatus(
        string targetId,
        long expectedGeneration,
        ConnectionApplyStatus status,
        bool allowAppliedDowngrade = false)
    {
        if (expectedGeneration != Generation)
            return new StaleConnectionApply();
        if (Lifecycle is not ActiveResourceConnection active)
            return new ConnectionNoLongerActive();
        var existing = active.Targets.FirstOrDefault(target => target.Snapshot.TargetId == targetId);
        if (existing is null)
            return new UnknownConnectionApplyTarget(targetId);
        if (existing.Status is AppliedConnectionApply && !allowAppliedDowngrade)
            return new ConnectionApplyAlreadyApplied();

        return new ConnectionApplyRecorded(this with
        {
            Lifecycle = active with
            {
                Targets = active.Targets
                    .Select(target => target.Snapshot.TargetId == targetId
                        ? target with { Status = status }
                        : target)
                    .ToImmutableList()
            },
            Revision = checked(Revision + 1)
        });
    }

    private ConnectionCleanupOutcome ChangeCleanupStatus(
        ConnectionPolicyLocation location,
        long expectedGeneration,
        ConnectionCleanupStatus status,
        bool allowCompletedDowngrade = false)
    {
        if (expectedGeneration != Generation)
            return new StaleConnectionCleanup();
        if (Lifecycle is ActiveResourceConnection active &&
            active.Targets.Any(target => target.Snapshot.PolicyLocation == location))
        {
            return new CurrentConnectionPolicyLocation(location);
        }

        var targets = Lifecycle switch
        {
            ActiveResourceConnection current => current.RetiringTargets,
            DeletingResourceConnection deleting => deleting.Targets
        };
        var existing = targets.FirstOrDefault(target => target.Snapshot.PolicyLocation == location);
        if (existing is null)
            return new UnknownConnectionCleanupTarget(location);
        if (existing.Status is CompletedConnectionCleanup && !allowCompletedDowngrade)
            return new ConnectionCleanupAlreadyCompleted();

        var updatedTargets = targets
            .Select(target => target.Snapshot.PolicyLocation == location
                ? target with { Status = status }
                : target)
            .ToImmutableList();
        return new ConnectionCleanupRecorded(this with
        {
            Lifecycle = Lifecycle switch
            {
                ActiveResourceConnection current => current with
                {
                    RetiringTargets = updatedTargets
                },
                DeletingResourceConnection deleting => deleting with
                {
                    Targets = updatedTargets
                }
            },
            Revision = checked(Revision + 1)
        });
    }

    internal static string RequireValue(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A value is required.", parameterName);

        return value.Trim();
    }

    internal static DateTime RequireUtc(DateTime value, string parameterName)
    {
        if (value.Kind != DateTimeKind.Utc)
            throw new ArgumentException("The timestamp must be UTC.", parameterName);

        return value;
    }
}

public sealed record ConnectionSourcePlacement(
    string ResourceDeploymentId,
    string RuntimeCellId,
    string Namespace);

// A connection owns one policy object at each destination location. A port or
// selector change at the same location replaces that object; a location change
// requires cleanup of the old object.
public sealed record ConnectionPolicyLocation(
    string TargetId,
    string DestinationRuntimeCellId,
    string DestinationNamespace);

public sealed record ConnectionTargetSnapshot(
    string TargetId,
    string DestinationResourceDeploymentId,
    string DestinationRuntimeCellId,
    string DestinationNamespace,
    string DestinationServiceName,
    int DestinationServicePort,
    int DestinationPort,
    ImmutableList<ConnectionSourcePlacement> Sources)
{
    [BsonIgnore]
    public ConnectionPolicyLocation PolicyLocation => new(
        TargetId,
        DestinationRuntimeCellId,
        DestinationNamespace);

    public bool HasSameResolvedValues(ConnectionTargetSnapshot other) =>
        TargetId == other.TargetId &&
        DestinationResourceDeploymentId == other.DestinationResourceDeploymentId &&
        DestinationRuntimeCellId == other.DestinationRuntimeCellId &&
        DestinationNamespace == other.DestinationNamespace &&
        DestinationServiceName == other.DestinationServiceName &&
        DestinationServicePort == other.DestinationServicePort &&
        DestinationPort == other.DestinationPort &&
        Sources.SequenceEqual(other.Sources);

    public static ConnectionTargetSnapshot Create(
        string targetId,
        string destinationResourceDeploymentId,
        string destinationRuntimeCellId,
        string destinationNamespace,
        string destinationServiceName,
        int destinationServicePort,
        int destinationPort,
        IEnumerable<ConnectionSourcePlacement> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _ = NetworkPort.Create(destinationServicePort);
        _ = NetworkPort.Create(destinationPort);

        var copiedSources = sources.ToImmutableList();
        if (copiedSources.Count == 0 ||
            copiedSources.Any(source => source is null ||
                string.IsNullOrWhiteSpace(source.ResourceDeploymentId) ||
                string.IsNullOrWhiteSpace(source.RuntimeCellId) ||
                string.IsNullOrWhiteSpace(source.Namespace)) ||
            copiedSources.Select(source => source.ResourceDeploymentId.Trim())
                .Distinct(StringComparer.Ordinal).Count() != copiedSources.Count)
        {
            throw new ArgumentException(
                "A target requires distinct, fully resolved source deployments.",
                nameof(sources));
        }

        var normalizedSources = copiedSources
            .Select(source => new ConnectionSourcePlacement(
                ResourceConnection.RequireValue(
                    source.ResourceDeploymentId,
                    nameof(sources)),
                ResourceConnection.RequireValue(source.RuntimeCellId, nameof(sources)),
                ResourceConnection.RequireValue(source.Namespace, nameof(sources))))
            .OrderBy(source => source.ResourceDeploymentId, StringComparer.Ordinal)
            .ToImmutableList();

        return new ConnectionTargetSnapshot(
            ResourceConnection.RequireValue(targetId, nameof(targetId)),
            ResourceConnection.RequireValue(
                destinationResourceDeploymentId,
                nameof(destinationResourceDeploymentId)),
            ResourceConnection.RequireValue(
                destinationRuntimeCellId,
                nameof(destinationRuntimeCellId)),
            ResourceConnection.RequireValue(destinationNamespace, nameof(destinationNamespace)),
            ResourceConnection.RequireValue(destinationServiceName, nameof(destinationServiceName)),
            destinationServicePort,
            destinationPort,
            normalizedSources);
    }
}

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(typeof(ActiveResourceConnection), typeof(DeletingResourceConnection))]
public closed record ResourceConnectionLifecycle;

public sealed record ActiveResourceConnection(
    ImmutableList<ApplyConnectionTarget> Targets,
    ImmutableList<CleanupConnectionTarget> RetiringTargets) : ResourceConnectionLifecycle;

public sealed record DeletingResourceConnection(
    DateTime RequestedAtUtc,
    ImmutableList<CleanupConnectionTarget> Targets) : ResourceConnectionLifecycle;

public sealed record ApplyConnectionTarget(
    ConnectionTargetSnapshot Snapshot,
    ConnectionApplyStatus Status);

public sealed record CleanupConnectionTarget(
    ConnectionTargetSnapshot Snapshot,
    ConnectionCleanupStatus Status);

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(
    typeof(PendingConnectionApply),
    typeof(AppliedConnectionApply),
    typeof(FailedConnectionApply))]
public closed record ConnectionApplyStatus;

public sealed record PendingConnectionApply : ConnectionApplyStatus;

public sealed record AppliedConnectionApply(DateTime AppliedAtUtc) : ConnectionApplyStatus;

public sealed record FailedConnectionApply(
    string Reason,
    DateTime FailedAtUtc) : ConnectionApplyStatus;

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(
    typeof(PendingConnectionCleanup),
    typeof(CompletedConnectionCleanup),
    typeof(FailedConnectionCleanup))]
public closed record ConnectionCleanupStatus;

public sealed record PendingConnectionCleanup : ConnectionCleanupStatus;

public sealed record CompletedConnectionCleanup(DateTime CompletedAtUtc) : ConnectionCleanupStatus;

public sealed record FailedConnectionCleanup(
    string Reason,
    DateTime FailedAtUtc) : ConnectionCleanupStatus;

public sealed record ConnectionApplyRecorded(ResourceConnection Connection);

public sealed record StaleConnectionApply;

public sealed record ConnectionNoLongerActive;

public sealed record ConnectionApplyAlreadyApplied;

public sealed record UnknownConnectionApplyTarget(string TargetId);

public union ConnectionApplyOutcome(
    ConnectionApplyRecorded,
    StaleConnectionApply,
    ConnectionNoLongerActive,
    ConnectionApplyAlreadyApplied,
    UnknownConnectionApplyTarget);

public sealed record ConnectionCleanupRecorded(ResourceConnection Connection);

public sealed record StaleConnectionCleanup;

public sealed record CurrentConnectionPolicyLocation(ConnectionPolicyLocation Location);

public sealed record ConnectionCleanupAlreadyCompleted;

public sealed record UnknownConnectionCleanupTarget(ConnectionPolicyLocation Location);

public union ConnectionCleanupOutcome(
    ConnectionCleanupRecorded,
    StaleConnectionCleanup,
    CurrentConnectionPolicyLocation,
    ConnectionCleanupAlreadyCompleted,
    UnknownConnectionCleanupTarget);

public static class ResourceConnectionFactory
{
    public static ResourceConnection Create(
        string id,
        string tenantId,
        string name,
        ResourceTypeId sourceResourceTypeId,
        string sourceResourceId,
        ResourceTypeId destinationResourceTypeId,
        string destinationResourceId,
        string endpointId,
        IEnumerable<ConnectionTargetSnapshot> resolvedTargets)
    {
        if (!ObjectId.TryParse(id, out _))
            throw new ArgumentException("Connection ID must be a Mongo ObjectId.", nameof(id));

        var normalizedName = ResourceConnection.RequireValue(name, nameof(name));
        if (normalizedName.Length > 30 ||
            normalizedName.Any(character => character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '-'))
        {
            throw new ArgumentException(
                "Name must contain at most 30 lowercase letters, numbers, or hyphens.",
                nameof(name));
        }

        return new ResourceConnection(
            id,
            ResourceConnection.RequireValue(tenantId, nameof(tenantId)),
            normalizedName,
            ResourceTypeId.Create(sourceResourceTypeId.Value).Value,
            ResourceConnection.RequireValue(sourceResourceId, nameof(sourceResourceId)),
            ResourceTypeId.Create(destinationResourceTypeId.Value).Value,
            ResourceConnection.RequireValue(destinationResourceId, nameof(destinationResourceId)),
            ResourceConnection.RequireValue(endpointId, nameof(endpointId)),
            Revision: 1,
            Generation: 1,
            Lifecycle: new ActiveResourceConnection(
                CopyTargets(resolvedTargets)
                    .Select(snapshot => new ApplyConnectionTarget(
                        snapshot,
                        new PendingConnectionApply()))
                    .ToImmutableList(),
                ImmutableList<CleanupConnectionTarget>.Empty));
    }

    internal static ImmutableList<ConnectionTargetSnapshot> CopyTargets(
        IEnumerable<ConnectionTargetSnapshot> resolvedTargets)
    {
        ArgumentNullException.ThrowIfNull(resolvedTargets);
        var supplied = resolvedTargets.ToImmutableList();
        if (supplied.Count == 0 || supplied.Any(target => target is null))
            throw new ArgumentException("A connection requires resolved targets.", nameof(resolvedTargets));

        var copied = supplied
            .Select(target => ConnectionTargetSnapshot.Create(
                target.TargetId,
                target.DestinationResourceDeploymentId,
                target.DestinationRuntimeCellId,
                target.DestinationNamespace,
                target.DestinationServiceName,
                target.DestinationServicePort,
                target.DestinationPort,
                target.Sources))
            .OrderBy(target => target.TargetId, StringComparer.Ordinal)
            .ToImmutableList();
        if (copied.Select(target => target.TargetId)
                .Distinct(StringComparer.Ordinal).Count() != copied.Count ||
            copied.Select(target => target.PolicyLocation)
                .Distinct().Count() != copied.Count ||
            copied.SelectMany(target => target.Sources)
                .Select(source => source.ResourceDeploymentId)
                .Distinct(StringComparer.Ordinal).Count() !=
            copied.Sum(target => target.Sources.Count))
        {
            throw new ArgumentException(
                "A connection requires unique, fully resolved target placements.",
                nameof(resolvedTargets));
        }

        return copied;
    }
}
