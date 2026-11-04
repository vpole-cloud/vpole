using Pole.Features.GitOps.Primitives;
using System.Text.Json;
using MongoDB.Driver;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Contracts;
using Xunit;

namespace Pole.Features.GitOps.Tests;

public sealed class ManifestCompilerTests
{
    private static ManifestCompiler Compiler() => new(new([new TestProvider()]));
    private const string Valid = """
        apiVersion: pole.io/v1alpha1
        kind: Widget
        metadata:
          name: demo
        spec:
          values: {b: second, a: first}
        """;

    [Fact]
    public void Resume_revalidates_typed_specs_without_reapplying_external_text_limits()
    {
        var content = Valid.Replace("{b: second, a: first}", "{a: '" + new string('+', 200_000) + "'}");
        var accepted = Compiler().Compile([new("input.yaml", content)]);
        Assert.True(accepted.Valid);
        var declaration = Assert.Single(accepted.Declarations);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(declaration.Spec.GetRawText(
            )) > SnapshotSchema.MaximumContentBytes);
        var restored = Compiler().Restore(SnapshotSchema.ApiVersion,
            [new(declaration.Address.Kind, declaration.Address.Name, declaration.Spec.GetRawText())]);
        Assert.True(restored.Valid);
        Assert.Equal(declaration.Hash, Assert.Single(restored.Declarations).Hash);
        Assert.False(Compiler().Restore("pole.io/v999", []).Valid);
        Assert.False(Compiler().Restore(SnapshotSchema.ApiVersion, [new("Unknown", "demo", "{}")]).Valid);
    }

    [Fact]
    public void External_input_and_persisted_spec_limits_remain_bounded()
    {
        var overInput = Compiler().Compile([new("input.yaml", new string('a',
            SnapshotSchema.MaximumContentBytes + 1))]);
        Assert.Contains(overInput.Diagnostics, item => item.Code == "SnapshotLimitExceeded");
        var overStored = Compiler().Restore(SnapshotSchema.ApiVersion,
            [new("Widget", "demo", new string('a', SnapshotSchema.MaximumStoredSpecBytes + 1))]);
        Assert.False(overStored.Valid);
    }

    [Theory]
    [InlineData("apiVersion: pole.io/v1alpha1", "apiVersion: pole.io/v2")]
    [InlineData("kind: Widget", "kind: Unknown")]
    [InlineData("name: demo", "name: Demo")]
    [InlineData("name: demo", "name: demo-")]
    [InlineData("metadata:", "tenant: another\nmetadata:")]
    [InlineData("values: {b: second, a: first}", "values: {b: second, a: first}\n  unknown: true")]
    [InlineData("values: {b: second, a: first}", "values: &data {b: second, a: first}")]
    [InlineData("values: {b: second, a: first}", "values: {a: first, a: second}")]
    public void Whole_snapshot_rejects_unknown_fields_versions_kinds_names_aliases_and_duplicate_mapping_keys(
        string from, string to)
    {
        var result = Compiler().Compile([new("valid.yaml", Valid), new("invalid.yaml", Valid.Replace(from, to))]);
        Assert.False(result.Valid);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.File == "invalid.yaml");
    }
    [Fact]
    public void Unordered_files_and_mapping_entries_have_the_same_normalized_hash()
    {
        var compiler = Compiler();
        var first = compiler.Compile([new("one.yaml", Valid)]);
        var second = compiler.Compile([new("two.yaml", Valid.Replace("{b: second, a: first}",
            "{a: first, b: second}"))]);
        Assert.True(first.Valid); Assert.True(second.Valid);
        Assert.Equal(first.Declarations.Single().Hash, second.Declarations.Single().Hash);
    }
    [Fact]
    public void Duplicate_identity_across_documents_and_unsafe_paths_are_rejected()
    {
        Assert.Contains(Compiler().Compile([new("two.yaml", Valid + "\n---\n" + Valid)]).Diagnostics,
            item => item.Code == "DuplicateIdentity");
        Assert.Contains(Compiler().Compile([new("../unsafe.yaml", Valid)]).Diagnostics,
            item => item.Code == "UnsafePath");
    }
    [Fact]
    public void Provider_contribution_needs_no_engine_switch_and_duplicate_registration_fails()
    {
        Assert.Single(Compiler().Compile([new("one.yaml", Valid)]).Declarations);
        Assert.Throws<ArgumentException>(() => new DeclarativeProviderRegistry([new TestProvider(),
            new TestProvider()]));
    }
    [Fact]
    public void Cyclic_operation_graph_is_rejected()
    {
        var one = new CompiledDeclaration(new("Widget", "one"), JsonSerializer.SerializeToElement(new { }), "hash",
            [new(DeclarationPhase.Prepare, [new(new("Widget", "two"), DeclarationPhase.Prepare)])], "one", 1);
        var two = one with
        {
            Address = new("Widget", "two"),
            Steps = [new(DeclarationPhase.Prepare, [new(new("Widget",
            "one"), DeclarationPhase.Prepare)])]
        };
        Assert.False(DeclarationPlan.TryOrder([one, two], out _));
    }
    [Fact]
    public void Provider_owned_failure_types_need_no_platform_error_enum_or_string_control_flow()
    {
        IDeclarationFailure reason = new WidgetRejected(WidgetRejection.DependencyMissing);
        var result = new GitOperationResult("resource", reason);
        Assert.False(result.Applied);
        Assert.Same(reason, result.Failure);
        Assert.Equal("WidgetDependencyMissing", DeclarationFailureDiagnostics.Code(result.Failure, new TestProvider()));
        Assert.Equal("StaleExecution", DeclarationFailureDiagnostics.Code(new StaleGitExecution(), new TestProvider()));
        Assert.Equal("OwnershipConflict", DeclarationFailureDiagnostics.Code(new GitDeclarationOwnershipConflict(),
            new TestProvider()));
        Assert.Null(DeclarationFailureDiagnostics.Code(null, new TestProvider()));
    }

    [Fact]
    public void Common_diagnostics_need_no_provider_and_unknown_owner_errors_still_require_owner_mapping()
    {
        Assert.Equal(nameof(GitLifecycleDependencyRetained),
            DeclarationFailureDiagnostics.CommonCode(new GitLifecycleDependencyRetained()));
        Assert.Equal(nameof(GitLifecycleDependencyRetained),
            DeclarationFailureDiagnostics.Code(new GitLifecycleDependencyRetained(), new TestProvider()));
        Assert.Equal("StaleExecution", DeclarationFailureDiagnostics.CommonCode(new StaleGitExecution()));
        Assert.Equal("OwnershipConflict",
            DeclarationFailureDiagnostics.CommonCode(new GitDeclarationOwnershipConflict()));
        Assert.Null(DeclarationFailureDiagnostics.CommonCode(null));
        Assert.Null(DeclarationFailureDiagnostics.CommonCode(new WidgetRejected(WidgetRejection.DependencyMissing)));
        Assert.Throws<ArgumentException>(() =>
            DeclarationFailureDiagnostics.Code(new UnknownOwnerFailure(), new TestProvider()));
    }

    private sealed record UnknownOwnerFailure : IDeclarationFailure;

    private enum WidgetRejection { DependencyMissing }
    private sealed record WidgetRejected(WidgetRejection Reason) : IDeclarationFailure;
    private sealed record TestSpec(Dictionary<string, string> Values);
    private sealed class TestProvider : IDeclarativeResourceProvider
    {
        public string Kind => "Widget";
        public string DescribeFailure(IDeclarationFailure failure) => failure switch
        {
            WidgetRejected { Reason: WidgetRejection.DependencyMissing } => "WidgetDependencyMissing",
            _ => throw new ArgumentException("Unknown widget failure.", nameof(failure))
        };
        public JsonElement NormalizeAndValidate(JsonElement spec) =>
            DeclarationJson.Write(DeclarationJson.Read<TestSpec>(spec));
        public IReadOnlyList<DeclarationStep> Steps(ResourceAddress address,
            JsonElement spec) => [new(DeclarationPhase.Prepare, [])];
        public Task<string?> ResolveAsync(
            ResolveDeclaredResourceRequest request,
            IClientSessionHandle session,
            CancellationToken ct) => throw new NotSupportedException();
        public Task<GitOperationResult> ApplyAsync(GitOperation operation,
            CancellationToken ct) => throw new NotSupportedException();
    }
}
