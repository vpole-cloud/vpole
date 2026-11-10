using Pole.Testing;
using Xunit;

namespace Pole.Tests.E2E;

public sealed class GitOpsRepositoryExamplesTests
{
    [Fact]
    public void FullFixtureUsesCanonicalPathsAndOnlySubstitutesItsEnvironment()
    {
        var canonical = RepositoryExampleFiles.Read("src/features/GitOps/examples/full/manifests");
        var files = GitOpsRepositoryExamples.Full(
            new GitOpsExampleEnvironment(
                "test-target",
                "test-profile",
                "test-connection",
                "9001",
                "feature/application",
                new GitOpsExampleImage("registry.test:5000", "functions/sample", "test-tag")));

        Assert.Equal(canonical.Select(file => file.Path), files.Select(file => file.Path));
        Assert.Equal(15, files.Count);
        Assert.Contains("branch: feature/application", File("source-repository.yaml").Content);
        Assert.Contains("connection: test-connection", File("source-repository.yaml").Content);
        Assert.Contains("registry.test:5000", File("function-app.yaml").Content);
        Assert.Contains("functions/sample", File("function-app.yaml").Content);
        Assert.Contains("test-tag", File("function-app.yaml").Content);
        Assert.Contains("test-target", File("domain-zone.yaml").Content);
        Assert.Contains("test-profile", File("postgres-cluster.yaml").Content);
        Assert.Equal(
            canonical.Single(file => file.Path.EndsWith("/source-build-configuration.yaml")).Content,
            File("source-build-configuration.yaml").Content);
        Assert.DoesNotContain(files, file => file.Content.Contains("demo-", StringComparison.Ordinal));
        Assert.Contains(
            "branch: main",
            canonical.Single(file => file.Path.EndsWith("/source-repository.yaml")).Content);

        RepositoryExampleFile File(string name) => files.Single(file => file.Path.EndsWith('/' + name));
    }
}
