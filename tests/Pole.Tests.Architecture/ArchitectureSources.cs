// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Pole.ArchitectureAnalyzers;

namespace Pole.Tests.Architecture;

internal sealed record ArchitectureSource(
    string RelativePath,
    SyntaxTree SyntaxTree
)
{
    public SyntaxNode Root => SyntaxTree.GetRoot();
}

internal sealed record ArchitectureTextSource(
    string RelativePath,
    string Text
)
{
    public IEnumerable<(int Number, string Text)> Lines =>
        Text.Split('\n').Select((line, index) => (index + 1, line.TrimEnd('\r')));
}

internal static class ArchitectureSources
{
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public static IEnumerable<ArchitectureSource> ProductionCSharp() =>
        LoadSources(Path.Combine(RepositoryRoot, "src"));

    public static IEnumerable<ArchitectureSource> RepositoryCSharp() => LoadSources(RepositoryRoot);

    public static IEnumerable<ArchitectureTextSource> ProductionFSharp() =>
        LoadTextSources(Path.Combine(RepositoryRoot, "src"), "*.fs");

    public static IEnumerable<ArchitectureTextSource> RepositoryFSharp() => LoadTextSources(RepositoryRoot, "*.fs");

    public static IEnumerable<ArchitectureSource> ServerCSharp() =>
        LoadSources(Path.Combine(RepositoryRoot, "src/api/Pole.Server"));

    public static IEnumerable<ArchitectureSource> RuntimeApplicationCSharp() => ProductionCSharp()
        .Where(source =>
            MongoApplicationPaths.Classify(source.RelativePath) != MongoApplicationPath.OutsideApplication);

    public static IEnumerable<ArchitectureTextSource> RuntimeApplicationFSharp() => ProductionFSharp()
        .Where(source =>
            MongoApplicationPaths.Classify(source.RelativePath) != MongoApplicationPath.OutsideApplication);

    public static ArchitectureSource ServerProgram() =>
        LoadSource(Path.Combine(RepositoryRoot, "src/api/Pole.Server/Program.cs"));

    public static string Describe(
        ArchitectureSource source,
        SyntaxNode node,
        string message)
    {
        var position = node.GetLocation().GetLineSpan().StartLinePosition;
        return $"{source.RelativePath}:{position.Line + 1}:{position.Character + 1}: {message}";
    }

    public static string Describe(
        ArchitectureTextSource source,
        int lineNumber,
        string message) => $"{source.RelativePath}:{lineNumber}: {message}";

    private static IEnumerable<ArchitectureSource> LoadSources(string directory) => SourceFiles(directory, "*.cs")
        .OrderBy(path => path, StringComparer.Ordinal)
        .Select(LoadSource);

    private static ArchitectureSource LoadSource(string path) => new(
        Path.GetRelativePath(RepositoryRoot, path).Replace(Path.DirectorySeparatorChar, '/'),
        CSharpSyntaxTree.ParseText(
            File.ReadAllText(path),
            new CSharpParseOptions(LanguageVersion.Preview),
            path: path));

    private static IEnumerable<ArchitectureTextSource> LoadTextSources(string directory, string searchPattern) =>
        SourceFiles(directory, searchPattern)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new ArchitectureTextSource(
                Path.GetRelativePath(RepositoryRoot, path).Replace(Path.DirectorySeparatorChar, '/'),
                File.ReadAllText(path)));

    internal static IEnumerable<string> SourceFiles(string directory, string searchPattern)
    {
        foreach (var path in Directory.EnumerateFiles(directory, searchPattern))
            yield return path;

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            // Local agent metadata is not repository source and can disappear while tools detach.
            if (Path.GetFileName(child) is "bin" or "obj" or ".artifacts" or ".tmp" or ".git" or ".agents"
                or "node_modules")
                continue;

            foreach (var path in SourceFiles(child, searchPattern))
                yield return path;
        }
    }

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
