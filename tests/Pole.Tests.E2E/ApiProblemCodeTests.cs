using System.Net;
using Pole.Tests.E2E.Infrastructure;
using Xunit;

namespace Pole.Tests.E2E;

public sealed class ApiProblemCodeTests
{
    [Theory]
    [InlineData("{\"code\":\"resource-deployments.changed\",\"detail\":\"private-canary\",\"token\":\"private-canary\"}", "resource-deployments.changed")]
    [InlineData("{\"code\":\"web-services.update.concurrent-change\"}", "web-services.update.concurrent-change")]
    [InlineData("{\"detail\":\"private-canary\"}", "unavailable")]
    [InlineData("{\"code\":42}", "unavailable")]
    [InlineData("{\"code\":\"Bearer private-canary\"}", "unavailable")]
    [InlineData("{\"code\":\"private-canary\"}", "unavailable")]
    [InlineData("[]", "unavailable")]
    [InlineData("not-json private-canary", "unavailable")]
    public async Task Returns_only_a_bounded_problem_identifier(string body, string expected)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent(body) };

        Assert.Equal(expected, await ApiProblemCode.ReadAsync(response, CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_oversized_content_even_when_it_starts_with_a_problem_code()
    {
        var body = "{\"code\":\"resource-deployments.changed\",\"detail\":\"" + new string('x', 16_384) + "\"}";
        using var response = new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent(body) };

        Assert.Equal("unavailable", await ApiProblemCode.ReadAsync(response, CancellationToken.None));
    }
}
