// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.Postgres.Application.Common

[<RequireQualifiedAccess>]
module TypedResultInterop =
    let computeProfile
        (result: Pole.Features.ComputeProfiles.Contracts.ResolveComputeProfileOutcome)
        : Result<
              Pole.Features.ComputeProfiles.Contracts.ResolvedComputeProfile,
              Pole.Features.ComputeProfiles.Contracts.ComputeProfileResolutionError
           >
        =
        match result.Value with
        | :? Pole.Features.ComputeProfiles.Contracts.ResolvedComputeProfile as value -> Ok value
        | :? Pole.Features.ComputeProfiles.Contracts.ComputeProfileResolutionError as error -> Error error
        | value -> invalidOp $"Unexpected compute profile outcome: {value}"

    let storageProfile
        (result: Pole.Features.StorageProfiles.Contracts.ResolveStorageProfileOutcome)
        : Result<
              Pole.Features.StorageProfiles.Contracts.ResolvedStorageProfile,
              Pole.Features.StorageProfiles.Contracts.StorageProfileResolutionError
           >
        =
        match result.Value with
        | :? Pole.Features.StorageProfiles.Contracts.ResolvedStorageProfile as value -> Ok value
        | :? Pole.Features.StorageProfiles.Contracts.StorageProfileResolutionError as error -> Error error
        | value -> invalidOp $"Unexpected storage profile outcome: {value}"

    let resourceRegistration
        (result: Pole.Features.ResourceGroups.Contracts.ResourceRegistrationOutcome)
        : Result<
              Pole.Features.ResourceGroups.Contracts.RegisteredResource,
              Pole.Features.ResourceGroups.Contracts.ResourceRegistrationMutationError
           >
        =
        match result.Value with
        | :? Pole.Features.ResourceGroups.Contracts.RegisteredResource as value -> Ok value
        | :? Pole.Features.ResourceGroups.Contracts.ResourceRegistrationMutationError as error -> Error error
        | value -> invalidOp $"Unexpected resource registration outcome: {value}"

    let resourceUnregistration
        (result: Pole.Features.ResourceGroups.Contracts.ResourceUnregistrationOutcome)
        : Result<
              Pole.Features.ResourceGroups.Contracts.ResourceUnregistered,
              Pole.Features.ResourceGroups.Contracts.ResourceRegistrationMutationError
           >
        =
        match result.Value with
        | :? Pole.Features.ResourceGroups.Contracts.ResourceUnregistered as value -> Ok value
        | :? Pole.Features.ResourceGroups.Contracts.ResourceRegistrationMutationError as error -> Error error
        | value -> invalidOp $"Unexpected resource unregistration outcome: {value}"

    let createResourceDeployment
        (result: Pole.Features.ResourceDeployments.Contracts.CreateResourceDeploymentOutcome)
        : Result<
              Pole.Features.ResourceDeployments.Contracts.PlacementSummaryDto,
              Pole.Features.ResourceDeployments.Contracts.CreateResourceDeploymentError
           >
        =
        match result.Value with
        | :? Pole.Features.ResourceDeployments.Contracts.PlacementSummaryDto as value -> Ok value
        | :? Pole.Features.ResourceDeployments.Contracts.CreateResourceDeploymentError as error -> Error error
        | value -> invalidOp $"Unexpected resource deployment creation outcome: {value}"

    let currentResourceDeploymentCompute
        (result: Pole.Features.ResourceDeployments.Contracts.LoadCurrentResourceDeploymentComputeOutcome)
        : Result<
              Pole.Features.ResourceDeployments.Contracts.CurrentResourceDeploymentComputeDto,
              Pole.Features.ResourceDeployments.Contracts.LoadCurrentResourceDeploymentComputeError
           >
        =
        match result.Value with
        | :? Pole.Features.ResourceDeployments.Contracts.CurrentResourceDeploymentComputeDto as value -> Ok value
        | :? Pole.Features.ResourceDeployments.Contracts.LoadCurrentResourceDeploymentComputeError as error ->
            Error error
        | value -> invalidOp $"Unexpected resource deployment compute outcome: {value}"

    let resourceDeploymentMutation
        (result: Pole.Features.ResourceDeployments.Contracts.ResourceDeploymentMutationOutcome)
        : Result<
              Pole.Features.ResourceDeployments.Contracts.PlacementSummaryDto,
              Pole.Features.ResourceDeployments.Contracts.ResourceDeploymentMutationError
           >
        =
        match result.Value with
        | :? Pole.Features.ResourceDeployments.Contracts.PlacementSummaryDto as value -> Ok value
        | :? Pole.Features.ResourceDeployments.Contracts.ResourceDeploymentMutationError as error -> Error error
        | value -> invalidOp $"Unexpected resource deployment mutation outcome: {value}"

    let targetAdmission
        (result: Pole.Features.DeploymentTargets.Contracts.AdmitDeploymentTargetForNewWorkloadOutcome)
        : Result<
              Pole.Features.DeploymentTargets.Contracts.DeploymentTargetLookupDto,
              Pole.Features.DeploymentTargets.Contracts.DeploymentTargetNewWorkloadAdmissionError
           >
        =
        match result.Value with
        | :? Pole.Features.DeploymentTargets.Contracts.DeploymentTargetLookupDto as value -> Ok value
        | :? Pole.Features.DeploymentTargets.Contracts.DeploymentTargetNewWorkloadAdmissionError as error -> Error error
        | value -> invalidOp $"Unexpected deployment target admission outcome: {value}"
