using System.Net;
using Pole.ASP.Helpers;
using Pole.Features.RedisServices.Application.Endpoints;
using Pole.Features.RedisServices.Application.Lifecycle;
using Pole.Features.RedisServices.Application.Services;

namespace Pole.Features.RedisServices.Application.Common;

public static class RedisHttpMappings
{
    public static HttpProblem MapNameAlreadyExists(RedisServiceNameAlreadyExists error) => Problem(
        HttpStatusCode.Conflict,
        "redis.name-already-exists",
        "Redis service name already exists in this tenant",
        ("name", error.Name));

    public static HttpProblem MapNotFound(RedisServiceNotFound error) => Problem(
        HttpStatusCode.NotFound,
        "redis-services.not-found",
        "Redis service not found",
        ("redisId", error.RedisId));

    public static HttpProblem MapUpdateUnavailable(RedisUpdateUnavailable unavailable) => Problem(
        HttpStatusCode.Conflict,
        "redis-services.update-unavailable",
        "Redis service cannot be updated in its current lifecycle",
        ("redisId", unavailable.RedisId));

    public static HttpProblem MapStorageProfileChangeNotAllowed(RedisStorageProfileChangeNotAllowed rejected) =>
        Problem(
            HttpStatusCode.UnprocessableEntity,
            "redis-services.storage-profile-change-not-allowed",
            "Storage profile cannot be changed for an existing Redis service",
            ("redisId", rejected.RedisId));

    public static HttpProblem MapDeletionResourceDeploymentMissing(RedisDeletionResourceDeploymentMissing missing) =>
        Problem(
            HttpStatusCode.Conflict,
            "redis-services.deletion-deployment-missing",
            "Deleting Redis service has no resource deployment",
            ("redisId", missing.RedisId));

    public static HttpProblem MapStorageResizeUnsupported(RedisStorageResizeUnsupported rejected) => Problem(
        HttpStatusCode.UnprocessableEntity,
        "redis-services.storage-resize-unsupported",
        "Native Redis storage size is fixed after creation",
        ("redisId", rejected.RedisId));

    public static HttpProblem MapUpdateConflict(RedisUpdateConflict error) => Problem(
        HttpStatusCode.Conflict,
        "redis-services.update.concurrent-change",
        "Redis service changed concurrently",
        ("redisId", error.RedisId));

    public static HttpProblem MapDeletionConflict(RedisDeletionConflict error) => Problem(
        HttpStatusCode.Conflict,
        "redis-services.deletion.concurrent-change",
        "Redis service changed concurrently while deletion was being accepted",
        ("redisId", error.RedisId));

    public static HttpProblem MapSecretBindingRemovalRequired(RedisSecretBindingRemovalRequired error) => Problem(
        HttpStatusCode.Conflict,
        "redis-services.secret-bindings-removal-required",
        "Remove Git-owned Secret bindings before deleting the Redis service",
        ("redisId", error.RedisId));

    public static HttpProblem MapGitOwned(RedisGitOwnedConfiguration error) => Problem(
        HttpStatusCode.Conflict,
        "redis-services.git-owned-configuration",
        "Redis configuration is managed by GitOps",
        ("redisId", error.RedisId));

    private static HttpProblem Problem(
        HttpStatusCode status,
        string code,
        string title,
        params (string Name, object Value)[] extensions) => new(
        (int)status,
        PoleProblemTypeId.FromCode(code),
        title,
        code,
        extensions: extensions.ToDictionary(extension => extension.Name, extension => extension.Value));
}
