namespace Pole.Features.Postgres.Application.ManagedDatabases

open System.Threading
open System.Threading.Tasks
open MongoDB.Driver
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Application.Mongo
open Pole.Features.Postgres.Contracts
open Pole.Features.Postgres.Domain
open Pole.Features.ResourceDeployments.Contracts

type ReadPostgresManagedDatabaseCandidates =
    delegate of tenantId: string * deploymentTargetId: string * ct: CancellationToken -> Task<PostgresManagedDatabaseCandidate array>

type MongoReadPostgresManagedDatabaseCandidates
    (clusters: IMongoCollection<PostgresCluster>, loadPlacements: LoadResourceDeploymentRuntimePlacements, loadSummaries: LoadResourceDeploymentSets) =
    member _.InvokeAsync(tenantId: string, deploymentTargetId: string, ct: CancellationToken) =
        task {
            let! found = clusters.Find(PostgresFilters.synchronousHa tenantId).SortBy(fun item -> item.Name).ToListAsync(ct)
            let! placements = loadPlacements.Invoke(
                LoadResourceDeploymentRuntimePlacementsRequest(tenantId, PostgresResourceDeployment.ResourceType, found |> Seq.map _.Id |> Seq.toArray), ct)
            let! summaries = loadSummaries.Invoke(LoadResourceDeploymentSetsRequest(
                                tenantId, PostgresResourceDeployment.ResourceType, found |> Seq.map _.Id |> Seq.toArray), ct)
            return found |> Seq.choose (fun cluster ->
                let placed = placements |> Seq.filter(fun item -> item.ResourceId = cluster.Id && item.DesiredOperation <> ResourceDeploymentDesiredOperation.Delete && item.Status = ResourceDeploymentStatus.Applied && item.DesiredGeneration > 0L) |> Seq.toArray
                let ready =
                    match summaries.TryGetValue cluster.Id with
                    | true, values when values.Count = 1 ->
                        let summary = values[0]
                        summary.Status = ResourceDeploymentStatus.Applied &&
                        summary.DesiredOperation = ResourceDeploymentDesiredOperation.Apply &&
                        summary.ObservedGeneration = summary.DesiredGeneration &&
                        summary.ResourceRevision = cluster.Revision && summary.FixedReplicas >= 3
                    | _ -> false
                if ready && placed.Length = 1 && placed[0].DeploymentTargetId = deploymentTargetId then
                    Some(PostgresManagedDatabaseCandidate(cluster.Id, cluster.Name, deploymentTargetId, cluster.GroupId,
                        placed[0].IsolationScopeId, cluster.Spec.Instances, cluster.Spec.ReplicationMode.ToString()))
                else None) |> Seq.toArray
        }
