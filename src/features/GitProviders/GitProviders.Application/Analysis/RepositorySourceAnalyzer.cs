using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Domain;
using Pole.ResultType;

namespace Pole.Features.GitProviders.Application.Analysis;

public interface IRepositorySourceAnalyzer
{
    Result<SourceRepositoryAnalysis> Analyze(RepositorySourceSnapshot snapshot);
}

public sealed class RepositorySourceAnalyzer(TimeProvider timeProvider) : IRepositorySourceAnalyzer
{
    public Result<SourceRepositoryAnalysis> Analyze(RepositorySourceSnapshot snapshot)
    {
        if (snapshot.IsTruncated)
        {
            return Failures.ValidationFailure(new Dictionary<string, string[]>
            {
                ["Repository"] = ["The repository tree is too large for automatic analysis."]
            });
        }

        var candidates = AnalyzeDockerfiles(snapshot)
            .Concat(AnalyzeDotNetProjects(snapshot))
            .Concat(AnalyzeNodeProjects(snapshot))
            .ToImmutableList();

        var detectedFiles = snapshot.Paths
            .Where(IsBuildSignal)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToImmutableList();

        return new SourceRepositoryAnalysis(
            snapshot.Branch,
            snapshot.CommitSha,
            timeProvider.GetUtcNow().UtcDateTime,
            detectedFiles,
            candidates);
    }

    private static IEnumerable<SourceBuildPlan> AnalyzeDockerfiles(RepositorySourceSnapshot snapshot)
    {
        foreach (var path in snapshot.Paths.Where(path =>
                     Path.GetFileName(path).StartsWith("Dockerfile", StringComparison.OrdinalIgnoreCase)))
        {
            var root = NormalizeRoot(Path.GetDirectoryName(path));
            var warnings = new List<string>();
            var preview = snapshot.FileContents.GetValueOrDefault(path, string.Empty);
            if (string.IsNullOrWhiteSpace(preview))
                warnings.Add("The Dockerfile could not be loaded for preview.");

            yield return new SourceBuildPlan(
                CandidateKey(SourceBuildPlanKind.Dockerfile, path),
                SourceBuildPlanKind.Dockerfile,
                $"Dockerfile at {path}",
                root,
                path,
                warnings.Count == 0 ? SourceBuildPlanConfidence.High : SourceBuildPlanConfidence.Medium,
                [path],
                warnings.ToImmutableList(),
                [$"docker build -f \"{path}\" \"{root}\""],
                StartCommand: null,
                DockerfilePreview: preview);
        }
    }

    private static IEnumerable<SourceBuildPlan> AnalyzeDotNetProjects(RepositorySourceSnapshot snapshot)
    {
        var solutionFiles = snapshot.Paths
            .Where(path => path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var path in snapshot.Paths.Where(path =>
                     path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                     path.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)))
        {
            var root = NormalizeRoot(Path.GetDirectoryName(path));
            var projectName = Path.GetFileNameWithoutExtension(path);
            var targetFramework = "net11.0";
            var assemblyName = projectName;
            var outputType = string.Empty;
            var sdk = string.Empty;
            var warnings = new List<string>();
            var signals = new List<string> { path };

            if (snapshot.FileContents.TryGetValue(path, out var projectFile))
            {
                try
                {
                    var document = XDocument.Parse(projectFile);
                    sdk = document.Root?.Attribute("Sdk")?.Value ?? string.Empty;
                    targetFramework = FindProperty(document, "TargetFramework") ??
                                      FindProperty(document, "TargetFrameworks")?.Split(';')[0] ??
                                      targetFramework;
                    assemblyName = FindProperty(document, "AssemblyName") ?? assemblyName;
                    outputType = FindProperty(document, "OutputType") ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(sdk))
                        signals.Add($"SDK: {sdk}");
                    signals.Add($"Target: {targetFramework}");
                }
                catch (Exception exception) when (exception is System.Xml.XmlException or InvalidOperationException)
                {
                    warnings.Add("The project file could not be parsed; defaults were inferred from its path.");
                }
            }
            else
            {
                warnings.Add("The project file could not be loaded; defaults were inferred from its path.");
            }

            var nearbySolution = solutionFiles.FirstOrDefault(solution =>
                IsAtOrAbove(Path.GetDirectoryName(solution), root));
            if (nearbySolution is not null)
                signals.Add(nearbySolution);

