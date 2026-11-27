// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Pole.Tests.Architecture;

public sealed class ContractOperationParameterTests
{
    [Fact]
    public void Cross_feature_contract_operations_have_at_most_three_parameters()
    {
        var violations = ArchitectureSources.ProductionCSharp()
            .Where(source => source.RelativePath.Contains(".Contracts/", StringComparison.Ordinal))
            .SelectMany(source =>
                Violations(source.Root)
                    .Select(node => ArchitectureSources.Describe(
                        source,
                        node,
                        "Contract operations accept at most three parameters, including session and cancellation. Use a provider-owned request model.")))
            .ToArray();
        Assert.True(violations.Length == 0, string.Join(Environment.NewLine, violations));
    }

    [Theory]
    [InlineData("public delegate void Read(Request request, Session session, CancellationToken ct);", 0)]
    [InlineData("public delegate void Read(string tenant, string id, Session session, CancellationToken ct);", 1)]
    [InlineData(
        "public interface IReader { void Read(string tenant, string id, Session? session = null, CancellationToken ct = default); }",
        1)]
    [InlineData("public interface IReader { void Read(Request request, Session session, CancellationToken ct); }", 0)]
    [InlineData("public sealed record Dto(string Tenant, string Group, string Scope, string Id);", 0)]
    [InlineData(
        "public sealed class Model(string tenant, string group, string scope, string id) { public static Model Create(string tenant, string group, string scope, string id) => new(tenant, group, scope, id); }",
        0)]
    public void Parameter_rule_counts_optional_and_technical_parameters_but_excludes_model_construction(
        string source,
        int expected)
    {
        var root = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview)).GetRoot();
        Assert.Equal(expected, Violations(root).Count());
    }

    private static IEnumerable<SyntaxNode> Violations(SyntaxNode root) => root.DescendantNodes()
        .Where(node => node is DelegateDeclarationSyntax { ParameterList.Parameters.Count: > 3 } ||
            node is MethodDeclarationSyntax
            {
                ParameterList.Parameters.Count: > 3,
                Parent: InterfaceDeclarationSyntax,
            });
}
