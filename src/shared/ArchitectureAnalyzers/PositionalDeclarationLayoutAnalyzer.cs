// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Pole.ArchitectureAnalyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PositionalDeclarationLayoutAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "POLE007";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Expand positional records",
        "Put each parameter of multi-parameter record '{0}' on its own line",
        "Architecture",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterSyntaxTreeAction(AnalyzeTree);
    }

    private static void AnalyzeTree(SyntaxTreeAnalysisContext context)
    {
        foreach (var parameters in context.Tree
                     .GetRoot(context.CancellationToken)
                     .DescendantNodes()
                     .OfType<ParameterListSyntax>())
        {
            var declaration = parameters.Parent!;
            if (declaration is not RecordDeclarationSyntax)
                continue;
            if (parameters.Parameters.Count < 2)
                continue;

            var previousLine = parameters.OpenParenToken.GetLocation().GetLineSpan().EndLinePosition.Line;
            foreach (var parameter in parameters.Parameters)
            {
                var span = parameter.GetLocation().GetLineSpan();
                if (span.StartLinePosition.Line <= previousLine)
                {
                    var name = declaration.ChildTokens()
                        .FirstOrDefault(token => token.IsKind(SyntaxKind.IdentifierToken))
                        .ValueText;
                    context.ReportDiagnostic(Diagnostic.Create(Rule, parameters.GetLocation(), name));
                    break;
                }

                previousLine = span.EndLinePosition.Line;
            }
        }
    }
}
