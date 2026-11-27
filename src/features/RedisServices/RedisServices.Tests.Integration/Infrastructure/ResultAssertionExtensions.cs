// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using Pole.Features.RedisServices.Application.Common;
using Pole.Features.RedisServices.Application.Endpoints;
using Pole.Features.Request;
using Pole.Mediator;
using Xunit.Sdk;

namespace Pole.Features.RedisServices.Tests.Integration.Infrastructure;

public static class ResultAssertionExtensions
{
    public static T AssertSuccess<T>(this MediatorResult<T> result)
        where T : notnull => result.Match(
        outcome => outcome,
        rejected => throw new XunitException(
            $"Expected a handler outcome, but received '{rejected.Reason.GetType().Name}'."));

    public static RequestValidationRejected AssertValidationRejected<T>(this MediatorResult<T> result)
        where T : notnull => result.Match(
        _ => throw new XunitException("Expected request validation rejection."),
        rejected => rejected.Reason as RequestValidationRejected ?? throw new XunitException(
            $"Expected validation rejection, but received '{rejected.Reason.GetType().Name}'."));

    public static CreateRedisServiceResponse AssertSuccess(this MediatorResult<CreateRedisServiceOutcome> result) =>
        result.AssertSuccess<CreateRedisServiceOutcome>().AssertSuccess();

    public static UpdateRedisServiceResponse AssertSuccess(this MediatorResult<UpdateRedisServiceOutcome> result) =>
        result.AssertSuccess<UpdateRedisServiceOutcome>().AssertSuccess();

    public static DeleteRedisServiceResponse AssertSuccess(this MediatorResult<DeleteRedisServiceOutcome> result) =>
        result.AssertSuccess<DeleteRedisServiceOutcome>().AssertSuccess();

    public static RedisDto AssertSuccess(this MediatorResult<GetRedisServiceByIdOutcome> result) =>
        result.AssertSuccess<GetRedisServiceByIdOutcome>().AssertSuccess();

    public static PaginatedResponse<RedisDto> AssertSuccess(this MediatorResult<GetRedisServicesOutcome> result) =>
        result.AssertSuccess<GetRedisServicesOutcome>().AssertSuccess();

    public static CreateRedisServiceResponse AssertSuccess(this CreateRedisServiceOutcome outcome) => outcome switch
    {
        CreateRedisServiceResponse value => value, var error => Fail<CreateRedisServiceResponse>(error),
    };

    public static UpdateRedisServiceResponse AssertSuccess(this UpdateRedisServiceOutcome outcome) => outcome switch
    {
        UpdateRedisServiceResponse value => value, var error => Fail<UpdateRedisServiceResponse>(error),
    };

    public static DeleteRedisServiceResponse AssertSuccess(this DeleteRedisServiceOutcome outcome) => outcome switch
    {
        DeleteRedisServiceResponse value => value, var error => Fail<DeleteRedisServiceResponse>(error),
    };

    public static RedisDto AssertSuccess(this GetRedisServiceByIdOutcome outcome) =>
        outcome switch { RedisDto value => value, var error => Fail<RedisDto>(error) };

    public static RedisServiceNotFound AssertFailure(this GetRedisServiceByIdOutcome outcome) => outcome switch
    {
        RedisServiceNotFound value => value, var success => Fail<RedisServiceNotFound>(success),
    };

    public static PaginatedResponse<RedisDto> AssertSuccess(this GetRedisServicesOutcome outcome) =>
        outcome switch { PaginatedResponse<RedisDto> value => value };

    private static T Fail<T>(object value) =>
        throw new XunitException($"Unexpected result: {JsonSerializer.Serialize(value, value.GetType())}");
}
