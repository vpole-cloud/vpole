namespace Pole.Features.Postgres.Application.Common

open System.Collections.Generic
open Pole.Features.Postgres.Domain
open Pole.Features.Quotas.Contracts

type PostgresClusterCountQuota() =
    inherit Quota<PostgresClusterSpec>()

    static member QuotaKey = QuotaKey("PostgresClusterCount")

    override _.Key = PostgresClusterCountQuota.QuotaKey
    override _.Unit = "clusters"
    override _.Calculate _ = 1L

type PostgresQuotaProfile(
    cpuQuota : CpuQuota,
    ramQuota : RamQuota,
    workloadInstancesQuota : WorkloadInstancesQuota,
    clusterCountQuota : PostgresClusterCountQuota) =
    inherit QuotaProfile<PostgresClusterSpec>()

    override _.Keys =
        [| CpuQuota.QuotaKey
           RamQuota.QuotaKey
           WorkloadInstancesQuota.QuotaKey
           PostgresClusterCountQuota.QuotaKey |]
        :> IReadOnlyCollection<QuotaKey>

    override _.Calculate(spec) =
        let workload =
            WorkloadQuotaInput(
                int64 spec.Resources.Requests.CpuCores * 1000L,
                int64 spec.Resources.Requests.MemoryMi,
                int64 spec.Instances)

        QuotaDelta.Create(
            [| cpuQuota.Measure(workload)
               ramQuota.Measure(workload)
               workloadInstancesQuota.Measure(workload)
               clusterCountQuota.Measure(spec) |])

[<RequireQualifiedAccess>]
module PostgresQuotaResourceKey =
    let create clusterId = QuotaResourceKey.Create("postgres-cluster", clusterId)
