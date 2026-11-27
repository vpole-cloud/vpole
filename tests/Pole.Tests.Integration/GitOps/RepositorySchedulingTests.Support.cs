// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;

namespace Pole.Tests.Integration.GitOps;

public abstract partial class DeclarativePipelineTestBase
{
    private protected GitRepositorySources SourceStore => Services.GetRequiredService<GitRepositorySources>();

    private protected GitSourceScheduling Scheduler => Services.GetRequiredService<GitSourceScheduling>();

    private protected IMongoCollection<GitSynchronizationState> Synchronization =>
        Services.GetRequiredService<IMongoCollection<GitSynchronizationState>>();

    private protected Task<GitSynchronizationState> State() => SourceStore.StateAsync(Data.TenantId, source, default);

    private protected RepositorySyncSignal Push(string id) => new(
        Data.TenantId,
        repository.Provider,
        TestRepositorySnapshots.ConnectionId,
        source,
        "main",
        id);
}
