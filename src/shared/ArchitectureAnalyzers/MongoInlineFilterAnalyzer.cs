using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Pole.ArchitectureAnalyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MongoInlineFilterAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "POLE008";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Declare Mongo filters in named helpers",
        "Mongo filter must be defined in a named *Filters helper",
        "Architecture",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(Analyze, OperationKind.Argument);
    }

    private static void Analyze(OperationAnalysisContext context)
    {
        var argument = (IArgumentOperation)context.Operation;
        if (argument.Parameter?.Name != "filter" ||
            argument.Parameter.ContainingSymbol.ContainingNamespace.ToDisplayString() != "MongoDB.Driver")
            return;
        if (MongoApplicationPaths.Classify(argument.Syntax.SyntaxTree.FilePath) ==
            MongoApplicationPath.OutsideApplication)
            return;
        if (context.ContainingSymbol.ContainingType?.Name.EndsWith("Filters", StringComparison.Ordinal) == true)
            return;
        var value = argument.Value;
        while (value is IConversionOperation or IDelegateCreationOperation)
            value = value is IConversionOperation conversion
                ? conversion.Operand
                : ((IDelegateCreationOperation)value).Target;
        if (value is IAnonymousFunctionOperation)
            context.ReportDiagnostic(Diagnostic.Create(Rule, argument.Syntax.GetLocation()));
    }
}
