using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application.Endpoints;
using Pole.Features.Request;
using Pole.Mediator;
using Pole.ResultType;
using Xunit.Sdk;

namespace Pole.Features.RedisServices.Tests.Integration.Infrastructure;

public static class ResultAssertionExtensions
{
    public static T AssertSuccess<T>(this MediatorResult<T> result) where T : notnull =>
        result.Match(outcome => outcome, rejected => throw new XunitException($"Expected a handler outcome, but received '{rejected.Reason.GetType().Name}'."));

    public static RequestValidationRejected AssertValidationRejected<T>(this MediatorResult<T> result) where T : notnull =>
        result.Match(_ => throw new XunitException("Expected request validation rejection."), rejected => rejected.Reason as RequestValidationRejected ?? throw new XunitException($"Expected validation rejection, but received '{rejected.Reason.GetType().Name}'."));

    public static CreateRedisServiceResponse AssertSuccess(this MediatorResult<CreateRedisServiceOutcome> result) => AssertSuccess<CreateRedisServiceOutcome>(result).AssertSuccess();
    public static UpdateRedisServiceResponse AssertSuccess(this MediatorResult<UpdateRedisServiceOutcome> result) => AssertSuccess<UpdateRedisServiceOutcome>(result).AssertSuccess();
    public static DeleteRedisServiceResponse AssertSuccess(this MediatorResult<DeleteRedisServiceOutcome> result) => AssertSuccess<DeleteRedisServiceOutcome>(result).AssertSuccess();
    public static RedisDto AssertSuccess(this MediatorResult<GetRedisServiceByIdOutcome> result) => AssertSuccess<GetRedisServiceByIdOutcome>(result).AssertSuccess();
    public static PaginatedResponse<RedisDto> AssertSuccess(this MediatorResult<GetRedisServicesOutcome> result) => AssertSuccess<GetRedisServicesOutcome>(result).AssertSuccess();

    public static CreateRedisServiceResponse AssertSuccess(this CreateRedisServiceOutcome outcome) => outcome switch { CreateRedisServiceResponse value => value, var error => Fail<CreateRedisServiceResponse>(error) };
    public static UpdateRedisServiceResponse AssertSuccess(this UpdateRedisServiceOutcome outcome) => outcome switch { UpdateRedisServiceResponse value => value, var error => Fail<UpdateRedisServiceResponse>(error) };
    public static DeleteRedisServiceResponse AssertSuccess(this DeleteRedisServiceOutcome outcome) => outcome switch { DeleteRedisServiceResponse value => value, var error => Fail<DeleteRedisServiceResponse>(error) };
    public static RedisDto AssertSuccess(this GetRedisServiceByIdOutcome outcome) => outcome switch { RedisDto value => value, var error => Fail<RedisDto>(error) };
    public static RedisServiceNotFound AssertFailure(this GetRedisServiceByIdOutcome outcome) => outcome switch { RedisServiceNotFound value => value, var success => Fail<RedisServiceNotFound>(success) };
    public static PaginatedResponse<RedisDto> AssertSuccess(this GetRedisServicesOutcome outcome) => outcome switch { PaginatedResponse<RedisDto> value => value };

    public static T AssertSuccess<T, TError>(this Result<T, TError> result) where T : notnull where TError : notnull => result switch { Ok<T> value => value.Value, Err<TError> error => Fail<T>(error.Value), _ => throw new XunitException("Unsupported result.") };
    public static TError AssertFailure<T, TError>(this Result<T, TError> result) where T : notnull where TError : notnull => result switch { Err<TError> value => value.Value, Ok<T> success => Fail<TError>(success.Value), _ => throw new XunitException("Unsupported result.") };

    private static T Fail<T>(object value) => throw new XunitException($"Unexpected result: {System.Text.Json.JsonSerializer.Serialize(value, value.GetType())}");
}
