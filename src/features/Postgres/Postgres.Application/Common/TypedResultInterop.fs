namespace Pole.Features.Postgres.Application.Common

[<RequireQualifiedAccess>]
module TypedResultInterop =
    /// Converts a provider-owned C# typed result once at the F# boundary.
    /// Application workflows can then use F#'s exhaustive Ok/Error matching.
    let toResult
        (result : Pole.ResultType.Result<'value, 'error>)
        : Result<'value, 'error> =
        let succeeded, value, error =
            Pole.ResultType.TypedResultExtensions.TryGetValue(result)
        if succeeded then Ok value else Error error
