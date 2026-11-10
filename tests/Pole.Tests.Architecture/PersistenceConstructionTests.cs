using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Pole.Tests.Architecture;

public sealed class PersistenceConstructionTests
{
    [Fact]
    public void Handwritten_types_do_not_split_their_implementation_with_partial()
    {
        var violations = ArchitectureSources.ProductionCSharp()
            .Where(source => !source.RelativePath.Contains(".Tests/", StringComparison.Ordinal))
            .SelectMany(source =>
                source.Root
                    .DescendantNodes()
                    .OfType<TypeDeclarationSyntax>()
                    .Where(type => type.Modifiers.Any(token => token.RawKind == (int)SyntaxKind.PartialKeyword))
                    .Where(type => !type.DescendantNodes().OfType<MethodDeclarationSyntax>().Any(NeedsGenerator))
                    .Select(type => ArchitectureSources.Describe(
                        source,
                        type,
                        "Keep the implementation together or extract a cohesive collaborator.")));
        Assert.Empty(violations);
    }

    [Fact]
    public void Runtime_services_receive_collections_instead_of_selecting_them_inside_instances()
    {
        var violations = ArchitectureSources.ProductionCSharp()
            .Where(source => source.RelativePath.Contains(".Application/", StringComparison.Ordinal) ||
                source.RelativePath.StartsWith("src/shared/Database/Observation/", StringComparison.Ordinal))
            .SelectMany(source =>
                source.Root
                    .DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
                    .Where(call => call.Expression is MemberAccessExpressionSyntax member &&
                        member.Name.Identifier.ValueText == "GetCollection")
                    .Where(call => !IsStaticComposition(call))
                    .Select(call => ArchitectureSources.Describe(
                        source,
                        call,
                        "Inject the typed collection; select its database/name in composition.")));
        Assert.Empty(violations);
    }

    private static bool NeedsGenerator(MethodDeclarationSyntax method) =>
        method.Modifiers.Any(token => token.RawKind == (int)SyntaxKind.PartialKeyword) && method.AttributeLists
            .SelectMany(list => list.Attributes)
            .Any(attribute => attribute.Name.ToString().Split('.').Last() is "GeneratedRegex"
                or "GeneratedRegexAttribute" or "LoggerMessage" or "LoggerMessageAttribute");

    private static bool IsStaticComposition(InvocationExpressionSyntax call) =>
        call.Ancestors()
            .OfType<TypeDeclarationSyntax>()
            .Any(type => type.Modifiers.Any(token => token.RawKind == (int)SyntaxKind.StaticKeyword)) || call
            .Ancestors()
            .OfType<MethodDeclarationSyntax>()
            .Any(method => method.Modifiers.Any(token => token.RawKind == (int)SyntaxKind.StaticKeyword));
}
