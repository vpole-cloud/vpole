using Pole.Features.GitOps.Primitives;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pole.Features.GitOps.Contracts;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Core;

namespace Pole.Features.GitOps.Compiler;

public sealed class ManifestCompiler(DeclarativeProviderRegistry providers)
{
    private const string KindField = "kind";

    private const string ReferencesField = "spec.references";

    public const string ApiVersion = SnapshotSchema.ApiVersion;

    public CompiledSnapshot Compile(IReadOnlyList<ManifestFile> files)
    {
        var declarations = new List<CompiledDeclaration>();
        var diagnostics = new List<DeclarationDiagnostic>();

        if (files.Count > SnapshotSchema.MaximumFiles || files.Sum(file =>
                (long)Encoding.UTF8.GetByteCount(file.Content)) > SnapshotSchema.MaximumContentBytes)
            return new([], [new("", 0, "", "SnapshotLimitExceeded")]);

        foreach (var file in files.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            if (Path.IsPathRooted(file.Path) || file.Path.Split('/', '\\').Any(part => part is ".." or "."))
            {
                diagnostics.Add(new(file.Path, 0, "", "UnsafePath"));
                continue;
            }

            try
            {
                var stream = new YamlStream();
                stream.Load(new StringReader(file.Content));

                if (stream.Documents.Count > SnapshotSchema.MaximumDocumentsPerFile)
                    throw new DeclarationSchemaException("documents");

                for (var index = 0; index < stream.Documents.Count; index++)
                {
                    try
                    {
                        var root = Convert(stream.Documents[index].RootNode, 0, new HashSet<YamlNode>());
                        var envelope = DeclarationJson.Read<Envelope>(JsonSerializer.SerializeToElement(root));
                        DeclarationJson.Require(envelope.ApiVersion == ApiVersion, "apiVersion");
                        DeclarationJson.Require(DeclarationNames.Valid(envelope.Metadata?.Name),
                            DeclarationFieldPaths.MetadataName);
                        var provider = providers.Find(envelope.Kind ?? "") ?? throw new DeclarationSchemaException(
                            KindField);
                        DeclarationJson.Require(
                            envelope.Metadata!.Name!.Length <= provider.MaximumNameLength,
                            DeclarationFieldPaths.MetadataName);
                        var address = new ResourceAddress(provider.Kind, envelope.Metadata!.Name!);
                        var spec = Canonical(provider.NormalizeAndValidate(envelope.Spec));
                        var hash = System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                                    spec.GetRawText())));
                        declarations.Add(new(address, spec, hash, provider.Steps(address, spec), file.Path, index + 1));
                    }
                    catch (DeclarationSchemaException ex)
                    {
                        diagnostics.Add(new(
                                file.Path,
                                index + 1,
                                ex.Field,
                                "InvalidDeclaration"));
                    }
                    catch (ArgumentException)
                    {
                        diagnostics.Add(new(
                                file.Path,
                                index + 1,
                                "yaml",
                                "InvalidYamlStructure"));
                    }
                    catch (JsonException)
                    {
                        diagnostics.Add(new(file.Path, index + 1, DeclarationFieldPaths.Spec, "InvalidSchema"));
                    }
                }
            }
            catch (YamlException) { diagnostics.Add(new(file.Path, 0, "", "MalformedYaml")); }
            catch (DeclarationSchemaException ex)
            {
                diagnostics.Add(new(
                        file.Path,
                        0,
                        ex.Field,
                        "InvalidYamlStructure"));
            }
        }

        return Validate(declarations, diagnostics);
    }

    // Accepted declarations have already passed the transport limits. Revalidate their typed
    // schema and complete graph without reparsing expanded JSON as external YAML input.
    public CompiledSnapshot Restore(string apiVersion, IReadOnlyList<AcceptedManifestDeclaration> accepted)
    {
        if (apiVersion != ApiVersion || accepted.Count > SnapshotSchema.MaximumDeclarations ||
            accepted.Sum(item => (long)Encoding.UTF8.GetByteCount(
                    item.SpecJson)) > SnapshotSchema.MaximumStoredSpecBytes)
            return new([], [new("", 0, "", "UnsupportedStoredSchema")]);

        var declarations = new List<CompiledDeclaration>();
        var diagnostics = new List<DeclarationDiagnostic>();
        foreach (var item in accepted)
        {
            var file = $"{item.Kind}/{item.Name}.json";
            try
            {
                var provider = providers.Find(item.Kind) ?? throw new DeclarationSchemaException(KindField);
                DeclarationJson.Require(
                    DeclarationNames.Valid(item.Name) && item.Name.Length <= provider.MaximumNameLength,
                    DeclarationFieldPaths.MetadataName);
                var address = new ResourceAddress(item.Kind, item.Name);
                var spec = Canonical(provider.NormalizeAndValidate(JsonSerializer.Deserialize<JsonElement>(
                            item.SpecJson)));
                var hash = System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(spec.GetRawText())));
                declarations.Add(new(address, spec, hash, provider.Steps(address, spec), file, 1));
            }
            catch (DeclarationSchemaException ex) { diagnostics.Add(new(file, 1, ex.Field, "InvalidDeclaration")); }
            catch (JsonException) { diagnostics.Add(new(file, 1, DeclarationFieldPaths.Spec, "InvalidSchema")); }
        }

        return Validate(declarations, diagnostics);
    }

    private CompiledSnapshot Validate(List<CompiledDeclaration> declarations, List<DeclarationDiagnostic> diagnostics)
    {
        // Leave ample room below MongoDB's document limit for resource bindings and execution metadata.
        if (declarations.Sum(item =>
                (long)Encoding.UTF8.GetByteCount(item.Spec.GetRawText())) > SnapshotSchema.MaximumStoredSpecBytes)
            diagnostics.Add(new("", 0, "", "SnapshotLimitExceeded"));

        foreach (var duplicate in declarations.GroupBy(item => item.Address).Where(group => group.Count() > 1))
            foreach (var item in duplicate) diagnostics.Add(new(
                        item.File,
                        item.Document,
                        DeclarationFieldPaths.MetadataName,
                        "DuplicateIdentity"));

        if (diagnostics.Count == 0)
        {
            var facts = declarations.ToDictionary(
                item => item.Address,
                item => providers.Find(item.Address.Kind)!.Facts(item.Spec));
            var grants = facts.Values.SelectMany(item => item.AccessGrants).ToArray();
            var relations = declarations.SelectMany(item =>
                facts[item.Address].ExclusiveRelations.Select(relation => (Declaration: item, Relation: relation)));
            foreach (var duplicate in relations.GroupBy(item => item.Relation).Where(group => group.Count() > 1))
                foreach (var item in duplicate) diagnostics.Add(new(
                            item.Declaration.File,
                            item.Declaration.Document,
                            ReferencesField,
                            "DuplicateRelationship"));
            foreach (var item in declarations)
            {
                var own = facts[item.Address];
                var scopes = new List<ResourceAddress>();

                if (own.Scope is not null)
                    scopes.Add(own.Scope);

                foreach (var reference in own.References)
                {
                    var matchingGrants = grants.Where(grant =>
                        grant.Consumer == item.Address && grant.Target == reference.Target).ToArray();

                    if (reference.RequiresAccess && reference.RequiredKey is not null && matchingGrants.Length > 0 &&
                        !matchingGrants.Any(grant => grant.AllKeys || grant.Keys.Contains(reference.RequiredKey)))
                        diagnostics.Add(new(item.File, item.Document, reference.Field, "ContradictoryAccessKeys"));

                    if (!facts.TryGetValue(reference.Target, out var target))
                        continue; // External prerequisites block during application.

                    if (target.Scope is not null)
                        scopes.Add(target.Scope);

                    if (reference.RequiredKey is not null && !target.Keys.Contains(reference.RequiredKey))
                        diagnostics.Add(new(item.File, item.Document, reference.Field, "ContradictoryReferenceKey"));
                }

                if (own.ReferencesShareScope && scopes.Distinct().Count() > 1)
                    diagnostics.Add(new(item.File, item.Document, ReferencesField, "ContradictoryReferenceScope"));
            }
        }

        if (declarations.Count > SnapshotSchema.MaximumDeclarations)
            diagnostics.Add(new("", 0, "", "SnapshotLimitExceeded"));
        // Validate the complete operation graph before any source/inventory mutation.
        if (diagnostics.Count == 0 && !DeclarationPlan.TryOrder(declarations, out _))
            diagnostics.Add(new("", 0, "references", "DependencyCycle"));

        return new(declarations.OrderBy(item => item.Address.Kind, StringComparer.Ordinal)
                .ThenBy(item => item.Address.Name, StringComparer.Ordinal).ToArray(), diagnostics);
    }

    public static JsonElement Canonical(JsonElement value) => JsonSerializer.SerializeToElement(Sort(value));

    private static JsonNode? Sort(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => new JsonObject(value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => KeyValuePair.Create(p.Name, Sort(p.Value)))),
        JsonValueKind.Array => new JsonArray(value.EnumerateArray().Select(Sort).ToArray()),
        _ => JsonNode.Parse(value.GetRawText())
    };

    private static JsonNode? Convert(YamlNode node, int depth, HashSet<YamlNode> ancestors)
    {
        if (depth > SnapshotSchema.MaximumYamlDepth || !ancestors.Add(node) || !node.Anchor.IsEmpty
            || !node.Tag.IsEmpty)
            throw new DeclarationSchemaException("yaml");

        try
        {
            return node switch
            {
                YamlMappingNode map => new JsonObject(map.Children.Select(pair => KeyValuePair.Create(
                            pair.Key is YamlScalarNode { Value: not null } key && key.Tag.IsEmpty
                            && key.Anchor.IsEmpty ? key.Value : throw new DeclarationSchemaException("key"),
                            Convert(pair.Value, depth + 1, ancestors)))),
                YamlSequenceNode sequence => new JsonArray(sequence.Children.Select(child => Convert(
                            child,
                            depth + 1,
                            ancestors)).ToArray()),
                YamlScalarNode scalar => Scalar(scalar),
                _ => throw new DeclarationSchemaException("yaml")
            };
        }
        finally { ancestors.Remove(node); }
    }

    private static JsonNode? Scalar(YamlScalarNode scalar)
    {
        var value = scalar.Value;

        if (scalar.Style is ScalarStyle.Plain)
        {
            if (value is null or "null" or "~")
                return null;

            if (value is "true" or "false")
                return JsonValue.Create(value == "true");

            if (long.TryParse(value, System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture, out var number))
                return JsonValue.Create(number);
        }

        return JsonValue.Create(value ?? "");
    }

    private sealed record Metadata(string? Name);

    private sealed record Envelope(
        string? ApiVersion,
        string? Kind,
        Metadata? Metadata,
        JsonElement Spec);
}
