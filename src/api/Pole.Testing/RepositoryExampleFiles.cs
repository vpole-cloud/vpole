namespace Pole.Testing;

public sealed record RepositoryExampleFile(
    string Path,
    string Content);

public static class RepositoryExampleFiles
{
    public static IReadOnlyList<RepositoryExampleFile> Read(string repositoryDirectory,
        string pathPrefix = "manifests")
    {
        var directory = Path.Combine(RepositoryPaths.FindRoot(), repositoryDirectory);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Example directory does not exist: {repositoryDirectory}");

        return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path).Equals(".yaml", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(path).Equals(".yml", StringComparison.OrdinalIgnoreCase))
            .Select(path => new RepositoryExampleFile(
                Path.Combine(pathPrefix, Path.GetRelativePath(directory, path)).Replace('\\', '/'),
                File.ReadAllText(path)))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToArray();
    }
}
