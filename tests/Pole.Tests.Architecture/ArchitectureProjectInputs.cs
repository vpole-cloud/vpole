using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Pole.ArchitectureAnalyzers;

namespace Pole.Tests.Architecture;

internal sealed record ArchitectureProject(
    string Path,
    string Role,
    bool IsTest,
    string AssemblyName,
    string Configuration,
    string[] References,
    string[] CompilerArguments
)
{
    public bool Exempt => ArchitectureProjectRoles.ExemptsMongoRules(Role, IsTest);
}

internal sealed record ArchitectureProjectSnapshot(
    ArchitectureProject[] Projects,
    TimeSpan Elapsed
);

internal static class ArchitectureProjectInputs
{
    internal static ArchitectureProjectSnapshot Load(
        IEnumerable<string> projectPaths,
        string? configuration = null,
        bool includeCompilerInputs = true)
    {
        var paths = projectPaths.Select(Path.GetFullPath).Order(StringComparer.Ordinal).ToArray();
        if (paths.Length == 0)
            throw new InvalidOperationException("No architecture projects discovered.");
        var list = Path.Combine(Path.GetTempPath(), "pole-project-inputs-" + Guid.NewGuid().ToString("N") + ".txt");
        var stopwatch = Stopwatch.StartNew();
        try
        {
            File.WriteAllLines(list, paths);
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = ArchitectureSources.RepositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.Environment["MSBUILDUSESERVER"] = "0";
            configuration ??= typeof(ArchitectureProjectInputs).Assembly
                .GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
            foreach (var argument in new[]
                     {
                         "msbuild",
                         Path.Combine(
                             ArchitectureSources.RepositoryRoot,
                             "tests/Pole.Tests.Architecture/ProjectInputs.proj"),
                         "-getTargetResult:CollectArchitectureInputs", "-nologo", "-maxcpucount:1", "-nodeReuse:false",
                         "-property:PoleProjectListFile=" + list, "-property:Configuration=" + configuration,
                         "-property:PoleCollectCompilerInputs=" + includeCompilerInputs.ToString().ToLowerInvariant(),
                     })
                start.ArgumentList.Add(argument);

            using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start MSBuild.");
            var output = child.StandardOutput.ReadToEndAsync();
            var error = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(180_000))
            {
                child.Kill(entireProcessTree: true);
                throw new InvalidOperationException("Timed out collecting architecture project inputs.");
            }

            var stdout = output.GetAwaiter().GetResult();
            var stderr = error.GetAwaiter().GetResult();
            if (child.ExitCode != 0)
                throw new InvalidOperationException(
                    $"Could not collect architecture inputs:{Environment.NewLine}{stdout}{stderr}");

            using var json = JsonDocument.Parse(stdout);
            var items = json.RootElement
                .GetProperty("TargetResults")
                .GetProperty("CollectArchitectureInputs")
                .GetProperty("Items")
                .EnumerateArray()
                .ToArray();
            var projects = items.Where(item => item.GetProperty("Kind").GetString() == "Project")
                .Select(item =>
                {
                    var path = item.GetProperty("ProjectPath").GetString()!;

                    string[] Values(string kind) => items.Where(candidate =>
                            candidate.GetProperty("Kind").GetString() == kind &&
                            candidate.GetProperty("ProjectPath").GetString() == path)
                        .Select(candidate => candidate.GetProperty("Identity").GetString()!)
                        .ToArray();

                    return new ArchitectureProject(
                        path,
                        item.GetProperty("Role").GetString()!,
                        bool.TryParse(item.GetProperty("IsTestProject").GetString(), out var value) && value,
                        item.GetProperty("AssemblyName").GetString()!,
                        item.GetProperty("Configuration").GetString()!,
                        Values("ProjectReference"),
                        Values("CompilerArgument"));
                })
                .OrderBy(project => project.Path, StringComparer.Ordinal)
                .ToArray();
            if (!paths.SequenceEqual(projects.Select(project => project.Path), StringComparer.Ordinal))
                throw new InvalidOperationException(
                    "MSBuild architecture inventory differs from the discovered projects.");
            return new ArchitectureProjectSnapshot(projects, stopwatch.Elapsed);
        }
        finally
        {
            File.Delete(list);
        }
    }
}
