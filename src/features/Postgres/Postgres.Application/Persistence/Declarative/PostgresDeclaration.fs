namespace Pole.Features.Postgres.Application.Persistence.Declarative

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.Text.Json
open Pole.Features.GitOps.Contracts
open Pole.Features.GitOps.Primitives
open Pole.Features.Postgres.Application.Common
open Pole.Features.Postgres.Domain
open Pole.Features.ResourceDeployments.Contracts
open Pole.Features.Secrets.Contracts

type PostgresPinnedProfile =
    { Name : string
      Revision : int64 }

type PostgresDeclarationPlacement =
    { DeploymentTarget : string
      ComputeProfile : PostgresPinnedProfile
      IsolationScope : string }

type PostgresDeclarationStorage =
    { SizeGb : int
      Profile : PostgresPinnedProfile }

type PostgresDeclaredBootstrap =
    { Secret : string
      Key : string }

type PostgresDeclaration =
    { ResourceGroup : string
      Placement : PostgresDeclarationPlacement
      Storage : PostgresDeclarationStorage
      Version : int
      Instances : Nullable<int>
      Username : string
      BootstrapPassword : PostgresDeclaredBootstrap }

type PostgresDeclarationError =
    | AcceptedSnapshotRequired = 0
    | ResourceDeleting = 1
    | ResourceGroupUnavailable = 2
    | IsolationScopeUnavailable = 3
    | DeploymentTargetUnavailable = 4
    | ComputeProfileUnavailable = 5
    | StorageProfileUnavailable = 6
    | PlacementUnavailable = 7
    | ResourceRegistrationUnavailable = 8
    | QuotaUnavailable = 9
    | ExplicitAdoptionRequired = 10
    | UnexpectedOperation = 11
    | BootstrapSecretUnavailable = 12
    | BootstrapCredentialUnavailable = 13
    | BootstrapProtectionUnavailable = 14
    | BootstrapValuesTooLarge = 15
    | StatefulSetInstancesUnsupported = 16
    | StatefulSetStorageResizeUnsupported = 17
    | StorageShrinkUnsupported = 18
    | ComputeRuntimeClassUnsupported = 19

type PostgresDeclarationRejected =
    { Reason : PostgresDeclarationError }
    interface IDeclarationFailure

type PostgresSecretDeclarationRejected =
    { Reason : ConsumerSecretReferenceError }
    interface IDeclarationFailure

type PostgresManagedIdentity =
    { ResourceGroupId : string
      IsolationScopeId : string
      DeploymentTargetId : string
      Version : int
      DeploymentMode : PostgresDeploymentMode
      BootstrapSecretId : string
      BootstrapKey : string
      Username : string
      StorageProfileId : string
      Storage : ResourceDeploymentStorageConfiguration }

type PostgresProjectionInput =
    { Identity : PostgresManagedIdentity
      Instances : int
      StorageSizeGb : int
      FixedReplicas : int
      Compute : ResourceDeploymentComputeConfiguration }

