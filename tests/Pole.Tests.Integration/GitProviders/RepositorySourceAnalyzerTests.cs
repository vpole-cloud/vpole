using System.Collections.Immutable;
using Pole.Features.GitProviders.Application.Analysis;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Domain;
using Pole.ResultType;
using Xunit;

namespace Pole.Tests.Integration.GitProviders;

public sealed class RepositorySourceAnalyzerTests
{
    private static readonly DateTimeOffset AnalysisTime =
        new(2026, 7, 10, 12, 30, 0, TimeSpan.Zero);

    private readonly RepositorySourceAnalyzer _analyzer = new(new FixedTimeProvider(AnalysisTime));

    [Fact]
    public void Analyze_DotNetWebProject_ProducesDeterministicBuildPlan()
    {
        const string project = """
                               <Project Sdk="Microsoft.NET.Sdk.Web">
                                 <PropertyGroup>
                                   <TargetFramework>net9.0</TargetFramework>
                                   <AssemblyName>Sample.Api</AssemblyName>
                                 </PropertyGroup>
                               </Project>
                               """;

        var analysis = AssertSuccess(_analyzer.Analyze(Snapshot(
            ["Sample.slnx", "src/Sample.Api/Sample.Api.csproj", "src/Sample.Api/Program.cs"],
            new Dictionary<string, string>
            {
                ["src/Sample.Api/Sample.Api.csproj"] = project
            })));

        var plan = Assert.Single(analysis.Candidates);
        Assert.Equal(SourceBuildPlanKind.DotNet, plan.Kind);
        Assert.Equal("src/Sample.Api", plan.SourceRoot);
        Assert.Equal(SourceBuildPlanConfidence.High, plan.Confidence);
        Assert.Equal("dotnet Sample.Api.dll", plan.StartCommand);
        Assert.Contains("Target: net9.0", plan.Signals);
        Assert.Contains("FROM mcr.microsoft.com/dotnet/aspnet:9.0", plan.DockerfilePreview);
        Assert.Equal(AnalysisTime.UtcDateTime, analysis.AnalyzedAtUtc);

        var repeated = AssertSuccess(_analyzer.Analyze(Snapshot(
            ["Sample.slnx", "src/Sample.Api/Sample.Api.csproj"],
            new Dictionary<string, string> { ["src/Sample.Api/Sample.Api.csproj"] = project })));
        Assert.Equal(plan.CandidateKey, Assert.Single(repeated.Candidates).CandidateKey);
    }

    [Fact]
    public void Analyze_NodeViteProject_ParsesPackageManagerScriptsAndFrameworks()
    {
        const string packageJson = """
                                   {
                                     "packageManager": "pnpm@10.0.0",
                                     "scripts": { "build": "vite build", "start": "vite preview" },
                                     "dependencies": { "react": "19.0.0" },
                                     "devDependencies": { "vite": "7.0.0" }
                                   }
                                   """;

        var analysis = AssertSuccess(_analyzer.Analyze(Snapshot(
            ["apps/web/package.json", "apps/web/pnpm-lock.yaml", "apps/web/vite.config.ts"],
            new Dictionary<string, string> { ["apps/web/package.json"] = packageJson })));

        var plan = Assert.Single(analysis.Candidates);
        Assert.Equal(SourceBuildPlanKind.Node, plan.Kind);
        Assert.Equal("apps/web", plan.SourceRoot);
        Assert.Equal(["pnpm install --frozen-lockfile", "pnpm run build"], plan.Commands);
        Assert.Equal("pnpm run start", plan.StartCommand);
        Assert.Contains("Vite", plan.Signals);
        Assert.Contains("React", plan.Signals);
        Assert.Empty(plan.Warnings);
        Assert.Contains("CMD [\"pnpm\", \"run\", \"start\"]", plan.DockerfilePreview);
    }

    [Fact]
    public void Analyze_Monorepo_ReturnsEveryCandidateWithoutChoosingOne()
    {
        var analysis = AssertSuccess(_analyzer.Analyze(Snapshot(
            [
                "Dockerfile",
                "src/Api/Api.csproj",
                "src/Worker/Worker.fsproj",
                "ui/package.json",
                "ui/package-lock.json"
            ],
            new Dictionary<string, string>
            {
                ["Dockerfile"] = "FROM alpine:3.22",
                ["src/Api/Api.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />",
                ["src/Worker/Worker.fsproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>",
                ["ui/package.json"] = "{\"scripts\":{\"build\":\"vite build\"}}"
            })));

        Assert.Equal(4, analysis.Candidates.Count);
        Assert.Equal(4, analysis.Candidates.Select(candidate => candidate.CandidateKey).Distinct().Count());
        Assert.Contains(analysis.Candidates, candidate => candidate.Kind == SourceBuildPlanKind.Dockerfile);
        Assert.Equal(2, analysis.Candidates.Count(candidate => candidate.Kind == SourceBuildPlanKind.DotNet));
        Assert.Contains(analysis.Candidates, candidate => candidate.Kind == SourceBuildPlanKind.Node);
    }

    [Fact]
    public void Analyze_LibraryAndUnsupportedRepository_SurfaceActionableResults()
    {
        var library = AssertSuccess(_analyzer.Analyze(Snapshot(
            ["src/Library/Library.csproj"],
            new Dictionary<string, string>
            {
                ["src/Library/Library.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"
            })));
        Assert.Contains("does not declare a web or executable output", Assert.Single(library.Candidates).Warnings[0]);

        var unsupported = AssertSuccess(_analyzer.Analyze(Snapshot(
            ["README.md", "main.py"],
            new Dictionary<string, string>())));
        Assert.Empty(unsupported.Candidates);
    }

    [Fact]
    public void Analyze_TruncatedTree_ReturnsRepositorySizeValidationFailure()
    {
        var result = _analyzer.Analyze(Snapshot(
            ["package.json"],
            new Dictionary<string, string>(),
            isTruncated: true));

        Assert.False(result.TryGetValue(out _, out var failure));
        var validation = Assert.IsType<ValidationFailure>(failure);
        Assert.Contains("too large", validation.ValidationErrors["Repository"][0]);
    }

    private static SourceRepositoryAnalysis AssertSuccess(Result<SourceRepositoryAnalysis> result)
    {
        Assert.True(result.TryGetValue(out var analysis, out var failure), failure?.Message);
        return analysis;
    }

    private static RepositorySourceSnapshot Snapshot(
        IEnumerable<string> paths,
        IReadOnlyDictionary<string, string> contents,
        bool isTruncated = false) =>
        new(
            new ProviderRepository(
                "9001",
                "sample",
                "lsbV/sample",
                "https://github.com/lsbV/sample",
                true,
                "main",
                null),
            "main",
            "abcdef1234567890",
            paths.ToImmutableList(),
            contents.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase),
            isTruncated);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
