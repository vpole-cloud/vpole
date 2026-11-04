using Pole.Testing;

namespace Pole.Tests.E2E;

internal sealed record GitOpsExampleImage(
    string Server,
    string Name,
    string Tag);

internal sealed record GitOpsExampleEnvironment(
    string Target,
    string Profile,
    string Connection,
    string ApplicationRepositoryId,
    string ApplicationBranch,
    GitOpsExampleImage FunctionImage);

internal static class GitOpsRepositoryExamples
{
    public static IReadOnlyList<RepositoryExampleFile> Full(GitOpsExampleEnvironment environment) =>
        RepositoryExampleFiles.Read("src/features/GitOps/examples/full/manifests")
            .Select(file => file with { Content = Substitute(file, environment) })
            .ToArray();

    private static string Substitute(RepositoryExampleFile file, GitOpsExampleEnvironment environment)
    {
        var content = file.Content
            .Replace("demo-target", environment.Target, StringComparison.Ordinal)
            .Replace("demo-compute", environment.Profile, StringComparison.Ordinal)
            .Replace("demo-storage", environment.Profile, StringComparison.Ordinal);

        if (file.Path.EndsWith("/source-repository.yaml", StringComparison.Ordinal))
            content = content
                .Replace("github-demo", environment.Connection, StringComparison.Ordinal)
                .Replace("123456789", environment.ApplicationRepositoryId, StringComparison.Ordinal)
                .Replace("branch: main", $"branch: {environment.ApplicationBranch}", StringComparison.Ordinal);

        if (file.Path.EndsWith("/function-app.yaml", StringComparison.Ordinal))
            content = content
                .Replace("registry.example.test", environment.FunctionImage.Server, StringComparison.Ordinal)
                .Replace("pole/functions", environment.FunctionImage.Name, StringComparison.Ordinal)
                .Replace("replace-with-sdk-image", environment.FunctionImage.Tag, StringComparison.Ordinal);

        return content;
    }
}
