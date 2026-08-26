using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application.Endpoints;
using Pole.Features.Request;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Features.StorageProfiles.Application.Endpoints;
using Pole.Features.StorageProfiles.Application.Endpoints.KubernetesInventory;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Endpoints;
using Pole.Features.WebServices.Application.Endpoints.Releases;
using Pole.Features.WebServices.Application.Persistence;
using Pole.Features.WebServices.Application.Persistence.Deployments;
using Pole.Features.WebServices.Application.Persistence.Lifecycle;
using Pole.Mediator;
using Pole.ResultType;
using Xunit.Sdk;

namespace Pole.Tests.Integration.Infrastructure;

public static class ResultAssertionExtensions
{
    public static WebServiceDeploymentCompletionRecorded AssertSuccess(
        this CompleteWebServiceDeploymentOutcome outcome) => outcome switch
    {
        WebServiceDeploymentCompletionRecorded success => success,
        WebServiceFinalizationError error =>
            UnexpectedError<WebServiceDeploymentCompletionRecorded>(error)
    };

    public static WebServiceDeletionFinalized AssertSuccess(
        this FinalizeWebServiceDeletionOutcome outcome) => outcome switch
    {
        WebServiceDeletionFinalized success => success,
        WebServiceFinalizationError error =>
            UnexpectedError<WebServiceDeletionFinalized>(error)
    };

    public static CreateRedisServiceResponse AssertSuccess(
        this MediatorResult<CreateRedisServiceOutcome> result) =>
        ResultAssertionExtensions.AssertSuccess<CreateRedisServiceOutcome>(result).AssertSuccess();

    public static UpdateRedisServiceResponse AssertSuccess(
        this MediatorResult<UpdateRedisServiceOutcome> result) =>
        ResultAssertionExtensions.AssertSuccess<UpdateRedisServiceOutcome>(result).AssertSuccess();

    public static DeleteRedisServiceResponse AssertSuccess(
        this MediatorResult<DeleteRedisServiceOutcome> result) =>
        ResultAssertionExtensions.AssertSuccess<DeleteRedisServiceOutcome>(result).AssertSuccess();

    public static RedisDto AssertSuccess(
        this MediatorResult<GetRedisServiceByIdOutcome> result) =>
        ResultAssertionExtensions.AssertSuccess<GetRedisServiceByIdOutcome>(result).AssertSuccess();

    public static PaginatedResponse<RedisDto> AssertSuccess(
        this MediatorResult<GetRedisServicesOutcome> result) =>
        ResultAssertionExtensions.AssertSuccess<GetRedisServicesOutcome>(result).AssertSuccess();

    public static CreateRedisServiceResponse AssertSuccess(
        this CreateRedisServiceOutcome outcome) => outcome switch
    {
        CreateRedisServiceResponse success => success,
        CreateRedisServiceError error => UnexpectedError<CreateRedisServiceResponse>(error)
    };

    public static CreateRedisServiceError AssertFailure(
        this CreateRedisServiceOutcome outcome) => outcome switch
    {
        CreateRedisServiceResponse success => UnexpectedSuccess<CreateRedisServiceError>(success),
        CreateRedisServiceError error => error
    };

    public static UpdateRedisServiceResponse AssertSuccess(
        this UpdateRedisServiceOutcome outcome) => outcome switch
    {
        UpdateRedisServiceResponse success => success,
        UpdateRedisServiceError error => UnexpectedError<UpdateRedisServiceResponse>(error)
    };

    public static UpdateRedisServiceError AssertFailure(
        this UpdateRedisServiceOutcome outcome) => outcome switch
    {
        UpdateRedisServiceResponse success => UnexpectedSuccess<UpdateRedisServiceError>(success),
        UpdateRedisServiceError error => error
    };

    public static DeleteRedisServiceResponse AssertSuccess(
        this DeleteRedisServiceOutcome outcome) => outcome switch
    {
        DeleteRedisServiceResponse success => success,
        DeleteRedisServiceError error => UnexpectedError<DeleteRedisServiceResponse>(error)
    };

    public static DeleteRedisServiceError AssertFailure(
        this DeleteRedisServiceOutcome outcome) => outcome switch
    {
        DeleteRedisServiceResponse success => UnexpectedSuccess<DeleteRedisServiceError>(success),
        DeleteRedisServiceError error => error
    };

