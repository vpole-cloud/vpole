using System.Net;
using Pole.Tests.Integration.Http;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitOps.Application.Endpoints.Sources;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Pole.Features.Secrets.Domain;
using Pole.Features.Tenants.Domain;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Domain;
using Pole.Tests.Integration.Infrastructure;
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
