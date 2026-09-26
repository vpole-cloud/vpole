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

    let computeProfile
        (result : Pole.Features.ComputeProfiles.Contracts.ResolveComputeProfileOutcome)
        : Result<Pole.Features.ComputeProfiles.Contracts.ResolvedComputeProfile,
                 Pole.Features.ComputeProfiles.Contracts.ComputeProfileResolutionError> =
        match result.Value with
        | :? Pole.Features.ComputeProfiles.Contracts.ResolvedComputeProfile as value -> Ok value
        | :? Pole.Features.ComputeProfiles.Contracts.ComputeProfileResolutionError as error -> Error error
        | value -> invalidOp $"Unexpected compute profile outcome: {value}"

    let storageProfile
        (result : Pole.Features.StorageProfiles.Contracts.ResolveStorageProfileOutcome)
        : Result<Pole.Features.StorageProfiles.Contracts.ResolvedStorageProfile,
                 Pole.Features.StorageProfiles.Contracts.StorageProfileResolutionError> =
        match result.Value with
        | :? Pole.Features.StorageProfiles.Contracts.ResolvedStorageProfile as value -> Ok value
        | :? Pole.Features.StorageProfiles.Contracts.StorageProfileResolutionError as error -> Error error
        | value -> invalidOp $"Unexpected storage profile outcome: {value}"

    let resourceRegistration
        (result : Pole.Features.ResourceGroups.Contracts.ResourceRegistrationOutcome)
        : Result<Pole.Features.ResourceGroups.Contracts.RegisteredResource,
                 Pole.Features.ResourceGroups.Contracts.ResourceRegistrationMutationError> =
        match result.Value with
        | :? Pole.Features.ResourceGroups.Contracts.RegisteredResource as value -> Ok value
        | :? Pole.Features.ResourceGroups.Contracts.ResourceRegistrationMutationError as error -> Error error
        | value -> invalidOp $"Unexpected resource registration outcome: {value}"

    let resourceUnregistration
        (result : Pole.Features.ResourceGroups.Contracts.ResourceUnregistrationOutcome)
        : Result<Pole.Features.ResourceGroups.Contracts.ResourceUnregistered,
                 Pole.Features.ResourceGroups.Contracts.ResourceRegistrationMutationError> =
        match result.Value with
        | :? Pole.Features.ResourceGroups.Contracts.ResourceUnregistered as value -> Ok value
        | :? Pole.Features.ResourceGroups.Contracts.ResourceRegistrationMutationError as error -> Error error
        | value -> invalidOp $"Unexpected resource unregistration outcome: {value}"
