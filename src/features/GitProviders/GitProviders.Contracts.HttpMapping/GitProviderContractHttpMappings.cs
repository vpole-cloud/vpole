// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using Pole.ASP.Helpers;

namespace Pole.Features.GitProviders.Contracts.HttpMapping;

public static class GitProviderContractHttpMappings
{
    public static HttpProblem MapSourceRepositoryBuildConnectionNotFound(
        SourceRepositoryBuildConnectionNotFound missing) => Problem(
        HttpStatusCode.NotFound,
        "git-providers.connections.not-found",
        "Git provider connection not found");

    public static HttpProblem MapSourceRepositoryRemoteNotFound(SourceRepositoryRemoteNotFound _) => Problem(
        HttpStatusCode.NotFound,
        "git-providers.source-repositories.remote-not-found",
        "The remote repository was not found");

    public static HttpProblem MapSourceRepositoryBranchNotFound(SourceRepositoryBranchNotFound missing) => Problem(
        HttpStatusCode.NotFound,
        "git-providers.source-repositories.branch-not-found",
        "The repository branch was not found");

    public static HttpProblem MapSourceRepositoryAccessDenied(SourceRepositoryAccessDenied _) => Problem(
        HttpStatusCode.Forbidden,
        "git-providers.source-repositories.access-denied",
        "Current repository access is unavailable");

    public static HttpProblem MapSourceRepositoryUnavailable(SourceRepositoryUnavailable _) => Problem(
        HttpStatusCode.ServiceUnavailable,
        "git-providers.source-repositories.unavailable",
        "The repository provider is temporarily unavailable");

    private static HttpProblem Problem(
        HttpStatusCode status,
        string code,
        string title) => new((int)status, PoleProblemTypeId.FromCode(code), title, code);

    public static HttpProblem MapSourceRepositoryBuildNotFound(SourceRepositoryBuildNotFound notFound) => new(
        (int)HttpStatusCode.NotFound,
        PoleProblemTypeId.FromCode("git-providers.source-repositories.not-found"),
        "Source repository not found",
        "git-providers.source-repositories.not-found",
        extensions: new Dictionary<string, object>
        {
            ["sourceRepositoryId"] = notFound.SourceRepositoryId,
        });
}
