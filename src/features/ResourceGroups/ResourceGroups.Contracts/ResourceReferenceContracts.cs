using MongoDB.Driver;
using Pole.ResultType;
using Pole.Workloads;
namespace Pole.Features.ResourceGroups.Contracts;

public sealed record ResourceScopeConsumerReference(ResourceTypeId ResourceType, string ResourceId);

// Admission takes the group write fence shared by deletion and unregistration.
public sealed record ResourceScopeReferenceUnavailable;

public delegate Task<Result<Unit, ResourceScopeReferenceUnavailable>> AdmitResourceScopeReference(
    string tenantId, string resourceGroupId, string isolationScopeId,
    ResourceScopeConsumerReference? consumer,
    IClientSessionHandle session, CancellationToken cancellationToken = default);
