// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Pole.ArchitectureAnalyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DomainDesignAnalyzer : DiagnosticAnalyzer
{
    public const string FactoryDiagnosticId = "POLE003";
    public const string CollectionDiagnosticId = "POLE004";
    public const string StateDiagnosticId = "POLE005";

    private static readonly DiagnosticDescriptor FactoryRule = Rule(
        FactoryDiagnosticId,
        "Use the domain factory",
        "Construct '{0}' through '{1}'");

    private static readonly DiagnosticDescriptor CollectionRule = Rule(
        CollectionDiagnosticId,
        "Domain entities own collection mutation",
        "Entity property '{0}' exposes a mutable collection; use an immutable or read-only collection");

    private static readonly DiagnosticDescriptor StateRule = Rule(
        StateDiagnosticId,
        "Use typed domain states",
        "Domain property '{0}' must represent its state with a dedicated type");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [FactoryRule, CollectionRule, StateRule];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterOperationAction(AnalyzeCreation, OperationKind.ObjectCreation);
        context.RegisterSymbolAction(AnalyzeProperty, SymbolKind.Property);
    }

    private static DiagnosticDescriptor Rule(
        string id,
        string title,
        string message) => new(id, title, message, "Architecture", DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static bool IsDomain(INamedTypeSymbol type) =>
        type.ContainingNamespace.ToDisplayString().StartsWith("Pole.Features.", StringComparison.Ordinal) &&
        type.ContainingNamespace.ToDisplayString().Split('.').Contains("Domain");

    // The existing convention identifies factory-owned entities without guessing from names such as Id.
    private static INamedTypeSymbol? FactoryFor(INamedTypeSymbol type) => IsDomain(type)
        ? type.ContainingNamespace
            .GetTypeMembers(type.Name + "Factory")
            .FirstOrDefault(factory =>
                factory.GetMembers()
                    .OfType<IMethodSymbol>()
                    .Any(method => method.IsStatic && method.DeclaredAccessibility == Accessibility.Public &&
                        SymbolEqualityComparer.Default.Equals(method.ReturnType, type)))
        : null;

    private static void AnalyzeCreation(OperationAnalysisContext context)
    {
        var creation = (IObjectCreationOperation)context.Operation;
        if (creation.Type is not INamedTypeSymbol type || FactoryFor(type) is not { } factory)
            return;

        var owner = context.ContainingSymbol.ContainingType;
        if (SymbolEqualityComparer.Default.Equals(owner, factory) || SymbolEqualityComparer.Default.Equals(owner, type))
            return;

        context.ReportDiagnostic(
            Diagnostic.Create(FactoryRule, creation.Syntax.GetLocation(), type.Name, factory.Name));
    }

    private static void AnalyzeProperty(SymbolAnalysisContext context)
    {
        var property = (IPropertySymbol)context.Symbol;
        if (property.IsStatic || !IsDomain(property.ContainingType))
            return;

        var location = property.Locations.FirstOrDefault(location => location.IsInSource);
        if (location is null)
            return;

        if (property.DeclaredAccessibility == Accessibility.Public && FactoryFor(property.ContainingType) is not null &&
            IsMutableCollection(property.Type))
            context.ReportDiagnostic(Diagnostic.Create(CollectionRule, location, property.Name));

        if (property.Name is "State" or "Status" or "Lifecycle" && IsPrimitiveState(property.Type))
            context.ReportDiagnostic(Diagnostic.Create(StateRule, location, property.Name));
    }

    private static bool IsPrimitiveState(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            type = nullable.TypeArguments[0];
        return type.SpecialType is SpecialType.System_String or SpecialType.System_Boolean or SpecialType.System_Byte
            or SpecialType.System_SByte or SpecialType.System_Int16 or SpecialType.System_UInt16
            or SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64
            or SpecialType.System_UInt64;
    }

    private static bool IsMutableCollection(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol)
            return true;
        if (type is not INamedTypeSymbol named)
            return false;
        // Immutable collections also implement mutable interfaces explicitly, so inspect the declared API.
        var definition = named.OriginalDefinition.ToDisplayString();
        return definition is "System.Collections.Generic.List<T>"
            or "System.Collections.Generic.Dictionary<TKey, TValue>" or "System.Collections.Generic.HashSet<T>"
            or "System.Collections.Generic.IList<T>" or "System.Collections.Generic.ICollection<T>"
            or "System.Collections.Generic.IDictionary<TKey, TValue>" or "System.Collections.Generic.ISet<T>";
    }
}
