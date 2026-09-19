using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Pole.Testing;

public sealed partial record TestRunIdentity(
    string WorktreeId,
    string RunId)
{
    public const string OwnerLabel = "pole.dev/owned-by";
    public const string WorktreeLabel = "pole.dev/worktree-id";
    public const string RunLabel = "pole.dev/test-run-id";
    public const string Owner = "pole-tests";
    public const string WorktreeEnvironmentVariable = "POLE_TEST_WORKTREE_ID";
    public const string RunEnvironmentVariable = "POLE_TEST_RUN_ID";

    public IReadOnlyDictionary<string, string> NamespaceLabels { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [OwnerLabel] = Owner,
            [WorktreeLabel] = WorktreeId,
            [RunLabel] = RunId
        };

    public static TestRunIdentity Create()
    {
        var worktreeId = Environment.GetEnvironmentVariable(WorktreeEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(worktreeId))
            worktreeId = ComputeWorktreeId(RepositoryPaths.FindRoot());

        var runId = Environment.GetEnvironmentVariable(RunEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(runId))
            runId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));

        return new TestRunIdentity(
            ValidateIdentifier(worktreeId, WorktreeEnvironmentVariable),
            ValidateIdentifier(runId, RunEnvironmentVariable));
    }

    public string CreateNamespaceName(string purpose)
    {
        var normalizedPurpose = new string(purpose
                .Trim()
                .ToLowerInvariant()
                .Where(character => char.IsAsciiLetterOrDigit(character) || character == '-')
                .ToArray())
            .Trim('-');
        if (string.IsNullOrWhiteSpace(normalizedPurpose))
            normalizedPurpose = "test";
        // Preserve room for both maximum-length IDs and the random suffix while
        // staying within Kubernetes' 63-character DNS label limit.
        if (normalizedPurpose.Length > 9)
            normalizedPurpose = normalizedPurpose[..9].TrimEnd('-');
        return $"pole-{WorktreeId}-{RunId}-{normalizedPurpose}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3))}";
    }

    private static string ComputeWorktreeId(string repositoryRoot)
    {
        var dotGit = Path.Combine(repositoryRoot, ".git");
        string gitDirectory;
        if (Directory.Exists(dotGit))
        {
            gitDirectory = Path.GetFullPath(dotGit);
        }
        else if (File.Exists(dotGit))
        {
            var marker = File.ReadAllText(dotGit).Trim();
            const string prefix = "gitdir:";
            if (!marker.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Git worktree marker '{dotGit}' is malformed.");
            var configured = marker[prefix.Length..].Trim();
            gitDirectory = Path.GetFullPath(configured, repositoryRoot);
        }
        else
        {
            throw new DirectoryNotFoundException($"Repository Git metadata was not found beneath '{repositoryRoot}'.");
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(gitDirectory));
        return Convert.ToHexStringLower(digest.AsSpan(0, 6));
    }

    internal static string ValidateIdentifier(string value, string description)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length is < 1 or > 19 || !IdentifierPattern().IsMatch(normalized))
        {
            throw new InvalidOperationException(
                $"{description} must be 1-19 lowercase alphanumeric or hyphen characters and must start and end with an alphanumeric character.");
        }
        return normalized;
    }

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$")]
    private static partial Regex IdentifierPattern();
}

public static class RepositoryPaths
{
    public static string FindRoot()
    {
        foreach (var origin in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var current = new DirectoryInfo(Path.GetFullPath(origin));
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "Pole.slnx")))
                    return current.FullName;
                current = current.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the Pole repository root containing Pole.slnx.");
    }

    public static string FindSharedRoot()
    {
        var root = FindRoot();
        var gitMarker = Path.Combine(root, ".git");
        if (Directory.Exists(gitMarker) || !File.Exists(gitMarker))
            return root;

        var marker = File.ReadAllText(gitMarker).Trim();
        const string prefix = "gitdir:";
        if (!marker.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Git worktree marker '{gitMarker}' is malformed.");
        var gitDirectory = marker[prefix.Length..].Trim();
        if (!Path.IsPathFullyQualified(gitDirectory))
            gitDirectory = Path.GetFullPath(Path.Combine(root, gitDirectory));
        var worktreesDirectory = Directory.GetParent(gitDirectory);
        var commonGitDirectory = worktreesDirectory?.Name == "worktrees"
            ? worktreesDirectory.Parent
            : null;
        var sharedRoot = commonGitDirectory?.Parent?.FullName;
        if (sharedRoot is null || !File.Exists(Path.Combine(sharedRoot, "Pole.slnx")))
            throw new InvalidOperationException($"Could not resolve the shared Pole worktree from '{gitMarker}'.");
        return sharedRoot;
    }
}