    public static RedisDto AssertSuccess(this GetRedisServiceByIdOutcome outcome) => outcome switch
    {
        RedisDto success => success,
        RedisServiceNotFound error => UnexpectedError<RedisDto>(error)
    };

    public static RedisServiceNotFound AssertFailure(
        this GetRedisServiceByIdOutcome outcome) => outcome switch
    {
        RedisDto success => UnexpectedSuccess<RedisServiceNotFound>(success),
        RedisServiceNotFound error => error
    };

    public static PaginatedResponse<RedisDto> AssertSuccess(
        this GetRedisServicesOutcome outcome) => outcome switch
    {
        PaginatedResponse<RedisDto> success => success
    };

    public static StorageProfileDto AssertSuccess(this CreateStorageProfileOutcome outcome) =>
        outcome switch
        {
            StorageProfileDto success => success,
            CreateStorageProfileError error => UnexpectedError<StorageProfileDto>(error)
        };

    public static CreateStorageProfileError AssertFailure(
        this CreateStorageProfileOutcome outcome) => outcome switch
    {
        StorageProfileDto success => UnexpectedSuccess<CreateStorageProfileError>(success),
        CreateStorageProfileError error => error
    };

    public static StorageProfileDto AssertSuccess(this UpdateStorageProfileOutcome outcome) =>
        outcome switch
        {
            StorageProfileDto success => success,
            UpdateStorageProfileError error => UnexpectedError<StorageProfileDto>(error)
        };

    public static UpdateStorageProfileError AssertFailure(
        this UpdateStorageProfileOutcome outcome) => outcome switch
    {
        StorageProfileDto success => UnexpectedSuccess<UpdateStorageProfileError>(success),
        UpdateStorageProfileError error => error
    };

    public static IReadOnlyCollection<StorageProfileDto> AssertSuccess(
        this GetStorageProfilesOutcome outcome) => outcome switch
    {
        IReadOnlyCollection<StorageProfileDto> success => success
    };

    public static IReadOnlyCollection<StorageProfileDto> AssertSuccess(
        this GetAvailableStorageProfilesOutcome outcome) => outcome switch
    {
        IReadOnlyCollection<StorageProfileDto> success => success,
        KubernetesStorageDiscoveryUnavailable error =>
            UnexpectedError<IReadOnlyCollection<StorageProfileDto>>(error)
    };

    public static IReadOnlyCollection<KubernetesStorageClassDto> AssertSuccess(
        this GetKubernetesStorageClassesOutcome outcome) => outcome switch
    {
        IReadOnlyCollection<KubernetesStorageClassDto> success => success,
        KubernetesStorageDiscoveryUnavailable error =>
            UnexpectedError<IReadOnlyCollection<KubernetesStorageClassDto>>(error)
    };

    public static IReadOnlyCollection<KubernetesPersistentVolumeDto> AssertSuccess(
        this GetKubernetesPersistentVolumesOutcome outcome) => outcome switch
    {
        IReadOnlyCollection<KubernetesPersistentVolumeDto> success => success,
        KubernetesStorageDiscoveryUnavailable error =>
            UnexpectedError<IReadOnlyCollection<KubernetesPersistentVolumeDto>>(error)
    };

    public static IReadOnlyCollection<KubernetesPersistentVolumeClaimDto> AssertSuccess(
        this GetKubernetesPersistentVolumeClaimsOutcome outcome) => outcome switch
    {
        IReadOnlyCollection<KubernetesPersistentVolumeClaimDto> success => success,
        KubernetesStorageDiscoveryUnavailable error =>
            UnexpectedError<IReadOnlyCollection<KubernetesPersistentVolumeClaimDto>>(error)
    };

    public static CreateWebServiceResponse AssertSuccess(
        this MediatorResult<CreateWebServiceOutcome> result) =>
        ResultAssertionExtensions.AssertSuccess<CreateWebServiceOutcome>(result).AssertSuccess();

    public static CreateWebServiceError AssertFailure(
        this MediatorResult<CreateWebServiceOutcome> result) =>
        ResultAssertionExtensions.AssertSuccess<CreateWebServiceOutcome>(result).AssertFailure();

    public static UpdateWebServiceResponse AssertSuccess(
        this MediatorResult<UpdateWebServiceOutcome> result) =>
        ResultAssertionExtensions.AssertSuccess<UpdateWebServiceOutcome>(result).AssertSuccess();

