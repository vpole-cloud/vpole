namespace Pole.Features.GitProviders.Contracts;

public sealed record NamedGitProviderConnectionRequest(
    string TenantId,
    string TechnicalName);

public sealed record NamedGitProviderConnectionFound(string ConnectionId);

public sealed record NamedGitProviderConnectionNotFound(string TechnicalName);

public union FindNamedGitProviderConnectionOutcome(
    NamedGitProviderConnectionFound,
    NamedGitProviderConnectionNotFound);

public delegate Task<FindNamedGitProviderConnectionOutcome> FindNamedGitProviderConnection(
    NamedGitProviderConnectionRequest request,
    CancellationToken cancellationToken = default);
