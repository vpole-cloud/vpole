// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Architecture.FSharp

open System
open System.Collections.Generic
open System.IO
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Symbols
open FSharp.Compiler.Syntax
open FSharp.Compiler.Text

/// A source-located architecture violation, accessible without F# collection types.
type RawBsonViolation =
    { Rule: string
      FilePath: string
      Line: int
      Column: int
      Message: string }

module private Implementation =
    let rule = "POLE014"
    let allowanceType = "Pole.Architecture.ViolateArchitectureAttribute"

    let forbidden =
        set
            [ "MongoDB.Bson.BsonDocument"
              "MongoDB.Bson.BsonArray"
              "MongoDB.Bson.BsonValue" ]

    let fullName (entity: FSharpEntity) =
        entity.TryFullName |> Option.defaultValue entity.DisplayName

    let rec forbiddenTypes (value: FSharpType) =
        seq {
            if value.IsAbbreviation then
                yield! forbiddenTypes value.AbbreviatedType

            if value.HasTypeDefinition && forbidden.Contains(fullName value.TypeDefinition) then
                yield fullName value.TypeDefinition

            // Generic parameter symbols do not have GenericArguments; the accessor throws.
            if not value.IsGenericParameter then
                for argument in value.GenericArguments do
                    yield! forbiddenTypes argument
        }

    let symbolTypes (symbol: FSharpSymbol) =
        seq {
            match symbol with
            | :? FSharpEntity as entity ->
                if forbidden.Contains(fullName entity) then
                    yield fullName entity

                if entity.IsFSharpAbbreviation then
                    yield! forbiddenTypes entity.AbbreviatedType
            | :? FSharpMemberOrFunctionOrValue as memberValue -> yield! forbiddenTypes memberValue.FullType
            | :? FSharpField as field -> yield! forbiddenTypes field.FieldType
            | :? FSharpParameter as parameter -> yield! forbiddenTypes parameter.Type
            | :? FSharpUnionCase as unionCase ->
                for field in unionCase.Fields do
                    yield! forbiddenTypes field.FieldType
            | _ -> ()
        }

    let attributes (symbol: FSharpSymbol) =
        match symbol with
        | :? FSharpEntity as entity -> entity.Attributes :> seq<_>
        | :? FSharpMemberOrFunctionOrValue as memberValue -> memberValue.Attributes :> seq<_>
        | :? FSharpField as field -> field.Attributes :> seq<_>
        | :? FSharpParameter as parameter -> parameter.Attributes :> seq<_>
        | :? FSharpUnionCase as unionCase -> unionCase.Attributes :> seq<_>
        | _ -> Seq.empty

    let isAllowance (attribute: FSharpAttribute) =
        fullName attribute.AttributeType = allowanceType

    let validArguments (attribute: FSharpAttribute) =
        if not (isAllowance attribute) || attribute.ConstructorArguments.Count <> 2 then
            false
        else
            match snd attribute.ConstructorArguments[0], snd attribute.ConstructorArguments[1] with
            | (:? string as id), (:? string as reason) ->
                id.Length = 7
                && id.StartsWith("POLE", StringComparison.Ordinal)
                && (id.Substring(4) |> Seq.forall Char.IsDigit)
                && not (String.IsNullOrWhiteSpace(reason))
            | _ -> false

    let isValidAllowance (attribute: FSharpAttribute) =
        validArguments attribute
        && (snd attribute.ConstructorArguments[0] :?> string) = rule

    let allowed symbol =
        attributes symbol |> Seq.exists isValidAllowance

    let rec declaringTypeAllowed (entity: FSharpEntity option) =
        match entity with
        | Some value -> allowed value || declaringTypeAllowed value.DeclaringEntity
        | None -> false

    let declarationAllowed (symbol: FSharpSymbol) =
        allowed symbol
        || match symbol with
           | :? FSharpEntity as entity -> declaringTypeAllowed entity.DeclaringEntity
           | :? FSharpMemberOrFunctionOrValue as memberValue -> declaringTypeAllowed memberValue.DeclaringEntity
           | :? FSharpField as field -> declaringTypeAllowed field.DeclaringEntity
           | _ -> false

    let positionBeforeOrEqual (left: pos) (right: pos) =
        left.Line < right.Line
        || (left.Line = right.Line && left.Column <= right.Column)

    let contains (scope: range) (location: range) =
        scope.FileName = location.FileName
        && positionBeforeOrEqual scope.Start location.Start
        && positionBeforeOrEqual location.End scope.End

    let allowedScopes (checker: FSharpChecker) options (results: FSharpCheckProjectResults) =
        let scopes = ResizeArray<range>()
        let parsingOptions, _ = checker.GetParsingOptionsFromProjectOptions(options)
        let parsedFiles = Dictionary<string, ParsedInput>()

        let memberScope (memberValue: FSharpMemberOrFunctionOrValue) (body: FSharpExpr) =
            let declaration = memberValue.DeclarationLocation

            let tree =
                match parsedFiles.TryGetValue(declaration.FileName) with
                | true, value -> value
                | _ ->
                    let parsed =
                        checker.ParseFile(
                            declaration.FileName,
                            SourceText.ofString (File.ReadAllText(declaration.FileName)),
                            parsingOptions
                        )
                        |> Async.RunSynchronously

                    parsedFiles.Add(declaration.FileName, parsed.ParseTree)
                    parsed.ParseTree

            let visitor =
                { new SyntaxVisitorBase<range>() with
                    override _.VisitBinding(_, defaultTraverse, binding) =
                        if contains binding.RangeOfHeadPattern declaration then
                            Some binding.RangeOfBindingWithRhs
                        else
                            defaultTraverse binding }

            // Lowered task/async expressions may retain only the builder token as their range.
            // The parsed binding preserves the exact source member, including its signature.
            SyntaxTraversal.Traverse(declaration.Start, tree, visitor)
            |> Option.defaultValue (Range.mkRange body.Range.FileName declaration.Start body.Range.End)

        let rec walk ancestorAllowed declarations =
            for declaration in declarations do
                match declaration with
                | FSharpImplementationFileDeclaration.Entity(entity, nested) ->
                    walk (ancestorAllowed || allowed entity) nested
                | FSharpImplementationFileDeclaration.MemberOrFunctionOrValue(memberValue, _, body) ->
                    if ancestorAllowed || declarationAllowed memberValue then
                        // Include the signature, not only the expression: inferred return types are
                        // reported on the declaration name, before the body's source range.
                        scopes.Add(memberScope memberValue body)
                | FSharpImplementationFileDeclaration.InitAction _ -> ()

        for file in results.AssemblyContents.ImplementationFiles do
            walk false file.Declarations

        scopes

    let allowedTypeScopes (checker: FSharpChecker) options (uses: FSharpSymbolUse array) =
        let types =
            uses
            |> Array.choose (fun usage ->
                match usage.Symbol with
                | :? FSharpEntity as entity when usage.IsFromDefinition && allowed entity -> Some entity
                | _ -> None)
            |> Array.distinctBy (fun entity -> entity.DeclarationLocation)

        let parsingOptions, _ = checker.GetParsingOptionsFromProjectOptions(options)
        let scopes = ResizeArray<range>()

        let rec declarations nodes =
            seq {
                for node in nodes do
                    match node with
                    | SynModuleDecl.Types(definitions, _) ->
                        for definition in definitions do
                            yield definition.Range
                    | SynModuleDecl.NestedModule(_, _, nested, _, location, _) ->
                        yield location
                        yield! declarations nested
                    | SynModuleDecl.NamespaceFragment value -> yield! moduleRanges value
                    | _ -> ()
            }

        and moduleRanges (SynModuleOrNamespace(_, _, _, nodes, _, _, _, location, _)) =
            seq {
                yield location
                yield! declarations nodes
            }

        let rec signatures nodes =
            seq {
                for node in nodes do
                    match node with
                    | SynModuleSigDecl.Types(definitions, _) ->
                        for definition in definitions do
                            yield definition.Range
                    | SynModuleSigDecl.NestedModule(_, _, nested, location, _) ->
                        yield location
                        yield! signatures nested
                    | SynModuleSigDecl.NamespaceFragment value -> yield! signatureModuleRanges value
                    | _ -> ()
            }

        and signatureModuleRanges (SynModuleOrNamespaceSig(_, _, _, nodes, _, _, _, location, _)) =
            seq {
                yield location
                yield! signatures nodes
            }

        for file, entities in types |> Array.groupBy (fun entity -> entity.DeclarationLocation.FileName) do
            let parsed =
                checker.ParseFile(file, SourceText.ofString (File.ReadAllText(file)), parsingOptions)
                |> Async.RunSynchronously

            let ranges =
                match parsed.ParseTree with
                | ParsedInput.ImplFile input -> input.Contents |> Seq.collect moduleRanges |> Seq.toArray
                | ParsedInput.SigFile input -> input.Contents |> Seq.collect signatureModuleRanges |> Seq.toArray

            for entity in entities do
                // Match a semantically validated attribute to its actual syntax declaration.
                // This includes record fields and constructor signatures with no body node.
                let candidates =
                    ranges
                    |> Array.filter (fun location -> contains location entity.DeclarationLocation)

                if candidates.Length = 0 then
                    invalidOp $"Could not locate the F# declaration for architecture exception '{entity.DisplayName}'."

                let scope =
                    candidates
                    |> Array.minBy (fun location ->
                        location.EndLine - location.StartLine, location.EndColumn - location.StartColumn)

                scopes.Add(scope)

        scopes

    let violation projectDirectory (location: range) message =
        { Rule = rule
          FilePath = Path.GetFullPath(location.FileName, projectDirectory)
          Line = location.StartLine
          Column = location.StartColumn + 1
          Message = message }

    let normalizeOptions (projectPath: string) (compilerArgs: string array) =
        let directory = Path.GetDirectoryName(projectPath)

        let isSource (argument: string) =
            not (argument.StartsWith("-", StringComparison.Ordinal))
            && (argument.EndsWith(".fs", StringComparison.OrdinalIgnoreCase)
                || argument.EndsWith(".fsi", StringComparison.OrdinalIgnoreCase))

        let fullPath (path: string) =
            Path.GetFullPath(path.Trim('"'), directory)

        let normalizeReference (argument: string) =
            let prefix =
                [ "-r:"; "--reference:" ]
                |> List.tryFind (fun prefix -> argument.StartsWith(prefix, StringComparison.Ordinal))

            match prefix with
            | Some prefix ->
                let path = fullPath (argument.Substring(prefix.Length))

                if not (File.Exists(path)) then
                    invalidOp $"Missing F# semantic-check reference '{path}'. Build the solution first."

                prefix + path
            | None -> argument

        let sources = compilerArgs |> Array.filter isSource |> Array.map fullPath

        if sources.Length = 0 then
            invalidOp $"No F# source inputs were supplied for '{projectPath}'."

        for source in sources do
            if not (File.Exists(source)) then
                invalidOp $"Missing F# semantic-check source '{source}'."

        let others =
            compilerArgs |> Array.filter (isSource >> not) |> Array.map normalizeReference

        let hasReferences =
            others
            |> Array.exists (fun argument -> argument.StartsWith("-r:") || argument.StartsWith("--reference:"))

        if not hasReferences then
            invalidOp $"No F# references for '{projectPath}'. Resolve references after building the solution."

        sources, others