    public static RollbackWebServiceResponse AssertSuccess(
        this MediatorResult<RollbackWebServiceOutcome> result) =>
        ResultAssertionExtensions.AssertSuccess<RollbackWebServiceOutcome>(result).AssertSuccess();

    public static RollbackWebServiceError AssertFailure(
        this MediatorResult<RollbackWebServiceOutcome> result) =>
        ResultAssertionExtensions.AssertSuccess<RollbackWebServiceOutcome>(result).AssertFailure();

    public static PlacementSummaryDto AssertSuccess(
        this MediatorResult<DeleteWebServiceDeploymentOutcome> result) =>
        ResultAssertionExtensions.AssertSuccess<DeleteWebServiceDeploymentOutcome>(result).AssertSuccess();

    public static DeleteWebServiceDeploymentError AssertFailure(
        this MediatorResult<DeleteWebServiceDeploymentOutcome> result) =>
        ResultAssertionExtensions.AssertSuccess<DeleteWebServiceDeploymentOutcome>(result).AssertFailure();

    public static PlacementSummaryDto AssertSuccess(
        this MediatorResult<RetryWebServiceDeploymentOutcome> result) =>
        ResultAssertionExtensions.AssertSuccess<RetryWebServiceDeploymentOutcome>(result).AssertSuccess();

    public static PlacementSummaryDto AssertSuccess(
        this MediatorResult<UpdateWebServiceDeploymentOutcome> result) =>
        ResultAssertionExtensions.AssertSuccess<UpdateWebServiceDeploymentOutcome>(result).AssertSuccess();

    public static WebServiceReleaseHistoryDto AssertSuccess(
        this MediatorResult<GetWebServiceReleasesOutcome> result) =>
        ResultAssertionExtensions.AssertSuccess<GetWebServiceReleasesOutcome>(result).AssertSuccess();

    public static WebServiceNotFound AssertFailure(
        this MediatorResult<GetWebServiceReleasesOutcome> result) =>
        ResultAssertionExtensions.AssertSuccess<GetWebServiceReleasesOutcome>(result).AssertFailure();

    public static DeleteWebServiceResponse AssertSuccess(
        this MediatorResult<DeleteWebServiceOutcome> result) =>
        ResultAssertionExtensions.AssertSuccess<DeleteWebServiceOutcome>(result).AssertSuccess();

    public static CreateWebServiceResponse AssertSuccess(this CreateWebServiceOutcome outcome) =>
        outcome switch
        {
            CreateWebServiceResponse success => success,
            CreateWebServiceError error => UnexpectedError<CreateWebServiceResponse>(error)
        };

    public static CreateWebServiceError AssertFailure(this CreateWebServiceOutcome outcome) =>
        outcome switch
        {
            CreateWebServiceResponse success => UnexpectedSuccess<CreateWebServiceError>(success),
            CreateWebServiceError error => error
        };

    public static UpdateWebServiceResponse AssertSuccess(this UpdateWebServiceOutcome outcome) =>
        outcome switch
        {
            UpdateWebServiceResponse success => success,
            UpdateWebServiceError error => UnexpectedError<UpdateWebServiceResponse>(error)
        };

    public static RollbackWebServiceResponse AssertSuccess(this RollbackWebServiceOutcome outcome) =>
        outcome switch
        {
            RollbackWebServiceResponse success => success,
            RollbackWebServiceError error => UnexpectedError<RollbackWebServiceResponse>(error)
        };

    public static RollbackWebServiceError AssertFailure(this RollbackWebServiceOutcome outcome) =>
        outcome switch
        {
            RollbackWebServiceResponse success => UnexpectedSuccess<RollbackWebServiceError>(success),
            RollbackWebServiceError error => error
        };

    public static PlacementSummaryDto AssertSuccess(
        this DeleteWebServiceDeploymentOutcome outcome) => outcome switch
        {
            PlacementSummaryDto success => success,
            DeleteWebServiceDeploymentError error => UnexpectedError<PlacementSummaryDto>(error)
        };

    public static DeleteWebServiceDeploymentError AssertFailure(
        this DeleteWebServiceDeploymentOutcome outcome) => outcome switch
        {
            PlacementSummaryDto success =>
                UnexpectedSuccess<DeleteWebServiceDeploymentError>(success),
            DeleteWebServiceDeploymentError error => error
        };

