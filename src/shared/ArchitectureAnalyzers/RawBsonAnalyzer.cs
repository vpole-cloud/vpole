using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Pole.ArchitectureAnalyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RawBsonAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "POLE014";
    public const string ExceptionAttribute = "Pole.Architecture.ViolateArchitectureAttribute";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Raw BSON requires an explicit architecture exception",
        "{0}",
        "Architecture",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        customTags: [WellKnownDiagnosticTags.NotConfigurable]);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(
            GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.RegisterCompilationStartAction(start =>
        {
            var options = start.Options.AnalyzerConfigOptionsProvider.GlobalOptions;
            options.TryGetValue("build_property.PoleArchitectureRole", out var role);
            options.TryGetValue("build_property.IsTestProject", out var test);
            if (ArchitectureProjectRoles.ExemptsRawBson(role, bool.TryParse(test, out var value) && value))
                return;

            var aliases = start.Compilation.SyntaxTrees
                .SelectMany(tree => tree.GetRoot(start.CancellationToken).DescendantNodes()
                    .OfType<UsingDirectiveSyntax>())
                .Where(directive => directive.Alias is not null)
                .Select(directive => directive.Alias!.Name.Identifier.ValueText)
                .ToImmutableHashSet(StringComparer.Ordinal);
            start.RegisterSyntaxNodeAction(node => AnalyzeName(node, aliases),
                SyntaxKind.IdentifierName, SyntaxKind.GenericName);
            start.RegisterSyntaxNodeAction(AnalyzeAttribute, SyntaxKind.Attribute);
            start.RegisterOperationAction(AnalyzeOperation,
                OperationKind.Invocation, OperationKind.ObjectCreation, OperationKind.PropertyReference,
                OperationKind.FieldReference, OperationKind.LocalReference, OperationKind.ParameterReference,
                OperationKind.Conversion, OperationKind.ArrayCreation, OperationKind.DelegateCreation,
                OperationKind.AnonymousFunction);
        });
    }

    public static bool IsValidException(string? rule, string? reason) =>
        rule is { Length: 7 } && rule.StartsWith("POLE", StringComparison.Ordinal) &&
        rule.Skip(4).All(char.IsDigit) && !string.IsNullOrWhiteSpace(reason);

    private static void AnalyzeName(SyntaxNodeAnalysisContext context, ImmutableHashSet<string> aliases)
    {
        if (context.Node.Ancestors().Any(node => node is UsingDirectiveSyntax))
            return;

        var name = (SimpleNameSyntax)context.Node;
        if (!IsBsonName(name.Identifier.ValueText) && !aliases.Contains(name.Identifier.ValueText))
            return;

        var symbol = context.SemanticModel.GetSymbolInfo(name, context.CancellationToken).Symbol;
        var type = symbol is IAliasSymbol alias ? alias.Target as ITypeSymbol : symbol as ITypeSymbol;
        if (ForbiddenType(type) is { } forbidden &&
            !Allowed(context.SemanticModel, name, context.CancellationToken))
            context.ReportDiagnostic(Diagnostic.Create(Rule, name.GetLocation(), Message(forbidden)));
    }

    private static void AnalyzeOperation(OperationAnalysisContext context)
    {
        var operation = context.Operation;
        if (operation.SemanticModel is not { } model ||
            ForbiddenOperation(operation) is not { } forbidden ||
            Allowed(model, operation.Syntax, context.CancellationToken))
            return;

        // Only the operation's own explicit type can make its diagnostic redundant. A nested
        // callback may have an exception while its caller still returns unapproved raw BSON.
        var explicitType = operation.Syntax switch
        {
            ObjectCreationExpressionSyntax creation => creation.Type,
            ArrayCreationExpressionSyntax creation => creation.Type.ElementType,
            CastExpressionSyntax conversion => conversion.Type,
            _ => null
        };
        if (explicitType is not null && explicitType.DescendantNodesAndSelf().OfType<SimpleNameSyntax>().Any(name =>
                model.GetSymbolInfo(name, context.CancellationToken).Symbol is ITypeSymbol type &&
                ForbiddenType(type) is not null))
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, operation.Syntax.GetLocation(), Message(forbidden)));
    }

    private static void AnalyzeAttribute(SyntaxNodeAnalysisContext context)
    {
        var syntax = (AttributeSyntax)context.Node;
        if (context.SemanticModel.GetSymbolInfo(syntax, context.CancellationToken).Symbol is not IMethodSymbol ctor ||
            ctor.ContainingType.ToDisplayString() != ExceptionAttribute)
            return;

        var values = new string?[2];
        if (syntax.ArgumentList is { } arguments)
        {
            for (var index = 0; index < arguments.Arguments.Count; index++)
            {
                var argument = arguments.Arguments[index];
                var parameter = argument.NameColon is { } named
                    ? ctor.Parameters.FirstOrDefault(item => item.Name == named.Name.Identifier.ValueText)?.Ordinal
                    : index;
                if (parameter is 0 or 1)
                    values[parameter.Value] = context.SemanticModel.GetConstantValue(
                        argument.Expression, context.CancellationToken).Value as string;
            }
        }

        if (!IsValidException(values[0], values[1]))
            context.ReportDiagnostic(Diagnostic.Create(Rule, syntax.GetLocation(),
                "ViolateArchitecture requires a specific POLE rule ID and a nonblank justification."));
    }

    private static string Message(string type) =>
        $"Raw BSON type '{type}' is forbidden; use typed APIs or ViolateArchitecture(\"{DiagnosticId}\", reason).";

    private static string? ForbiddenOperation(IOperation operation)
    {
        if (ForbiddenType(operation.Type) is { } forbidden)
            return forbidden;

        if (operation is not IAnonymousFunctionOperation function)
            return null;

        return ForbiddenType(function.Symbol.ReturnType) ?? function.Symbol.Parameters
            .Select(parameter => ForbiddenType(parameter.Type)).FirstOrDefault(type => type is not null);
    }

    private static string? ForbiddenType(ITypeSymbol? type)
    {
        if (type is IArrayTypeSymbol array)
            return ForbiddenType(array.ElementType);
        if (type is not INamedTypeSymbol named)
            return null;

        if (IsBsonName(named.MetadataName) && named.ContainingType is null &&
            named.ContainingNamespace?.ToDisplayString() == "MongoDB.Bson")
            return "MongoDB.Bson." + named.MetadataName;

        foreach (var argument in named.TypeArguments)
            if (ForbiddenType(argument) is { } forbidden)
                return forbidden;
        return null;
    }

    private static bool IsBsonName(string name) => name is "BsonDocument" or "BsonArray" or "BsonValue";

    private static bool Allowed(SemanticModel model, SyntaxNode node, CancellationToken ct)
    {
        var symbols = node.AncestorsAndSelf()
            .Where(ancestor => ancestor is MemberDeclarationSyntax or AccessorDeclarationSyntax or
                LocalFunctionStatementSyntax or VariableDeclaratorSyntax or AnonymousFunctionExpressionSyntax)
            .Select(ancestor => ancestor is AnonymousFunctionExpressionSyntax function
                ? model.GetSymbolInfo(function, ct).Symbol
                : model.GetDeclaredSymbol(ancestor, ct))
            .Prepend(model.GetEnclosingSymbol(node.SpanStart, ct));
        foreach (var declaration in symbols)
        {
            for (var symbol = declaration; symbol is not null; symbol = symbol.ContainingSymbol)
            {
                if (symbol.GetAttributes().Any(IsRawBsonException))
                    return true;
            }
        }

        return false;
    }

    private static bool IsRawBsonException(AttributeData attribute) =>
        attribute.AttributeClass?.ToDisplayString() == ExceptionAttribute &&
        attribute.ConstructorArguments.Length == 2 &&
        attribute.ConstructorArguments[0].Value is string rule &&
        attribute.ConstructorArguments[1].Value is string reason &&
        rule == DiagnosticId && IsValidException(rule, reason);
}
