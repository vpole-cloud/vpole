using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Pole.ArchitectureAnalyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TypedMongoFieldFilterAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "POLE013";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Runtime Mongo field operations use typed fields",
        "Use a typed field expression instead of the string field '{0}'",
        "Architecture",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(start =>
        {
            var options = start.Options.AnalyzerConfigOptionsProvider.GlobalOptions;
            options.TryGetValue("build_property.PoleArchitectureRole", out var role);
            options.TryGetValue("build_property.IsTestProject", out var test);
            if (ArchitectureProjectRoles.ExemptsMongoRules(role, bool.TryParse(test, out var value) && value))
                return;
            start.RegisterOperationAction(Analyze, OperationKind.Invocation);
        });
    }

    private static void Analyze(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        if (invocation.TargetMethod.ContainingType.Name is not ("FilterDefinitionBuilder" or "UpdateDefinitionBuilder"
                or "SortDefinitionBuilder" or "IndexKeysDefinitionBuilder") ||
            invocation.TargetMethod.ContainingNamespace.ToDisplayString() != "MongoDB.Driver")
            return;
        foreach (var argument in invocation.Arguments)
        {
            if (argument.Parameter?.Name != "field")
                continue;

            var field = argument.Value;
            while (field is IConversionOperation { IsImplicit: true } conversion)
                field = conversion.Operand;

            if (field.Type?.SpecialType == SpecialType.System_String)
            {
                var name = field.ConstantValue.HasValue
                    ? field.ConstantValue.Value?.ToString() ?? "null"
                    : field.Syntax.ToString();
                context.ReportDiagnostic(Diagnostic.Create(Rule, argument.Syntax.GetLocation(), name));
            }
        }
    }
}
