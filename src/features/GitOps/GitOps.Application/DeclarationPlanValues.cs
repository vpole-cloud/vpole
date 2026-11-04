using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;

namespace Pole.Features.GitOps.Application;

internal static class DeclarationPlanValues
{
    public static CurrentDeclaration? Current(ResourceDeclarationPlan plan) => plan switch
    {
        UpdateDeclarationPlan update => update.Current,
        NoOpDeclarationPlan noop => noop.Current,
        AdoptionDeclarationPlan adoption => adoption.Current,
        ReplacementDeclarationPlan replacement => replacement.Current,
        RemovalDeclarationPlan removal => removal.Current,
        CreateDeclarationPlan or BlockedDeclarationPlan => null
    };

    public static DeclarationProjection? Desired(ResourceDeclarationPlan plan) => plan switch
    {
        CreateDeclarationPlan create => create.Desired,
        UpdateDeclarationPlan update => update.Desired,
        NoOpDeclarationPlan noop => noop.Desired,
        AdoptionDeclarationPlan adoption => adoption.Desired,
        ReplacementDeclarationPlan replacement => replacement.Desired,
        RemovalDeclarationPlan or BlockedDeclarationPlan => null
    };

    public static GitPlannedAction Action(ResourceDeclarationPlan plan) => plan switch
    {
        CreateDeclarationPlan => GitPlannedAction.Create,
        UpdateDeclarationPlan => GitPlannedAction.Update,
        NoOpDeclarationPlan => GitPlannedAction.NoOp,
        BlockedDeclarationPlan => GitPlannedAction.Blocked,
        AdoptionDeclarationPlan => GitPlannedAction.Adopt,
        ReplacementDeclarationPlan => GitPlannedAction.Replace,
        RemovalDeclarationPlan => GitPlannedAction.Remove
    };

    public static IDeclarationFailure? Blocker(ResourceDeclarationPlan plan) => plan switch
    {
        BlockedDeclarationPlan blocked => blocked.Failure,
        AdoptionDeclarationPlan => new DeclarationAdoptionRequired(),
        ReplacementDeclarationPlan or RemovalDeclarationPlan => new DeclarationReplacementRequired(),
        CreateDeclarationPlan or UpdateDeclarationPlan or NoOpDeclarationPlan => null
    };

    public static DeclarationVersionToken Versions(ResourceDeclarationPlan plan) => new(
        (Current(plan)?.Version.Documents ?? [])
        .Concat(Desired(plan)?.Dependencies?.Documents ?? [])
        .Distinct().OrderBy(item => item.DocumentId, StringComparer.Ordinal).ToArray());

    public static bool Same(ResourceDeclarationPlan expected, ResourceDeclarationPlan current) =>
        Action(expected) == Action(current) &&
        Equals(Blocker(expected), Blocker(current)) &&
        Current(expected)?.ResourceId == Current(current)?.ResourceId &&
        Versions(expected).Documents.SequenceEqual(Versions(current).Documents) &&
        SameProjection(Current(expected)?.Projection, Current(current)?.Projection) &&
        SameProjection(Desired(expected), Desired(current));

    public static bool SameProjection(DeclarationProjection? left, DeclarationProjection? right) =>
        left is null || right is null ? left is null && right is null :
        JsonElement.DeepEquals(left.Identity, right.Identity) &&
        JsonElement.DeepEquals(left.Configuration, right.Configuration);

    public static string Json(DeclarationProjection projection)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("identity");
            Write(writer, projection.Identity);
            writer.WritePropertyName("configuration");
            Write(writer, projection.Configuration);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string Hash(string json) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

    public static IReadOnlyList<string> ChangedFields(ResourceDeclarationPlan plan)
    {
        var desired = Desired(plan);
        if (desired is null || plan is NoOpDeclarationPlan)
            return [];

        var current = Current(plan)?.Projection;
        return Differences(current?.Identity, desired.Identity, "identity")
            .Concat(Differences(current?.Configuration, desired.Configuration, "configuration"))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<string> Differences(JsonElement? before, JsonElement after, string path)
    {
        if (before is { } previous && JsonElement.DeepEquals(previous, after))
            yield break;

        if (after.ValueKind == JsonValueKind.Object &&
            (before is null || before.Value.ValueKind == JsonValueKind.Object))
        {
            var previousProperties = before?.EnumerateObject().ToDictionary(item => item.Name, item => item.Value)
                                     ?? new Dictionary<string, JsonElement>();
            var nextProperties = after.EnumerateObject().ToDictionary(item => item.Name, item => item.Value);
            foreach (var name in previousProperties.Keys.Union(nextProperties.Keys).Order(StringComparer.Ordinal))
            {
                var child = $"{path}.{name}";
                if (!nextProperties.TryGetValue(name, out var value))
                    yield return child;
                else
                    foreach (var field in Differences(previousProperties.TryGetValue(name, out var old) ? old : null,
                                 value, child))
                        yield return field;
            }
        }
        else
            yield return path;
    }

    private static void Write(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                Write(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray())
                Write(writer, item);
            writer.WriteEndArray();
        }
        else
            element.WriteTo(writer);
    }
}
