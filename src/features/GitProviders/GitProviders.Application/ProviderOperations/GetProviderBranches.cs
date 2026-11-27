// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.Extensions.Logging;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Contracts;
using Pole.Features.GitProviders.Domain;

namespace Pole.Features.GitProviders.Application.ProviderOperations;

public union GetProviderBranchesOutcome(ProviderPage<ProviderBranch>, GitProviderRepositoryAccessError);

public delegate Task<GetProviderBranchesOutcome> GetProviderBranches(
    GitProviderConnection connection,
    string repositoryId,
    int page,
    int pageSize,
    CancellationToken cancellationToken = default);

internal sealed class GitHubGetProviderBranches(
    IGitHubAppClient gitHubClient,
    ILogger<GitHubGetProviderBranches> logger
)
{
    public async Task<GetProviderBranchesOutcome> InvokeAsync(
        GitProviderConnection connection,
        string repositoryId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var result = await GitProviderOperation.ExecuteAsync(
            connection,
            () => gitHubClient.GetBranchesAsync(
                connection.ExternalInstallationId,
                repositoryId,
                page,
                pageSize,
                cancellationToken),
            logger);
        return result switch
        {
            ProviderPage<ProviderBranch> branchPage => branchPage,
            GitProviderRepositoryAccessError error => error,
        };
    }
}
