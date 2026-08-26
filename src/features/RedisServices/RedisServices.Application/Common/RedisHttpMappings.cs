using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Pole.ASP.Helpers;
using Pole.Features.RedisServices.Application.Endpoints;
using Pole.Features.RedisServices.Application.Persistence;
using Pole.Features.RedisServices.Application.Persistence.Lifecycle;
using Pole.Features.RedisServices.Application.Persistence.Mutations;

namespace Pole.Features.RedisServices.Application.Common;

public static class RedisHttpMappings
{
    public static IReadOnlyList<HttpProblemMapping> All { get; } =
        Array.AsReadOnly<HttpProblemMapping>(
        [
            HttpProblemMapping.For<RedisServiceNotFound>(MapNotFound),
            HttpProblemMapping.For<RedisUpdateStateError>(MapUpdateState),
            HttpProblemMapping.For<RedisDeletionStateError>(MapDeletionState),
            HttpProblemMapping.For<RedisUpdateConflict>(MapUpdateConflict),
            HttpProblemMapping.For<RedisDeletionConflict>(MapDeletionConflict)
        ]);

    public static IServiceCollection AddRedisHttpMappings(
        this IServiceCollection services) =>
        services.AddHttpProblemMappings(All);

    private static HttpProblem MapNotFound(RedisServiceNotFound error) => Problem(
        HttpStatusCode.NotFound,
        "redis-services.not-found",
        "Redis service not found",
        ("redisId", error.RedisId));

    private static HttpProblem MapUpdateState(
        RedisUpdateStateError error) => error switch
    {
        RedisServiceNotFound notFound => MapNotFound(notFound),
        RedisUpdateUnavailable unavailable => Problem(
            HttpStatusCode.Conflict,
            "redis-services.update-unavailable",
            "Redis service cannot be updated in its current lifecycle",
            ("redisId", unavailable.RedisId)),
        RedisStorageProfileChangeNotAllowed rejected => Problem(
            HttpStatusCode.UnprocessableEntity,
            "redis-services.storage-profile-change-not-allowed",
            "Storage profile cannot be changed for an existing Redis service",
            ("redisId", rejected.RedisId))
    };

    private static HttpProblem MapDeletionState(
        RedisDeletionStateError error) => error switch
    {
        RedisServiceNotFound notFound => MapNotFound(notFound),
        RedisDeletionResourceDeploymentMissing missing => Problem(
            HttpStatusCode.Conflict,
            "redis-services.deletion-deployment-missing",
            "Deleting Redis service has no resource deployment",
            ("redisId", missing.RedisId))
    };

    private static HttpProblem MapUpdateConflict(RedisUpdateConflict error) => Problem(
        HttpStatusCode.Conflict,
        "redis-services.update.concurrent-change",
        "Redis service changed concurrently",
        ("redisId", error.RedisId));

    private static HttpProblem MapDeletionConflict(RedisDeletionConflict error) => Problem(
        HttpStatusCode.Conflict,
        "redis-services.deletion.concurrent-change",
        "Redis service changed concurrently while deletion was being accepted",
        ("redisId", error.RedisId));

    private static HttpProblem Problem(
        HttpStatusCode status,
        string code,
        string title,
        params (string Name, object Value)[] extensions) =>
        new(
            (int)status,
            PoleProblemTypeId.FromCode(code),
            title,
            code,
            extensions: extensions.ToDictionary(
                extension => extension.Name,
                extension => extension.Value));
}
