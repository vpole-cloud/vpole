// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.GitProviders.Domain;
using Pole.Features.SourceBuilds.Domain;
using Pole.Features.WebServices.Application.Endpoints;
using Pole.Tests.Integration.Infrastructure;

namespace Pole.Tests.Integration.SourceBuilds;

internal static class SourceBuildTestData
{
    public static async Task SeedRepositoryAsync(
        MongoTestStore store,
        SourceBuildConfiguration configuration,
        string actorUserId)
    {
        var connection = GitProviderConnectionFactory.Create(
            configuration.TenantId,
            "application-github",
            GitProviderKind.GitHub,
            "installation-" + configuration.Id,
            "account",
            "owner",
            GitProviderAccountType.User,
            GitProviderRepositorySelection.All,
            actorUserId,
            GitProviderConnectionFactory.DefaultOptions);
        await store.GitProviderConnections.InsertOneAsync(connection);
        await store.SourceRepositories.InsertOneAsync(Repository(configuration, connection.Id));
    }

    public static CreateWebServiceRequest CreateTargetRequest(IntegrationTestData data, string groupId)
    {
        var request = data.CreateWebServiceRequest(groupId);
        return request with
        {
            Body = request.Body with
            {
                CreationMode = WebServiceCreationMode.SourceBuild,
                Spec = request.Body.Spec with { Image = null },
            },
        };
    }

    private static SourceRepository Repository(SourceBuildConfiguration configuration, string connectionId) =>
        SourceRepositoryFactory.Create(
            configuration.TenantId,
            "application",
            connectionId,
            "9001",
            "owner/application",
            "https://github.com/owner/application",
            true,
            "main",
            new SourceRepositoryFactory.CreateOptions(
                configuration.SourceRepositoryId,
                configuration.CreatedAtUtc,
                configuration.CreatedAtUtc));
}
