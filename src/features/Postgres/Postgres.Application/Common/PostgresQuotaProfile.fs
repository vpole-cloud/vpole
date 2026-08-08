namespace Pole.Features.Postgres.Application.Common

open System.Collections.Generic
open Pole.Features.Quotas.Contracts

type PostgresQuotaInput =
    { RequestedCpuMillisPerInstance : int64
      RequestedMemoryMiPerInstance : int64
      Instances : int64 }

[<RequireQualifiedAccess>]
module PostgresQuotaInput =
    let create requestedCpuMillis requestedMemoryMi instances =
        if requestedCpuMillis < 1L then
            invalidArg (nameof requestedCpuMillis) "Requested CPU must be positive."
        if requestedMemoryMi < 1L then
            invalidArg (nameof requestedMemoryMi) "Requested memory must be positive."
        if instances < 1L then
            invalidArg (nameof instances) "Instances must be positive."

        { RequestedCpuMillisPerInstance = requestedCpuMillis
          RequestedMemoryMiPerInstance = requestedMemoryMi
          Instances = instances }

type PostgresClusterCountQuota() =
    inherit Quota<PostgresQuotaInput>()

    static member QuotaKey = QuotaKey("PostgresClusterCount")

    override _.Key = PostgresClusterCountQuota.QuotaKey
    override _.Unit = "clusters"
    override _.Calculate _ = 1L

type PostgresQuotaProfile(
    cpuQuota : CpuQuota,
    ramQuota : RamQuota,
    workloadInstancesQuota : WorkloadInstancesQuota,
    clusterCountQuota : PostgresClusterCountQuota) =
    inherit QuotaProfile<PostgresQuotaInput>()

    override _.Keys =
        [| CpuQuota.QuotaKey
           RamQuota.QuotaKey
           WorkloadInstancesQuota.QuotaKey
           PostgresClusterCountQuota.QuotaKey |]
        :> IReadOnlyCollection<QuotaKey>

    override _.Calculate(input) =
        let workload =
            WorkloadQuotaInput(
                input.RequestedCpuMillisPerInstance,
                input.RequestedMemoryMiPerInstance,
                input.Instances)

        QuotaDelta.Create(
            [| cpuQuota.Measure(workload)
               ramQuota.Measure(workload)
               workloadInstancesQuota.Measure(workload)
               clusterCountQuota.Measure(input) |])

[<RequireQualifiedAccess>]
module PostgresQuotaResourceKey =
    let create clusterId = QuotaResourceKey.Create("postgres-cluster", clusterId)
