using Pole.Testing;
using Xunit;

namespace Pole.Testing.Tests;

[Trait(TestTraits.Layer, TestTraits.Layers.Fast)]
[Trait(TestTraits.Concern, "CI")]
[Trait(TestTraits.Feature, "TestCoverage")]
public sealed class TestCoverageCiContractTests
{
    [Fact]
    public void Github_workflow_publishes_backend_and_frontend_coverage_without_a_threshold()
    {
        var workflow = ReadRepositoryFile(".github", "workflows", "kubernetes-lab.yml");

        Assert.Contains("--profile \"${{ github.event_name == 'workflow_dispatch' && 'full' || 'pr' }}\"", workflow,
            StringComparison.Ordinal);
        // Collector and bounded execution arguments are exercised by test_quality_gate.py.
        Assert.Contains(
            "run: python3 scripts/quality_gate.py backend --results-directory .artifacts/coverage/backend/raw",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains("dotnet tool run reportgenerator", workflow, StringComparison.Ordinal);
        Assert.Contains("Html;Cobertura", workflow, StringComparison.Ordinal);
        Assert.Contains("MarkdownAssembliesSummary", workflow, StringComparison.Ordinal);
        Assert.Contains("-filefilters:-**/obj/**;-**/*.g.cs;-**/*.generated.cs", workflow,
            StringComparison.Ordinal);
        Assert.Contains("GITHUB_STEP_SUMMARY", workflow, StringComparison.Ordinal);
        Assert.Contains("actions/upload-artifact@v4", workflow, StringComparison.Ordinal);
        Assert.Contains("name: backend-coverage", workflow, StringComparison.Ordinal);
        Assert.Contains("name: frontend-coverage", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("threshold", workflow, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Frontend_coverage_includes_unloaded_source_files_without_a_threshold()
    {
        var package = ReadRepositoryFile("src", "api", "frontend", "package.json");
        var viteConfig = ReadRepositoryFile("src", "api", "frontend", "vite.config.ts");

        Assert.Contains("\"test:coverage\": \"vitest run --coverage\"", package, StringComparison.Ordinal);
        Assert.Contains("\"@vitest/coverage-v8\"", package, StringComparison.Ordinal);
        Assert.Contains("include: ['src/**/*.{ts,tsx}']", viteConfig, StringComparison.Ordinal);
        Assert.Contains("reporter: ['text-summary', 'html', 'cobertura', 'json-summary']", viteConfig,
            StringComparison.Ordinal);
        Assert.DoesNotContain("threshold", viteConfig, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Report_generator_is_pinned_as_a_repository_tool()
    {
        var manifest = ReadRepositoryFile(".config", "dotnet-tools.json");

        Assert.Contains("dotnet-reportgenerator-globaltool", manifest, StringComparison.Ordinal);
        Assert.Contains("reportgenerator", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void Documentation_keeps_backend_coverage_inputs_fresh_and_scoped()
    {
        var documentation = ReadRepositoryFile("docs", "testing.md");

        Assert.Contains(
            "rm -rf -- .artifacts/coverage/backend/raw .artifacts/coverage/backend/report",
            documentation,
            StringComparison.Ordinal);
        Assert.Contains("--results-directory .artifacts/coverage/backend/raw", documentation,
            StringComparison.Ordinal);
        Assert.Contains("-reports:.artifacts/coverage/backend/raw/**/*.cobertura.xml", documentation,
            StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] segments) =>
        File.ReadAllText(Path.Combine([RepositoryPaths.FindRoot(), .. segments]));
}