[<RequireQualifiedAccess>]
module PostgresDeclarations =
    [<Literal>]
    let Kind = "PostgresCluster"

    [<Literal>]
    let private BootstrapPasswordField = "spec.bootstrapPassword"

    let failure reason = { Reason = reason } : PostgresDeclarationRejected
    let blocked reason = DeclarationReadBlocked(failure reason)
    let instances (spec : PostgresDeclaration) = spec.Instances.GetValueOrDefault(PostgresCluster.DefaultInstances)

    let private validProfile (profile : PostgresPinnedProfile) =
        not (isNull (box profile)) && DeclarationNames.Valid profile.Name && profile.Revision > 0L

    let normalize (settings : PostgresSettings) (spec : JsonElement) =
        let value = DeclarationJson.Read<PostgresDeclaration> spec
        DeclarationJson.Require(DeclarationNames.Valid value.ResourceGroup, DeclarationFieldPaths.ResourceGroup)
        DeclarationJson.Require(not (isNull (box value.Placement)), DeclarationFieldPaths.Placement)
        DeclarationJson.Require(
            DeclarationNames.Valid value.Placement.DeploymentTarget, DeclarationFieldPaths.PlacementDeploymentTarget)
        DeclarationJson.Require(
            isNull value.Placement.IsolationScope
            || DeclarationNames.Valid value.Placement.IsolationScope && value.Placement.IsolationScope <> "default",
            DeclarationFieldPaths.PlacementIsolationScope)
        DeclarationJson.Require(
            validProfile value.Placement.ComputeProfile, DeclarationFieldPaths.PlacementComputeProfile)
        DeclarationJson.Require(
            not (isNull (box value.Storage)) && value.Storage.SizeGb > 0, DeclarationFieldPaths.StorageSizeGb)
        DeclarationJson.Require(validProfile value.Storage.Profile, DeclarationFieldPaths.StorageProfile)
        DeclarationJson.Require(PostgresSettings.isSupportedVersion settings value.Version, "spec.version")
        DeclarationJson.Require(instances value > 0, "spec.instances")
        DeclarationJson.Require(
            not (String.IsNullOrWhiteSpace value.Username)
            && PostgresManagedSecret.totalValueBytes [value.Username] <= PostgresManagedSecret.maximumValueBytes,
            "spec.username")
        DeclarationJson.Require(not (isNull (box value.BootstrapPassword)), BootstrapPasswordField)
        DeclarationJson.Require(
            DeclarationNames.Valid value.BootstrapPassword.Secret, "spec.bootstrapPassword.secret")
        DeclarationJson.Require(
            SecretKeyReferenceRules.ValidKey value.BootstrapPassword.Key, "spec.bootstrapPassword.key")
        DeclarationJson.Write { value with Instances = Nullable(instances value) }

    let facts (spec : JsonElement) =
        let value = DeclarationJson.Read<PostgresDeclaration> spec
        let references = ResizeArray<DeclaredReference>()
        references.Add(DeclaredReference(
            ResourceAddress("Secret", value.BootstrapPassword.Secret), BootstrapPasswordField,
            value.BootstrapPassword.Key, RequiresAccess = true))

        if not (isNull value.Placement.IsolationScope) then
            references.Add(DeclaredReference(
                ResourceAddress("IsolationScope", value.Placement.IsolationScope),
                DeclarationFieldPaths.PlacementIsolationScope))

        DeclarationFacts(ResourceAddress("ResourceGroup", value.ResourceGroup), [||], references, [||], [||])

    let steps (address : ResourceAddress) (spec : JsonElement) : IReadOnlyList<DeclarationStep> =
        let value = DeclarationJson.Read<PostgresDeclaration> spec
        let dependencies = ResizeArray<DeclarationDependency>()
        dependencies.Add(DeclarationDependency(ResourceAddress("ResourceGroup", value.ResourceGroup),
            DeclarationPhase.Prepare))
        dependencies.Add(DeclarationDependency(ResourceAddress("Secret", value.BootstrapPassword.Secret),
            DeclarationPhase.Prepare))

        if not (isNull value.Placement.IsolationScope) then
            dependencies.Add(DeclarationDependency(ResourceAddress("IsolationScope", value.Placement.IsolationScope),
                DeclarationPhase.Prepare))

        [| DeclarationStep(DeclarationPhase.Prepare, dependencies)
           DeclarationStep(DeclarationPhase.Configure,
                [| DeclarationDependency(address, DeclarationPhase.Prepare) |]) |]

    let describe (failure : IDeclarationFailure) =
        match failure with
        | :? PostgresSecretDeclarationRejected as rejected ->
            ConsumerSecretReferenceErrorCodes.Code rejected.Reason
        | :? PostgresDeclarationRejected as rejected when Enum.IsDefined rejected.Reason -> string rejected.Reason
        | _ -> invalidArg "failure" "Unsupported PostgreSQL declaration failure."

    let private ordered key (items : seq<'a>) =
        items |> Seq.sortWith (fun a b -> StringComparer.Ordinal.Compare(key a, key b))

    let private normalizeTerm (term : ResourceDeploymentNodeAffinityTerm) =
        let expressions =
            term.Expressions
            |> Seq.map (fun value -> ResourceDeploymentNodeAffinityExpression(
                value.Key, value.Operator, value.Values |> ordered id |> ImmutableList.CreateRange))
            |> ordered (fun value -> $"{value.Key}/{value.Operator}")
            |> ImmutableList.CreateRange
        ResourceDeploymentNodeAffinityTerm expressions

    let computeProjection (compute : ResourceDeploymentComputeConfiguration) =
        let terms =
            compute.RequiredNodeAffinity |> Seq.map normalizeTerm
            |> ordered (fun term -> (DeclarationJson.Write term).GetRawText()) |> Seq.toArray
        let preferences =
            compute.PreferredNodeAffinity
            |> Seq.map (fun term -> ResourceDeploymentPreferredNodeAffinityTerm(term.Weight, normalizeTerm term.Term))
            |> ordered (fun term -> (DeclarationJson.Write term).GetRawText()) |> Seq.toArray
        DeclarationJson.Write
            {| Requests = compute.Requests
               Limits = compute.Limits
               Architecture = compute.Architecture
               RuntimeClassName = compute.RuntimeClassName
               ExtendedResources = compute.ExtendedResources |> ordered _.Name |> Seq.toArray
               NodeSelector = compute.NodeSelector |> ordered _.Key |> Seq.toArray
               RequiredNodeAffinity = terms
               PreferredNodeAffinity = preferences
               Tolerations = compute.Tolerations
                   |> ordered (fun value -> (DeclarationJson.Write value).GetRawText()) |> Seq.toArray |}

    let project phase (input : PostgresProjectionInput) =
        let configuration =
            if phase = DeclarationPhase.Prepare then
                DeclarationJson.Write(Dictionary<string, obj>())
            else
                DeclarationJson.Write
                    {| Instances = input.Instances
                       StorageSizeGb = input.StorageSizeGb
                       FixedReplicas = input.FixedReplicas
                       ComputeProfileId = input.Compute.ComputeProfileId
                       ComputeProfileRevision = input.Compute.ComputeProfileRevision
                       Compute = computeProjection input.Compute |}

        DeclarationProjection(DeclarationJson.Write input.Identity, configuration)
