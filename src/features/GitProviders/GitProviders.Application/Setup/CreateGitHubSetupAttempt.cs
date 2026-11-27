// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.GitProviders.Domain;

namespace Pole.Features.GitProviders.Application.Setup;

public delegate Task CreateGitHubSetupAttempt(
    GitHubSetupAttempt attempt,
    CancellationToken cancellationToken = default);

internal sealed class MongoCreateGitHubSetupAttempt(
    IMongoCollection<GitHubSetupAttempt> gitHubSetupAttempts
)
{
    public Task InvokeAsync(GitHubSetupAttempt attempt, CancellationToken cancellationToken = default) =>
        gitHubSetupAttempts.InsertOneAsync(attempt, cancellationToken: cancellationToken);
}
