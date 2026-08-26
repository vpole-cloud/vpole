using MongoDB.Driver;
using Pole.Features.GitProviders.Domain;

namespace Pole.Features.GitProviders.Application.Persistence;

public delegate Task CreateGitHubSetupAttempt(
    GitHubSetupAttempt attempt,
    CancellationToken cancellationToken = default);

internal sealed class MongoCreateGitHubSetupAttempt(IMongoDatabase database)
{
    public Task InvokeAsync(
        GitHubSetupAttempt attempt,
        CancellationToken cancellationToken = default) =>
        database
            .GetCollection<GitHubSetupAttempt>("GitHubSetupAttempts")
            .InsertOneAsync(attempt, cancellationToken: cancellationToken);
}
