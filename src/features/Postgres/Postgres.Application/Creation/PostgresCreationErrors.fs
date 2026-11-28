// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.Postgres.Application.Creation

open Pole.Features.ComputeProfiles.Contracts
open Pole.Features.DeploymentTargets.Contracts
open Pole.Features.Postgres.Application.Credentials
open Pole.Features.Postgres.Application.Clusters
open Pole.Features.Quotas.Contracts
open Pole.Features.ResourceDeployments.Contracts
open Pole.Features.ResourceGroups.Contracts
open Pole.Features.StorageProfiles.Contracts

type CreatePostgresClusterStateError =
    | PostgresClusterComputeRuntimeClassUnsupported of deploymentTargetId: string
    | PostgresClusterRestorePlacementChanged
    | PostgresClusterRestoreTargetAlreadyCreated
    | PostgresClusterRestoreOperationUnavailable

type CreatePostgresClusterError =
    | CreateState of CreatePostgresClusterStateError
    | CreateTarget of DeploymentTargetNewWorkloadAdmissionError
    | CreateCompute of ComputeProfileResolutionError
    | CreateStorage of StorageProfileResolutionError
    | CreateCredential of PostgresCredentialError
    | CreateDeployment of CreateResourceDeploymentError
    | CreateRegistration of ResourceRegistrationMutationError
    | CreateNameAlreadyExists of PostgresClusterNameAlreadyExists
    | CreateQuota of QuotaExecutionError
