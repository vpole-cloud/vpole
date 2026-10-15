namespace Pole.Features.Postgres.Application.Kubernetes

open System
open System.Text.Json
open k8s
open k8s.Models

type internal PostgresClusterObservation =
    { Metadata: V1ObjectMeta
      Desired: int
      Current: int
      Ready: int
      ReadyCondition: bool
      RequiresPodConformance: bool
      Spec: JsonElement }

module internal PostgresClusterObservation =
    let element (native: obj) =
        match native with
        | :? JsonElement as value -> value
        | _ -> JsonSerializer.SerializeToElement(native)

    let property (parent: JsonElement) key =
        let mutable value = Unchecked.defaultof<JsonElement>

        if
            parent.ValueKind = JsonValueKind.Object
            && parent.TryGetProperty((key: string), &value)
        then
            Some value
        else
            None

    let stringValue value =
        match value with
        | Some(value: JsonElement) when value.ValueKind = JsonValueKind.String -> value.GetString()
        | _ -> null

    let integer value =
        let mutable result = 0

        match value with
        | Some(value: JsonElement) when
            value.ValueKind = JsonValueKind.Number
            && value.TryGetInt32(&result)
            && result >= 0
            ->
            Some result
        | _ -> None

    let integer64 value =
        let mutable result = 0L

        match value with
        | Some(value: JsonElement) when
            value.ValueKind = JsonValueKind.Number
            && value.TryGetInt64(&result)
            && result >= 0L
            ->
            Some result
        | _ -> None

    let metadata native =
        match property (element native) "metadata" with
        | Some value when value.ValueKind = JsonValueKind.Object ->
            KubernetesJson.Deserialize<V1ObjectMeta>(value.GetRawText())
        | _ -> raise (JsonException("InvalidNativeMetadata"))

    let tryRead native =
        try
            let root = element native
            let meta = metadata native
            let spec = property root "spec"
            let status = property root "status"

            let desired =
                spec |> Option.bind (fun value -> property value "instances") |> integer

            let current =
                status |> Option.bind (fun value -> property value "instances") |> integer

            let ready =
                status |> Option.bind (fun value -> property value "readyInstances") |> integer

            let condition = status |> Option.bind (fun value -> property value "conditions")

            let readyConditions =
                match condition with
                | Some value when value.ValueKind = JsonValueKind.Array ->
                    value.EnumerateArray()
                    |> Seq.filter (fun value ->
                        stringValue (property value "type") = "Ready"
                        && stringValue (property value "status") = "True")
                    |> Seq.toArray
                | _ -> [||]

            let currentCondition value =
                integer64 (property value "observedGeneration")
                |> Option.exists (fun generation -> generation >= meta.Generation.GetValueOrDefault())

            let unversionedCondition value =
                match property value "observedGeneration" with
                | None -> true // CNPG 1.30 omits this field; independent Pod/spec evidence is mandatory below.
                | Some value -> integer64 (Some value) = Some 0L

            let readyCondition = readyConditions |> Array.exists currentCondition

            let requiresPodConformance =
                not readyCondition && (readyConditions |> Array.exists unversionedCondition)

            match desired, current, ready with
            | Some desired, Some current, Some ready when desired > 0 && meta.Generation.GetValueOrDefault() > 0L ->
                Some
                    { Metadata = meta
                      Desired = desired
                      Current = current
                      Ready = ready
                      ReadyCondition = readyCondition
                      RequiresPodConformance = requiresPodConformance
                      Spec = spec.Value }
            | _ -> None
        with :? JsonException ->
            None
