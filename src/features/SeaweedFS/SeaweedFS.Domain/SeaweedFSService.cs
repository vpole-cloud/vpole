using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Pole.Multitenancy.Primitives;

namespace Pole.Features.SeaweedFS.Domain;

public enum SeaweedFSMode { SingleNode, Replicated }

public sealed record SeaweedFSStorage(
    int SizeGb,
    string StorageProfileId
)
{
    public const int MaximumSizeGb = 16_384;
}

public sealed record SeaweedFSSpec(
    SeaweedFSMode Mode,
    SeaweedFSStorage Storage,
    SeaweedFSMetadata Metadata
);

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(
    typeof(EmbeddedSeaweedFSMetadata),
    typeof(DedicatedPostgresSeaweedFSMetadata),
    typeof(ExistingPostgresSeaweedFSMetadata))]
public abstract record SeaweedFSMetadata;

public sealed record EmbeddedSeaweedFSMetadata : SeaweedFSMetadata;

public sealed record DedicatedPostgresSeaweedFSMetadata : SeaweedFSMetadata;

public sealed record ExistingPostgresSeaweedFSMetadata(
    string PostgresId
) : SeaweedFSMetadata;

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(
    typeof(ActiveSeaweedFSLifecycle),
    typeof(DeletingSeaweedFSLifecycle),
    typeof(PurgingSeaweedFSLifecycle),
    typeof(DeletedSeaweedFSLifecycle))]
public abstract record SeaweedFSLifecycle;

public sealed record ActiveSeaweedFSLifecycle : SeaweedFSLifecycle;

public sealed record DeletingSeaweedFSLifecycle(
    DateTime RequestedAtUtc,
    DateTime DeletionDeadlineUtc
) : SeaweedFSLifecycle;

public sealed record PurgingSeaweedFSLifecycle(
    DateTime RequestedAtUtc,
    DateTime DeletionDeadlineUtc
) : SeaweedFSLifecycle;

public sealed record DeletedSeaweedFSLifecycle(
    DateTime DeletedAtUtc
) : SeaweedFSLifecycle;

public sealed record SeaweedFSService(
    [property: BsonId, BsonRepresentation(BsonType.ObjectId)]
    string Id,
    string TenantId,
    string Name,
    string ResourceGroupId,
    SeaweedFSSpec Spec,
    ImmutableHashSet<string> Tags,
    string AdminSecretId,
    long Revision,
    SeaweedFSLifecycle Lifecycle,
    SeaweedFSQuotaCharge QuotaCharge,
    bool ReservesName
) : ITenantOwned
{
    public SeaweedFSService BeginDeletion(DateTime nowUtc, TimeSpan retention)
    {
        if (Lifecycle is not ActiveSeaweedFSLifecycle)
            return this;
        if (retention < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retention));
        if (nowUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("UTC required.", nameof(nowUtc));
        return this with
        {
            Revision = checked(Revision + 1),
            Lifecycle = new DeletingSeaweedFSLifecycle(nowUtc, nowUtc.Add(retention))
        };
    }
}

public static class SeaweedFSFactory
{
    public static SeaweedFSService Create(
        string tenantId,
        string name,
        string resourceGroupId,
        SeaweedFSSpec spec,
        ImmutableHashSet<string> tags,
        string adminSecretId,
        SeaweedFSQuotaCharge quotaCharge)
    {
        if (!ObjectId.TryParse(tenantId, out _) || !ObjectId.TryParse(resourceGroupId, out _) ||
            !ObjectId.TryParse(adminSecretId, out _))
            throw new ArgumentException("SeaweedFS identities must be ObjectIds.");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 30 ||
            !System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z0-9-]+$"))
            throw new ArgumentException("Invalid SeaweedFS name.", nameof(name));
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(spec.Storage);
        ArgumentNullException.ThrowIfNull(spec.Metadata);
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(quotaCharge);
        if (quotaCharge.CpuMillis <= 0 || quotaCharge.MemoryMi <= 0 || quotaCharge.Instances <= 0 ||
            quotaCharge.StorageGb <= 0)
            throw new ArgumentException("Physical quota charge must be positive.", nameof(quotaCharge));
        if ((spec.Mode == SeaweedFSMode.SingleNode && spec.Metadata is not EmbeddedSeaweedFSMetadata) ||
            (spec.Mode == SeaweedFSMode.Replicated && spec.Metadata is EmbeddedSeaweedFSMetadata) ||
            (spec.Metadata is ExistingPostgresSeaweedFSMetadata existing &&
                !ObjectId.TryParse(existing.PostgresId, out _)))
            throw new ArgumentException("Metadata topology does not match SeaweedFS mode.", nameof(spec));
        if (!Enum.IsDefined(spec.Mode) || spec.Storage.SizeGb is < 1 or > SeaweedFSStorage.MaximumSizeGb ||
            !ObjectId.TryParse(spec.Storage.StorageProfileId, out _))
            throw new ArgumentException("Invalid SeaweedFS specification.", nameof(spec));
        return new(
            ObjectId.GenerateNewId().ToString(),
            tenantId,
            name,
            resourceGroupId,
            spec,
            tags,
            adminSecretId,
            1,
            new ActiveSeaweedFSLifecycle(),
            quotaCharge,
            true);
    }
}

public sealed record SeaweedFSQuotaCharge(
    long CpuMillis,
    long MemoryMi,
    long Instances,
    long StorageGb
);
