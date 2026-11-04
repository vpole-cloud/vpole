using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Pole.Features.GitProviders.Domain;
using Xunit;

namespace Pole.Features.GitProviders.Tests;

public sealed class GitProviderIdentityTests
{
    [Fact]
    public void ConnectionAndSetupRetainExplicitIdentityThroughBson()
    {
        var connection = GitProviderConnectionFactory.Create(
            "tenant", "production-git", GitProviderKind.GitHub, "42", "account", "display",
            GitProviderAccountType.Organization, GitProviderRepositorySelection.Selected, "author",
            GitProviderConnectionFactory.DefaultOptions with
            { CreatedAtUtc = DateTime.UnixEpoch, UpdatedAtUtc = DateTime.UnixEpoch });
        var attempt = GitHubSetupAttemptFactory.Create("tenant", "author", "production-git", "hash",
            GitHubSetupAttemptFactory.DefaultOptions with
            { CreatedAtUtc = DateTime.UnixEpoch, ExpiresAtUtc = DateTime.UnixEpoch.AddMinutes(10) });

        Assert.Equal(connection, BsonSerializer.Deserialize<GitProviderConnection>(connection.ToBson()));
        Assert.Equal(attempt, BsonSerializer.Deserialize<GitHubSetupAttempt>(attempt.ToBson()));
        Assert.IsType<AwaitingGitHubInstallation>(attempt.State);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Uppercase")]
    [InlineData("trailing-")]
    [InlineData("with/slash")]
    public void FactoriesRejectInvalidTechnicalNames(string name)
    {
        Assert.Throws<ArgumentException>(() => GitProviderConnectionFactory.Create(
            "tenant", name, GitProviderKind.GitHub, "42", "account", "display", GitProviderAccountType.User,
            GitProviderRepositorySelection.All, "author", GitProviderConnectionFactory.DefaultOptions));
        Assert.Throws<ArgumentException>(() => GitHubSetupAttemptFactory.Create("tenant", "author", name,
            "hash", GitHubSetupAttemptFactory.DefaultOptions));
    }
}
