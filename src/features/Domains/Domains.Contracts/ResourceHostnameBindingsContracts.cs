using MongoDB.Driver;
using Pole.Workloads;

namespace Pole.Features.Domains.Contracts;

public sealed record LoadHostnameBindingsForResourceInTransactionRequest(
    string TenantId,
    ResourceTypeId ResourceType,
    string ResourceId);

public delegate Task<IReadOnlyList<HostnameBindingSnapshot>> LoadHostnameBindingsForResourceInTransaction(
    LoadHostnameBindingsForResourceInTransactionRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken);
