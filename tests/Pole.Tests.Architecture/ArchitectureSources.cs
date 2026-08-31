using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Pole.Tests.Architecture;

internal sealed record ArchitectureSource(
    string RelativePath,
    SyntaxTree SyntaxTree)
{
    public SyntaxNode Root => SyntaxTree.GetRoot();
}

internal sealed record ArchitectureTextSource(
    string RelativePath,
    string Text)
{
    public IEnumerable<(int Number, string Text)> Lines =>
        Text.Split('\n').Select((line, index) => (index + 1, line.TrimEnd('\r')));
}

internal static class ArchitectureSources
{
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public static IEnumerable<ArchitectureSource> ProductionCSharp() =>
        LoadSources(Path.Combine(RepositoryRoot, "src"));

    public static IEnumerable<ArchitectureSource> RepositoryCSharp() =>
        LoadSources(RepositoryRoot);

    public static IEnumerable<ArchitectureTextSource> ProductionFSharp() =>
        LoadTextSources(Path.Combine(RepositoryRoot, "src"), "*.fs");

    public static IEnumerable<ArchitectureTextSource> RepositoryFSharp() =>
        LoadTextSources(RepositoryRoot, "*.fs");

    public static IEnumerable<ArchitectureSource> ServerCSharp() =>
        LoadSources(Path.Combine(RepositoryRoot, "src/api/Pole.Server"));

    public static IEnumerable<ArchitectureSource> RuntimeMongoPersistenceCSharp() =>
        ProductionCSharp().Where(source =>
            source.RelativePath.Contains(".Application/Persistence/", StringComparison.Ordinal) &&
            !IsInDirectory(source, "Migrations"));

    public static IEnumerable<ArchitectureTextSource> RuntimeMongoPersistenceFSharp() =>
        ProductionFSharp().Where(source =>
            source.RelativePath.Contains(".Application/Persistence/", StringComparison.Ordinal) &&
            !source.RelativePath.Contains("/Migrations/", StringComparison.Ordinal));

    public static ArchitectureSource ServerProgram() =>
        LoadSource(Path.Combine(RepositoryRoot, "src/api/Pole.Server/Program.cs"));

    public static string Describe(ArchitectureSource source, SyntaxNode node, string message)
    {
        var position = node.GetLocation().GetLineSpan().StartLinePosition;
        return $"{source.RelativePath}:{position.Line + 1}:{position.Character + 1}: {message}";
    }

    public static string Describe(
        ArchitectureTextSource source,
        int lineNumber,
        string message) =>
        $"{source.RelativePath}:{lineNumber}: {message}";

    private static IEnumerable<ArchitectureSource> LoadSources(string directory) =>
        Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(LoadSource);

    private static ArchitectureSource LoadSource(string path) =>
        new(
            Path.GetRelativePath(RepositoryRoot, path).Replace(Path.DirectorySeparatorChar, '/'),
            CSharpSyntaxTree.ParseText(File.ReadAllText(path), path: path));

    private static IEnumerable<ArchitectureTextSource> LoadTextSources(
        string directory,
        string searchPattern) =>
        Directory.EnumerateFiles(directory, searchPattern, SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new ArchitectureTextSource(
                Path.GetRelativePath(RepositoryRoot, path)
                    .Replace(Path.DirectorySeparatorChar, '/'),
                File.ReadAllText(path)));

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
