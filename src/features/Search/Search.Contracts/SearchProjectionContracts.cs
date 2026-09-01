using Pole.Workloads;

namespace Pole.Features.Search.Contracts;

public sealed record SearchSourceType(
    ResourceTypeId ResourceTypeId,
    string ApiName,
    int SortOrder);

public sealed record SearchProjectionIdentity(
    string TenantId,
    ResourceTypeId ResourceTypeId,
    string SourceId);

public enum SearchProjectionFieldType
{
    SourceId = 0,
    Name = 1,
    Tag = 2,
    Domain = 3,
    Image = 4,
    ResourceGroupName = 5,
    StorageClassName = 6,
    ResourceName = 7,
    Username = 8,
    GroupId = 9
}

public sealed record SearchProjectionField(
    SearchProjectionFieldType Type,
    string Value);

public sealed record SearchProjectionSnapshot(
    SearchProjectionIdentity Identity,
    string Title,
    string? ResourceGroupId,
    string? ResourceGroupName,
    IReadOnlyCollection<SearchProjectionField> Fields);

public sealed record SearchOperationalStatusSnapshot(
    long DesiredGeneration,
    long ObservedGeneration,
    string Status,
    string? FailureCode,
    DateTime EventOccurredAtUtc);

public delegate Task CreateSearchProjection(
    SearchProjectionSnapshot snapshot,
    CancellationToken cancellationToken = default);

public delegate Task UpdateSearchProjection(
    SearchProjectionSnapshot snapshot,
    CancellationToken cancellationToken = default);

public delegate Task DeleteSearchProjection(
    SearchProjectionIdentity identity,
    CancellationToken cancellationToken = default);

public delegate Task UpdateSearchOperationalStatus(
    SearchProjectionIdentity identity,
    SearchOperationalStatusSnapshot status,
    CancellationToken cancellationToken = default);

public delegate SearchSourceType? ResolveSearchSourceType(
    ResourceTypeId resourceTypeId);
