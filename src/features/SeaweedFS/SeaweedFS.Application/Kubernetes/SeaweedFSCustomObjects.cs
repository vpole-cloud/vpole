using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace Pole.Features.SeaweedFS.Application.Kubernetes;

internal sealed record SeaweedFSObjectKind(
    string Group,
    string Plural
)
{
    private const string SeaweedGroup = "seaweed.seaweedfs.com";
    public static readonly SeaweedFSObjectKind Cluster = new(SeaweedGroup, "seaweeds");
    public static readonly SeaweedFSObjectKind Bucket = new(SeaweedGroup, "buckets");
    public static readonly SeaweedFSObjectKind Identity = new(SeaweedGroup, "s3identities");
    public static readonly SeaweedFSObjectKind Credentials = new(SeaweedGroup, "s3credentials");
    public static readonly SeaweedFSObjectKind Policy = new(SeaweedGroup, "s3policies");
    public static readonly SeaweedFSObjectKind Binding = new(SeaweedGroup, "s3policybindings");
    public static readonly SeaweedFSObjectKind Repair = new(SeaweedGroup, "adminscripts");
    public static readonly SeaweedFSObjectKind Postgres = new("postgresql.cnpg.io", "clusters");
}

// Resource versions fence create/update/delete races. Every existing object must carry the full ownership identity.
internal sealed class SeaweedFSCustomObjects(
    IKubernetes client,
    string ns,
    IReadOnlyDictionary<string, string> ownership,
    long generation
)
{
    private const string Generation = "pole.io/desired-generation";

    public async Task<JsonObject?> ReadAsync(
        SeaweedFSObjectKind kind,
        string name,
        CancellationToken ct)
    {
        try
        {
            var response = await client.CustomObjects.GetNamespacedCustomObjectAsync(
                kind.Group,
                "v1",
                ns,
                kind.Plural,
                name,
                cancellationToken: ct);
            var document = JsonSerializer.SerializeToNode(response)!.AsObject();
            var labels = document["metadata"]?["labels"];
            if (ownership.Any(label => (string?)labels?[label.Key] != label.Value))
                throw new InvalidOperationException("SeaweedFS Kubernetes object ownership changed.");
            var recorded = (string?)document["metadata"]?["annotations"]?[Generation];
            if (long.TryParse(recorded, out var prior) && prior > generation)
                throw new InvalidOperationException("SeaweedFS deployment generation has advanced.");
            return document;
        }
        catch (HttpOperationException error) when (error.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<JsonObject> ApplyAsync(
        SeaweedFSObjectKind kind,
        object manifest,
        CancellationToken ct)
    {
        var desired = JsonSerializer.SerializeToNode(manifest)!.AsObject();
        var metadata = desired["metadata"]!.AsObject();
        var name = (string)metadata["name"]!;
        var annotations = metadata["annotations"] as JsonObject ?? new JsonObject();
        annotations[Generation] = generation.ToString(System.Globalization.CultureInfo.InvariantCulture);
        metadata["annotations"] = annotations;
        var current = await ReadAsync(kind, name, ct);
        if (current is null)
        {
            var created = await client.CustomObjects.CreateNamespacedCustomObjectAsync(
                desired,
                kind.Group,
                "v1",
                ns,
                kind.Plural,
                cancellationToken: ct);
            return JsonSerializer.SerializeToNode(created)!.AsObject();
        }

        var currentMetadata = current["metadata"]!.AsObject();
        if (currentMetadata["deletionTimestamp"] is not null)
            throw new InvalidOperationException("SeaweedFS object is being deleted.");

        // The API server applies CRD defaults. Compare only fields this provider declares.
        var gatewayRemoved = kind == SeaweedFSObjectKind.Cluster && desired["spec"]?["s3"] is null &&
            current["spec"]?["s3"] is not null;
        if (!gatewayRemoved && Contains(current["spec"], desired["spec"]) &&
            Contains(currentMetadata["annotations"], annotations))
            return current;
        metadata["resourceVersion"] = currentMetadata["resourceVersion"]!.DeepClone();
        foreach (var field in new[] { "finalizers", "ownerReferences" })
            if (currentMetadata[field] is { } existing)
                metadata[field] = existing.DeepClone();
        foreach (var field in new[] { "annotations", "labels" })
        {
            var merged = currentMetadata[field]?.DeepClone().AsObject() ?? new JsonObject();
            if (metadata[field] is JsonObject values)
                foreach (var pair in values)
                    merged[pair.Key] = pair.Value?.DeepClone();
            metadata[field] = merged;
        }

        var replaced = await client.CustomObjects.ReplaceNamespacedCustomObjectAsync(
            desired,
            kind.Group,
            "v1",
            ns,
            kind.Plural,
            name,
            cancellationToken: ct);
        return JsonSerializer.SerializeToNode(replaced)!.AsObject();
    }

    public async Task<bool> DeleteAsync(
        SeaweedFSObjectKind kind,
        string name,
        CancellationToken ct)
    {
        var current = await ReadAsync(kind, name, ct);
        if (current is null)
            return true;
        var metadata = current["metadata"]!;
        if (metadata["deletionTimestamp"] is null)
            await client.CustomObjects.DeleteNamespacedCustomObjectAsync(
                kind.Group,
                "v1",
                ns,
                kind.Plural,
                name,
                body: new V1DeleteOptions
                {
                    PropagationPolicy = "Foreground",
                    Preconditions = new V1Preconditions
                    {
                        Uid = (string)metadata["uid"]!,
                        ResourceVersion = (string)metadata["resourceVersion"]!
                    },
                },
                cancellationToken: ct);
        return false;
    }

    internal static bool Contains(JsonNode? actual, JsonNode? desired) => desired switch
    {
        JsonObject fields => actual is JsonObject values && fields.All(pair => Contains(values[pair.Key], pair.Value)),
        JsonArray items => actual is JsonArray values && values.Count == items.Count &&
            items.Select((item, index) => Contains(values[index], item)).All(matches => matches),
        _ => JsonNode.DeepEquals(actual, desired),
    };

    public static bool Ready(JsonObject resource)
    {
        var generation = (long?)resource["metadata"]?["generation"];
        var status = resource["status"];
        return generation is > 0 && generation == (long?)status?["observedGeneration"] &&
            status?["conditions"] is JsonArray conditions && conditions.Any(condition =>
                (string?)condition?["type"] == "Ready" && (string?)condition?["status"] == "True");
    }
}
