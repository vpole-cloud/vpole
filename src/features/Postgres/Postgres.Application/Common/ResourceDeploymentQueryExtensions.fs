// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.Postgres.Application.Common

open System.Threading
open System.Threading.Tasks
open Pole.Features.ResourceDeployments.Contracts
open Pole.Workloads

[<AutoOpen>]
module ResourceDeploymentQueryExtensions =
    type LoadResourceDeployment with
        member this.FindOptionAsync
            (tenantId: string, resourceType: ResourceTypeId, resourceId: string, cancellationToken: CancellationToken)
            : Task<PlacementSummaryDto option> =
            task {
                let! placement =
                    this.Invoke(LoadResourceDeploymentRequest(tenantId, resourceType, resourceId), cancellationToken)

                return Option.ofObj placement
            }
