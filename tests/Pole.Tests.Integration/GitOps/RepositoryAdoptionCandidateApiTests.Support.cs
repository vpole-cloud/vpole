// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using Pole.Features.GitOps.Application.Endpoints.Sources;
using Pole.Tests.Integration.Http;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public abstract partial class RepositorySourceApiTestBase
{
    private protected async Task<AdoptionCandidatePage> Candidates(string source, string query)
    {
        using var response = await Client.GetAsync($"{Root}/{source}/adoption-candidates?{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AdoptionCandidatePage>())!;
    }
}
