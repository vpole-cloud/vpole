namespace Pole.Features.Postgres.Application.Backups

open System
open System.Net
open System.Text.Json
open System.Text.Json.Nodes
open k8s
open k8s.Autorest
open k8s.Models

module internal PostgresBackupObjects =
    type Identity = { Uid: string; ResourceVersion: string }

    let field (doc: JsonNode) (keys: string list) =
        keys |> List.fold (fun (parent: JsonNode) key ->
            if isNull parent then null else parent[key]) doc

    let value doc keys =
        let node = field doc keys
        if isNull node then null else node.ToString()

    let array doc keys =
        match field doc keys with
        | :? JsonArray as items -> items |> Seq.cast<JsonNode>
        | _ -> Seq.empty

    let identity doc =
        let uid = value doc ["metadata"; "uid"]
        let version = value doc ["metadata"; "resourceVersion"]
        if String.IsNullOrWhiteSpace uid || String.IsNullOrWhiteSpace version then None
        else Some { Uid = uid; ResourceVersion = version }

    let labelsMatch doc labels =
        labels |> List.forall (fun (key, expected) ->
            value doc ["metadata"; "labels"; key] = expected)

    let rec matches (desired: JsonNode) (current: JsonNode) =
        match desired, current with
        | (:? JsonObject as fields), (:? JsonObject as actual) ->
            fields |> Seq.forall (fun pair -> matches pair.Value actual[pair.Key])
        | _ -> JsonNode.DeepEquals(desired, current)

    let get (client: IKubernetes) group ns plural name ct =
        task {
            try
                let! result = client.CustomObjects.GetNamespacedCustomObjectAsync(
                    group, "v1", ns, plural, name, cancellationToken = ct)
                return JsonSerializer.SerializeToNode(result)
            with
            | :? HttpOperationException as error when error.Response.StatusCode = HttpStatusCode.NotFound ->
                return null
        }

    let create (client: IKubernetes) group ns plural (manifest: JsonNode) ct =
        task {
            let! result = client.CustomObjects.CreateNamespacedCustomObjectAsync(
                manifest, group, "v1", ns, plural, cancellationToken = ct)
            return JsonSerializer.SerializeToNode(result)
        }

    let patch (client: IKubernetes) group ns plural name identity (body: JsonNode) ct =
        task {
            if isNull body["metadata"] then body["metadata"] <- JsonObject()
            body["metadata"]["resourceVersion"] <- JsonValue.Create(identity.ResourceVersion)
            body["metadata"]["uid"] <- JsonValue.Create(identity.Uid)
            let patch = V1Patch(body.ToJsonString(), V1Patch.PatchType.MergePatch)
            let! result = client.CustomObjects.PatchNamespacedCustomObjectAsync(
                patch, group, "v1", ns, plural, name, cancellationToken = ct)
            return JsonSerializer.SerializeToNode(result)
        }

    let condition doc kind =
        array doc ["status"; "conditions"]
        |> Seq.tryFind (fun condition -> value condition ["type"] = kind)
        |> Option.toObj

    let timestamp doc keys =
        match DateTimeOffset.TryParse(
            value doc keys, Globalization.CultureInfo.InvariantCulture,
            Globalization.DateTimeStyles.RoundtripKind) with
        | true, parsed -> Nullable(parsed.ToUniversalTime())
        | false, _ -> Nullable()
