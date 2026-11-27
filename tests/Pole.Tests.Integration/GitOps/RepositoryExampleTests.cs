// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using Pole.Features.GitOps.Compiler;
using Pole.Features.GitOps.Primitives;
using Pole.Testing;
using Pole.Tests.Integration.Infrastructure;
using Xunit;
using MongoReplicaSetFixture = Pole.Tests.Integration.Infrastructure.MongoReplicaSetFixture;

namespace Pole.Tests.Integration.GitOps;

[Collection(IntegrationTestCollection.Tenant2)]
public sealed class RepositoryExampleTests(
    MongoReplicaSetFixture fixture
) : DeclarativePipelineTestBase(fixture)
{
    [Theory]
    [InlineData("minimal", 2, 3)]
    [InlineData("full", 15, 19)]
    [InlineData("backups", 4, 5)]
    [InlineData("lifecycle/before", 3, 4)]
    [InlineData("lifecycle/after-rename", 3, 4)]
    [InlineData("lifecycle/after-removal", 1, 1)]
    [InlineData("lifecycle/empty", 0, 0)]
    public void Canonical_repository_examples_compile_with_current_owner_schemas(
        string example,
        int declarations,
        int operations)
    {
        var snapshot = CompileExample(example);
        Assert.True(
            snapshot.Valid,
            string.Join(
                ", ",
                snapshot.Diagnostics.Select(item => $"{item.File}:{item.Document}:{item.Field}:{item.Code}")));
        Assert.Equal(declarations, snapshot.Declarations.Count);
        Assert.True(DeclarationPlan.TryOrder(snapshot.Declarations, out var ordered));
        Assert.Equal(operations, ordered.Count);
    }

    [Fact]
    public void Canonical_example_loader_supports_a_repository_root_snapshot()
    {
        var files = RepositoryExampleFiles.Read("src/features/GitOps/examples/minimal/manifests", "");
        Assert.All(files, file => Assert.False(Path.IsPathRooted(file.Path)));
        var snapshot = Services.GetRequiredService<ManifestCompiler>()
            .Compile(files.Select(file => new ManifestFile(file.Path, file.Content)).ToArray());
        Assert.True(snapshot.Valid);
        Assert.Equal(2, snapshot.Declarations.Count);
    }

    [Fact]
    public void Full_repository_example_covers_all_kinds_and_preserves_dependency_phase_order()
    {
        var snapshot = CompileExample("full");
        Assert.True(snapshot.Valid, string.Join(", ", snapshot.Diagnostics.Select(item => item.Code)));
        string[] kinds =
        [
            "DomainZone", "FunctionApp", "HostnameBinding", "IsolationScope", "PostgresCluster",
            "RedisService", "ResourceConnection", "ResourceGroup", "Secret", "SecretBinding",
            "SourceBuildConfiguration", "SourceRepository", "WebService",
        ];
        Assert.Equal(
            kinds,
            snapshot.Declarations.Select(item => item.Address.Kind).Distinct().Order(StringComparer.Ordinal));
        Assert.True(DeclarationPlan.TryOrder(snapshot.Declarations, out var ordered));

        int Step(
            string kind,
            string name,
            DeclarationPhase phase) => ordered.Select((operation, index) => (Operation: operation, Index: index))
            .Single(entry => entry.Operation.Declaration.Address.Kind == kind &&
                entry.Operation.Declaration.Address.Name == name && entry.Operation.Phase == phase)
            .Index;

        Assert.True(
            Step("ResourceGroup", "gitops-mixed-group", DeclarationPhase.Prepare) < Step(
                "IsolationScope",
                "gitops-mixed-scope",
                DeclarationPhase.Prepare));
        Assert.True(
            Step("PostgresCluster", "mixed-postgres", DeclarationPhase.Prepare) < Step(
                "SecretBinding",
                "mixed-bootstrap-grant",
                DeclarationPhase.Bind));
        Assert.True(
            Step("SecretBinding", "mixed-bootstrap-grant", DeclarationPhase.Bind) < Step(
                "PostgresCluster",
                "mixed-postgres",
                DeclarationPhase.Configure));
        Assert.True(
            Step("FunctionApp", "mixed-function", DeclarationPhase.Prepare) < Step(
                "SecretBinding",
                "mixed-function-binding",
                DeclarationPhase.Bind));
        Assert.True(
            Step("SecretBinding", "mixed-function-binding", DeclarationPhase.Bind) < Step(
                "FunctionApp",
                "mixed-function",
                DeclarationPhase.Configure));
        Assert.True(
            Step("WebService", "mixed-web", DeclarationPhase.Prepare) < Step(
                "SourceBuildConfiguration",
                "mixed-build",
                DeclarationPhase.Configure));
        Assert.True(
            Step("SourceRepository", "mixed-repository", DeclarationPhase.Prepare) < Step(
                "SourceBuildConfiguration",
                "mixed-build",
                DeclarationPhase.Configure));
        Assert.True(
            Step("SourceBuildConfiguration", "mixed-build", DeclarationPhase.Configure) < Step(
                "WebService",
                "mixed-web",
                DeclarationPhase.Configure));
        Assert.True(
            Step("RedisService", "mixed-redis", DeclarationPhase.Configure) < Step(
                "ResourceConnection",
                "mixed-cache",
                DeclarationPhase.Bind));
        Assert.True(
            Step("ResourceConnection", "mixed-cache", DeclarationPhase.Bind) < Step(
                "WebService",
                "mixed-web",
                DeclarationPhase.Configure));
        Assert.True(
            Step("WebService", "mixed-web", DeclarationPhase.Configure) < Step(
                "HostnameBinding",
                "mixed-hostname",
                DeclarationPhase.Bind));
    }

    [Fact]
    public void Canonical_secret_example_cannot_accept_values_in_the_manifest()
    {
        var files = ReadExample("full");
        var secret = files.Single(item => item.Path == "manifests/secret.yaml");
        var rejected = Services.GetRequiredService<ManifestCompiler>()
            .Compile(
                files.Select(item =>
                        item == secret
                            ? item with { Content = item.Content + "  values: {password: forbidden}\n" }
                            : item)
                    .ToArray());
        Assert.False(rejected.Valid);
        Assert.Contains(rejected.Diagnostics, item => item.File == secret.Path);
    }
}
