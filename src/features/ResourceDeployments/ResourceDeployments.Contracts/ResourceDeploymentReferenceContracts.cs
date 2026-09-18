using MongoDB.Driver;
using Pole.ResultType;
using Pole.Workloads;
namespace Pole.Features.ResourceDeployments.Contracts;

public sealed record ResourceDeploymentReferenceUnavailable;

public delegate Task<Result<Unit, ResourceDeploymentReferenceUnavailable>> AdmitResourceDeploymentReferences(
    string tenantId, string resourceGroupId, string isolationScopeId,
    ResourceTypeId consumerType, string consumerId, bool allDeployments,
    IReadOnlyCollection<string> deploymentIds, IClientSessionHandle session,
    CancellationToken cancellationToken = default);