            var isWeb = sdk.Contains("Web", StringComparison.OrdinalIgnoreCase);
            var isExecutable = isWeb || outputType.Equals("Exe", StringComparison.OrdinalIgnoreCase);
            if (!isExecutable)
                warnings.Add("This project does not declare a web or executable output; confirm that it can run as a service.");

            var runtimeVersion = GetDotNetRuntimeVersion(targetFramework);
            var startCommand = $"dotnet {assemblyName}.dll";
            var dockerfile = BuildDotNetDockerfile(path, assemblyName, runtimeVersion);

            yield return new SourceBuildPlan(
                CandidateKey(SourceBuildPlanKind.DotNet, path),
                SourceBuildPlanKind.DotNet,
                $".NET project {projectName}",
                root,
                path,
                warnings.Count == 0 ? SourceBuildPlanConfidence.High : SourceBuildPlanConfidence.Medium,
                signals.ToImmutableList(),
                warnings.ToImmutableList(),
                [
                    $"dotnet restore \"{path}\"",
                    $"dotnet publish \"{path}\" -c Release -o /app/publish"
                ],
                startCommand,
                dockerfile);
        }
    }

    private static IEnumerable<SourceBuildPlan> AnalyzeNodeProjects(RepositorySourceSnapshot snapshot)
    {
        foreach (var path in snapshot.Paths.Where(path =>
                     Path.GetFileName(path).Equals("package.json", StringComparison.OrdinalIgnoreCase)))
        {
            var root = NormalizeRoot(Path.GetDirectoryName(path));
            var warnings = new List<string>();
            var signals = new List<string> { path };

            var scripts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? declaredPackageManager = null;
            if (snapshot.FileContents.TryGetValue(path, out var packageJson))
            {
                try
                {
                    using var document = JsonDocument.Parse(packageJson);
                    if (document.RootElement.TryGetProperty("scripts", out var scriptElement))
                    {
                        foreach (var script in scriptElement.EnumerateObject())
                            scripts[script.Name] = script.Value.GetString() ?? string.Empty;
                    }
                    AddDependencyNames(document.RootElement, "dependencies", dependencies);
                    AddDependencyNames(document.RootElement, "devDependencies", dependencies);
                    if (document.RootElement.TryGetProperty("packageManager", out var packageManagerElement))
                        declaredPackageManager = packageManagerElement.GetString();
                }
                catch (JsonException)
                {
                    warnings.Add("package.json could not be parsed.");
                }
            }
            else
            {
                warnings.Add("package.json could not be loaded.");
            }

            var packageManager = DetectPackageManager(snapshot.Paths, root, declaredPackageManager);
            var installCommand = packageManager switch
            {
                "pnpm" => "pnpm install --frozen-lockfile",
                "yarn" => "yarn install --frozen-lockfile",
                _ => HasAtRoot(snapshot.Paths, root, "package-lock.json") ? "npm ci" : "npm install"
            };
            var buildCommand = scripts.ContainsKey("build") ? $"{packageManager} run build" : null;
            var startCommand = scripts.ContainsKey("start") ? $"{packageManager} run start" : null;

            foreach (var framework in DetectFrameworks(dependencies, snapshot.Paths, root))
                signals.Add(framework);
            if (buildCommand is null)
                warnings.Add("No build script was found.");
            if (startCommand is null)
                warnings.Add("No production start script was found.");

            var commands = new List<string> { installCommand };
            if (buildCommand is not null)
                commands.Add(buildCommand);

            yield return new SourceBuildPlan(
                CandidateKey(SourceBuildPlanKind.Node, path),
                SourceBuildPlanKind.Node,
                $"Node application at {root}",
                root,
                path,
                warnings.Count == 0 ? SourceBuildPlanConfidence.High : SourceBuildPlanConfidence.Medium,
                signals.ToImmutableList(),
                warnings.ToImmutableList(),
                commands.ToImmutableList(),
                startCommand,
                BuildNodeDockerfile(root, packageManager, installCommand, buildCommand, startCommand));
        }
    }

    private static string? FindProperty(XDocument document, string localName) =>
        document.Descendants().FirstOrDefault(element => element.Name.LocalName == localName)?.Value;

    private static string GetDotNetRuntimeVersion(string targetFramework)
    {
        var normalized = targetFramework.StartsWith("net", StringComparison.OrdinalIgnoreCase)
            ? targetFramework[3..]
            : string.Empty;
        var major = new string(normalized.TakeWhile(char.IsDigit).ToArray());
        return string.IsNullOrWhiteSpace(major) ? "10.0" : $"{major}.0";
    }

    private static string BuildDotNetDockerfile(string projectPath, string assemblyName, string runtimeVersion) =>
        $"""
        FROM mcr.microsoft.com/dotnet/sdk:{runtimeVersion} AS build
        WORKDIR /src
        COPY . .
        RUN dotnet publish "{projectPath}" -c Release -o /app/publish

        FROM mcr.microsoft.com/dotnet/aspnet:{runtimeVersion}
        WORKDIR /app
        COPY --from=build /app/publish .
        ENTRYPOINT ["dotnet", "{assemblyName}.dll"]
        """;

    private static string BuildNodeDockerfile(
        string root,
        string packageManager,
        string installCommand,
        string? buildCommand,
        string? startCommand)
    {
        var workDirectory = root == "." ? "/app" : $"/app/{root.Replace('\\', '/')}";
        var builder = new StringBuilder();
        builder.AppendLine("FROM node:22-alpine");
        builder.AppendLine("WORKDIR /app");
        builder.AppendLine("COPY . .");
        builder.AppendLine($"WORKDIR {workDirectory}");
        builder.AppendLine($"RUN {installCommand}");
        if (buildCommand is not null)
            builder.AppendLine($"RUN {buildCommand}");
        if (startCommand is not null)
        {
            var commandParts = startCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            builder.AppendLine($"CMD [{string.Join(", ", commandParts.Select(part => $"\"{part}\""))}]");
        }
        else
        {
            builder.AppendLine($"# Add a production start script for {packageManager} before building this image.");
        }
        return builder.ToString();
    }

    private static void AddDependencyNames(JsonElement root, string propertyName, HashSet<string> dependencies)
    {
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.Object)
            return;
        foreach (var dependency in element.EnumerateObject())
            dependencies.Add(dependency.Name);
    }

    private static string DetectPackageManager(
        IEnumerable<string> paths,
        string root,
        string? declaredPackageManager)
    {
        if (!string.IsNullOrWhiteSpace(declaredPackageManager))
            return declaredPackageManager.Split('@')[0].ToLowerInvariant();
        if (HasAtRoot(paths, root, "pnpm-lock.yaml"))
            return "pnpm";
        if (HasAtRoot(paths, root, "yarn.lock"))
            return "yarn";
        return "npm";
    }

    private static IEnumerable<string> DetectFrameworks(
        HashSet<string> dependencies,
        IEnumerable<string> paths,
        string root)
    {
        if (dependencies.Contains("next") || HasAtRoot(paths, root, "next.config.js") || HasAtRoot(paths, root, "next.config.mjs"))
            yield return "Next.js";
        if (dependencies.Contains("vite") || HasAtRoot(paths, root, "vite.config.ts") || HasAtRoot(paths, root, "vite.config.js"))
            yield return "Vite";
        if (dependencies.Contains("react"))
            yield return "React";
    }

    private static bool HasAtRoot(IEnumerable<string> paths, string root, string fileName)
    {
        var expected = root == "." ? fileName : $"{root.TrimEnd('/')}/{fileName}";
        return paths.Contains(expected, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsAtOrAbove(string? candidateDirectory, string root)
    {
        var candidate = NormalizeRoot(candidateDirectory);
        return root.Equals(candidate, StringComparison.OrdinalIgnoreCase) ||
               root.StartsWith($"{candidate.TrimEnd('/')}/", StringComparison.OrdinalIgnoreCase) ||
               candidate == ".";
    }

    private static bool IsBuildSignal(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.StartsWith("Dockerfile", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("package.json", StringComparison.OrdinalIgnoreCase) ||
               fileName is "package-lock.json" or "pnpm-lock.yaml" or "yarn.lock" ||
               fileName.StartsWith("vite.config.", StringComparison.OrdinalIgnoreCase) ||
               fileName.StartsWith("next.config.", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRoot(string? root) =>
        string.IsNullOrWhiteSpace(root) ? "." : root.Replace('\\', '/').TrimEnd('/');

    private static string CandidateKey(SourceBuildPlanKind kind, string manifestPath)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}:{manifestPath.ToLowerInvariant()}"));
        return Convert.ToHexStringLower(bytes)[..16];
    }
}
