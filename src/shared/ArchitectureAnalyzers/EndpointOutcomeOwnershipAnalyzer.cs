// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Pole.ArchitectureAnalyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class EndpointOutcomeOwnershipAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "POLE002";

    public const string SingleHandlerDiagnosticId = "POLE006";

    private static readonly DiagnosticDescriptor SingleHandlerRule = new(
        SingleHandlerDiagnosticId,
        "One handler pair per endpoint file",
        "Endpoint file containing '{0}' must contain only one mediator request-handler pair",
        "Architecture",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Endpoint owns its outcome",
        "Handler '{0}' must declare outcome '{1}' in the same endpoint file",
        "Architecture",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule, SingleHandlerRule];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterSymbolAction(AnalyzeHandler, SymbolKind.NamedType);
        context.RegisterSemanticModelAction(AnalyzeEndpointFile);
    }

    private static void AnalyzeHandler(SymbolAnalysisContext context)
    {
        var handler = (INamedTypeSymbol)context.Symbol;
        if (handler.TypeKind != TypeKind.Class || handler.IsAbstract)
            return;

        foreach (var location in handler.Locations.Where(location => location.IsInSource &&
                     location.SourceTree!.FilePath.Replace('\\', '/').Contains(".Application/Endpoints/")))
        {
            var contracts = handler.AllInterfaces.Where(IsOutcomeHandler).ToArray();
            if (contracts.Length == 0)
                continue;

            foreach (var contract in contracts)
            {
                var outcome = contract.TypeArguments[1];
                if (outcome.TypeKind == TypeKind.Error || outcome.Locations.Any(outcomeLocation =>
                        outcomeLocation.IsInSource && outcomeLocation.SourceTree == location.SourceTree))
                    continue;

                context.ReportDiagnostic(Diagnostic.Create(Rule, location, handler.Name, outcome.Name));
            }
        }
    }

    private static void AnalyzeEndpointFile(SemanticModelAnalysisContext context)
    {
        var tree = context.SemanticModel.SyntaxTree;
        if (!tree.FilePath.Replace('\\', '/').Contains(".Application/Endpoints/"))
            return;

        var handlers = tree.GetRoot(context.CancellationToken)
            .DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Select(declaration => context.SemanticModel.GetDeclaredSymbol(declaration, context.CancellationToken))
            .OfType<INamedTypeSymbol>()
            .Where(type => type.TypeKind == TypeKind.Class && !type.IsAbstract &&
                type.AllInterfaces.Any(IsOutcomeHandler))
            .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default)
            .ToArray();
        if (handlers.Sum(handler => handler.AllInterfaces.Count(IsOutcomeHandler)) < 2)
            return;

        foreach (var handler in handlers)
            context.ReportDiagnostic(
                Diagnostic.Create(
                    SingleHandlerRule,
                    handler.Locations.First(location => location.SourceTree == tree),
                    handler.Name));
    }

    private static bool IsOutcomeHandler(INamedTypeSymbol contract) => contract.OriginalDefinition.ToDisplayString() ==
        "Pole.Mediator.IMediatorOutcomeHandler<TRequest, TOutcome>";
}
