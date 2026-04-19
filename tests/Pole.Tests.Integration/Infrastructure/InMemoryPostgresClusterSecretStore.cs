using System.Collections.Concurrent;
using Microsoft.FSharp.Core;
using Pole.Features.Postgres.Kubernetes;

namespace Pole.Tests.Integration.Infrastructure;

public sealed class InMemoryPostgresClusterSecretStore : IPostgresClusterSecretStore
{
    private readonly ConcurrentDictionary<(string Namespace, string ClusterId), PostgresClusterCredentials> _credentials = new();

    public Task CreateAsync(string namespaceName, string clusterId, string username, string password, CancellationToken cancellationToken)
    {
        _credentials[(namespaceName, clusterId)] = new PostgresClusterCredentials(username, password);
        return Task.CompletedTask;
    }

    public Task UpdatePasswordAsync(string namespaceName, string clusterId, string password, CancellationToken cancellationToken)
    {
        if (!_credentials.TryGetValue((namespaceName, clusterId), out var existing))
        {
            throw new InvalidOperationException($"Credentials for cluster '{clusterId}' in namespace '{namespaceName}' were not found.");
        }

        _credentials[(namespaceName, clusterId)] = new PostgresClusterCredentials(existing.Username, password);
        return Task.CompletedTask;
    }

    public Task<FSharpOption<PostgresClusterCredentials>> GetAsync(string namespaceName, string clusterId, CancellationToken cancellationToken)
    {
        _credentials.TryGetValue((namespaceName, clusterId), out var credentials);
        return Task.FromResult(
            credentials is null
                ? FSharpOption<PostgresClusterCredentials>.None
                : FSharpOption<PostgresClusterCredentials>.Some(credentials));
    }

    public PostgresClusterCredentials? GetStored(string namespaceName, string clusterId)
    {
        _credentials.TryGetValue((namespaceName, clusterId), out var credentials);
        return credentials;
    }
}
