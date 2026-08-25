using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Pole.Tests.Architecture;

internal sealed record ArchitectureSource(
    string RelativePath,
    SyntaxTree SyntaxTree)
{
    public SyntaxNode Root => SyntaxTree.GetRoot();
}

internal static class ArchitectureSources
{
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public static IEnumerable<ArchitectureSource> ProductionCSharp() =>
        LoadSources(Path.Combine(RepositoryRoot, "src"));

    public static IEnumerable<ArchitectureSource> RuntimeMongoPersistenceCSharp() =>
        ProductionCSharp().Where(source =>
            source.RelativePath.Contains(".Application/Persistence/", StringComparison.Ordinal) &&
            !IsInDirectory(source, "Migrations"));

    public static ArchitectureSource ServerProgram() =>
        LoadSource(Path.Combine(RepositoryRoot, "src/api/Pole.Server/Program.cs"));

    public static string Describe(ArchitectureSource source, SyntaxNode node, string message)
    {
        var position = node.GetLocation().GetLineSpan().StartLinePosition;
        return $"{source.RelativePath}:{position.Line + 1}:{position.Character + 1}: {message}";
    }

    private static IEnumerable<ArchitectureSource> LoadSources(string directory) =>
        Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(LoadSource);

    private static ArchitectureSource LoadSource(string path) =>
        new(
            Path.GetRelativePath(RepositoryRoot, path).Replace(Path.DirectorySeparatorChar, '/'),
            CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path));

    private static bool IsInDirectory(ArchitectureSource source, string directory) =>
        source.RelativePath.Contains($"/{directory}/", StringComparison.Ordinal);

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not find the repository root from the architecture-test output directory.");
    }
}
