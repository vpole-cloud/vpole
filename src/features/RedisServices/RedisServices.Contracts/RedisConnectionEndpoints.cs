// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Workloads;

namespace Pole.Features.RedisServices.Contracts;

public sealed record RedisConnectionEndpoint(
    string Id,
    int TcpPort
);

public static class RedisConnectionEndpoints
{
    public const string RuntimeEndpointKind = "Redis";

    public static ResourceTypeId ResourceType { get; } = ResourceTypeId.Create("pole/redis");

    public static RedisConnectionEndpoint Redis { get; } = new("redis", 6379);

    public static RedisConnectionEndpoint? Resolve(string endpointId) =>
        string.Equals(endpointId, Redis.Id, StringComparison.Ordinal) ? Redis : null;
}
