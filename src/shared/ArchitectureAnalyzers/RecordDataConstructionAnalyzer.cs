// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Pole.ArchitectureAnalyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RecordDataConstructionAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "POLE009";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Use one record data-construction style",
        "Record '{0}' mixes positional constructor data with stored member '{1}'; put all data in its primary constructor or use an ordinary-property record",
        "Architecture",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterSymbolAction(AnalyzeType, SymbolKind.NamedType);
    }

    private static void AnalyzeType(SymbolAnalysisContext context)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        foreach (var member in MixedDataMembers(type))
        {
            if (RecordDataConstructionLegacy.Members.Contains(MemberKey(member)))
                continue;
            var location = member.Locations.FirstOrDefault(location => location.IsInSource);
            if (location is not null)
                context.ReportDiagnostic(Diagnostic.Create(Rule, location, type.Name, member.Name));
        }
    }

    // Only record data is covered. Primary constructors used for DI classes are unrelated.
    public static IEnumerable<ISymbol> MixedDataMembers(INamedTypeSymbol type)
    {
        if (!type.IsRecord || !type.DeclaringSyntaxReferences.Any(reference =>
                reference.GetSyntax() is RecordDeclarationSyntax { ParameterList: not null }))
            yield break;

        foreach (var property in type.GetMembers().OfType<IPropertySymbol>())
        {
            if (property.IsStatic || property.IsImplicitlyDeclared || property.IsAbstract)
                continue;
            var declarations = property.DeclaringSyntaxReferences
                .Select(reference => reference.GetSyntax())
                .OfType<PropertyDeclarationSyntax>()
                .ToArray();
            if (declarations.Length == 0)
                continue; // Synthesized positional properties point to their parameter declaration.
            var backingField = type.GetMembers()
                .OfType<IFieldSymbol>()
                .Any(field => SymbolEqualityComparer.Default.Equals(field.AssociatedSymbol, property));
            var storedAccessor = declarations.Any(declaration =>
                declaration.AccessorList?.Accessors.Any(accessor =>
                    accessor.Body is null && accessor.ExpressionBody is null) == true);
            if (backingField || storedAccessor)
                yield return property;
        }

        foreach (var field in type.GetMembers()
                     .OfType<IFieldSymbol>()
                     .Where(field => !field.IsStatic && !field.IsImplicitlyDeclared))
            yield return field;

        foreach (var constructor in type.InstanceConstructors.Where(constructor => !constructor.IsImplicitlyDeclared))
        {
            // A record copy constructor preserves record semantics, rather than offering another data shape.
            if (constructor.Parameters.Length == 1 && SymbolEqualityComparer.Default.Equals(
                    constructor.Parameters[0].Type,
                    type))
                continue;
            if (constructor.DeclaringSyntaxReferences.Any(reference =>
                    reference.GetSyntax() is ConstructorDeclarationSyntax))
                yield return constructor;
        }
    }

    public static string MemberKey(ISymbol member)
    {
        var location = member.Locations.First(location => location.IsInSource);
        var path = location.SourceTree!.FilePath.Replace('\\', '/');
        foreach (var prefix in new[] { "src/", "tests/" })
        {
            var index = path.IndexOf("/" + prefix, StringComparison.Ordinal);
            if (index >= 0)
            {
                path = path.Substring(index + 1);
                break;
            }
        }

        var name = member is IMethodSymbol constructor
            ? ".ctor(" +
            string.Join(",", constructor.Parameters.Select(parameter => parameter.Type.ToDisplayString())) + ")"
            : member.Name;
        var kind = member switch
        {
            IPropertySymbol => "extra-property",
            IFieldSymbol => "extra-field",
            _ => "secondary-constructor",
        };
        return path + "|" + member.ContainingType.ToDisplayString() + "|" + name + "|" + kind;
    }
}