/// Semantic F# checks using the same compiler service as the repository-pinned SDK.
[<AbstractClass; Sealed>]
type RawBsonCheck private () =
    /// Check a project using complete FscCommandLineArgs. Missing inputs and compiler
    /// errors fail explicitly, rather than returning a misleading empty result.
    static member CheckProject(projectPath: string, compilerArgs: string array) : RawBsonViolation array =
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath)
        ArgumentNullException.ThrowIfNull(compilerArgs)
        let projectPath = Path.GetFullPath(projectPath)
        let directory = Path.GetDirectoryName(projectPath)
        let sources, otherOptions = Implementation.normalizeOptions projectPath compilerArgs
        let checker = FSharpChecker.Create(keepAssemblyContents = true)

        let originalOptions =
            checker.GetProjectOptionsFromCommandLineArgs(projectPath, otherOptions)

        let options =
            { originalOptions with
                SourceFiles = sources
                OtherOptions = otherOptions }

        let results = checker.ParseAndCheckProject(options) |> Async.RunSynchronously

        let errors =
            results.Diagnostics
            |> Array.filter (fun diagnostic -> diagnostic.Severity = FSharpDiagnosticSeverity.Error)

        if results.HasCriticalErrors || errors.Length > 0 then
            let details = errors |> Array.map string |> String.concat Environment.NewLine
            invalidOp $"F# semantic checking failed for '{projectPath}':{Environment.NewLine}{details}"

        let uses = results.GetAllUsesOfAllSymbols()
        let scopes = Implementation.allowedScopes checker options results
        scopes.AddRange(Implementation.allowedTypeScopes checker options uses)
        let violations = ResizeArray<RawBsonViolation>()
        let checkedAttributes = HashSet<range>()

        for usage in uses do
            if usage.IsFromDefinition then
                for attribute in Implementation.attributes usage.Symbol do
                    if
                        Implementation.isAllowance attribute
                        && checkedAttributes.Add(attribute.Range)
                        && not (Implementation.validArguments attribute)
                    then
                        violations.Add(
                            Implementation.violation
                                directory
                                attribute.Range
                                "ViolateArchitecture requires a specific POLE rule ID and a nonblank reason."
                        )

            let inAllowedBody =
                scopes |> Seq.exists (fun scope -> Implementation.contains scope usage.Range)

            let allowedDefinition =
                usage.IsFromDefinition && Implementation.declarationAllowed usage.Symbol

            if not inAllowedBody && not allowedDefinition then
                let bannedTypes =
                    Seq.append
                        (Implementation.symbolTypes usage.Symbol)
                        (usage.GenericArguments
                         |> Seq.collect (fun (_, argument) -> Implementation.forbiddenTypes argument))
                    |> Seq.distinct
                    |> Seq.sort
                    |> Seq.toArray

                if bannedTypes.Length > 0 then
                    let names = String.Join(", ", bannedTypes)

                    violations.Add(
                        Implementation.violation
                            directory
                            usage.Range
                            $"Production code must use typed MongoDB models instead of {names}."
                    )

        violations
        |> Seq.distinctBy (fun item -> item.FilePath, item.Line, item.Column, item.Message)
        |> Seq.sortBy (fun item -> item.FilePath, item.Line, item.Column, item.Message)
        |> Seq.toArray