    public static PlacementSummaryDto AssertSuccess(
        this RetryWebServiceDeploymentOutcome outcome) => outcome switch
        {
            PlacementSummaryDto success => success,
            RetryWebServiceDeploymentError error => UnexpectedError<PlacementSummaryDto>(error)
        };

    public static PlacementSummaryDto AssertSuccess(
        this UpdateWebServiceDeploymentOutcome outcome) => outcome switch
        {
            PlacementSummaryDto success => success,
            UpdateWebServiceDeploymentError error => UnexpectedError<PlacementSummaryDto>(error)
        };

    public static WebServiceReleaseHistoryDto AssertSuccess(
        this GetWebServiceReleasesOutcome outcome) => outcome switch
        {
            WebServiceReleaseHistoryDto success => success,
            WebServiceNotFound error => UnexpectedError<WebServiceReleaseHistoryDto>(error)
        };

    public static WebServiceNotFound AssertFailure(
        this GetWebServiceReleasesOutcome outcome) => outcome switch
        {
            WebServiceReleaseHistoryDto success => UnexpectedSuccess<WebServiceNotFound>(success),
            WebServiceNotFound error => error
        };

    public static DeleteWebServiceResponse AssertSuccess(this DeleteWebServiceOutcome outcome) =>
        outcome switch
        {
            DeleteWebServiceResponse success => success,
            DeleteWebServiceError error => UnexpectedError<DeleteWebServiceResponse>(error)
        };

    private static TSuccess UnexpectedError<TSuccess>(object error)
        where TSuccess : notnull =>
        throw new XunitException(
            $"Expected a successful result, but received {error.GetType().Name}: {error}");

    private static TError UnexpectedSuccess<TError>(object success)
        where TError : notnull =>
        throw new XunitException(
            $"Expected a failed result, but received {success.GetType().Name}.");

    public static T AssertSuccess<T>(this MediatorResult<T> result)
        where T : notnull
        => result.Match(
            outcome => outcome,
            rejected => throw new XunitException(
                $"Expected a handler outcome, but the pipeline returned " +
                $"'{rejected.Reason.GetType().Name}'."));

    public static T AssertHandlerOutcome<T>(this MediatorResult<T> result)
        where T : notnull =>
        result.Match(
            outcome => outcome,
            rejected => throw new XunitException(
                $"Expected a handler outcome, but the pipeline returned " +
                $"'{rejected.Reason.GetType().Name}'."));

    public static RequestValidationRejected AssertValidationRejected<T>(
        this MediatorResult<T> result)
        where T : notnull =>
        result.Match(
            _ => throw new XunitException(
                "Expected request validation to reject the pipeline, but the handler returned an outcome."),
            rejected => rejected.Reason switch
            {
                RequestValidationRejected validation => validation,
                var reason => throw new XunitException(
                    "Expected a request validation rejection, but the pipeline returned " +
                    $"'{reason.GetType().Name}'.")
            });

    public static T AssertSuccess<T, TError>(this Result<T, TError> result)
        where T : notnull
        where TError : notnull =>
        result switch
        {
            Ok<T> success => success.Value,
            Err<TError> error => throw new XunitException(
                $"Expected a successful result, but received " +
                $"{error.Value.GetType().Name}: {error.Value}"),
            _ => throw new XunitException(
                $"Expected a successful result, but received unsupported result type " +
                $"'{result.GetType().Name}'.")
        };

    public static TError AssertFailure<T, TError>(this Result<T, TError> result)
        where T : notnull
        where TError : notnull =>
        result switch
        {
            Err<TError> error => error.Value,
            Ok<T> success => throw new XunitException(
                $"Expected a failed result, but received {success.GetType().Name}."),
            _ => throw new XunitException(
                $"Expected a failed result, but received unsupported result type " +
                $"'{result.GetType().Name}'.")
        };
}

/// <summary>
/// Static-call facade for test code that reads more clearly with assertion functions than
/// extension-method syntax. The assertion behavior remains centralized above.
/// </summary>
public static class ResultAssertions
{
    public static T AssertSuccess<T, TError>(Result<T, TError> result)
        where T : notnull
        where TError : notnull => result.AssertSuccess();

    public static TError AssertFailure<T, TError>(Result<T, TError> result)
        where T : notnull
        where TError : notnull => result.AssertFailure();
}
