using System.Collections.Immutable;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Runtime.Gateway;
using Xunit;

namespace Pole.Features.Functions.Tests;

public sealed class FunctionHttpRouteMatcherTests
{
    private static readonly ImmutableList<FunctionHttpRouteBinding> Routes =
    [
        new(
            "literal-trigger",
            "literal",
            "orders/special",
            ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "GET"),
            FunctionExposure.Public),
        new(
            "parameter-trigger",
            "by-id",
            "orders/{id}",
            ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "GET", "POST"),
            FunctionExposure.Public),
        new(
            "internal-trigger",
            "internal",
            "orders/{id}",
            ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "GET"),
            FunctionExposure.Internal)
    ];

    [Theory]
    [InlineData("orders/special", "GET", FunctionExposure.Public, "literal")]
    [InlineData("orders/42", "post", FunctionExposure.Public, "by-id")]
    [InlineData("/orders/42/", "GET", FunctionExposure.Internal, "internal")]
    public void Match_respects_literals_parameters_methods_and_exposure(
        string path,
        string method,
        FunctionExposure exposure,
        string expectedHandler)
    {
        var match = FunctionHttpRouteMatcher.Match(Routes, path, method, exposure);

        Assert.NotNull(match);
        Assert.Equal(expectedHandler, match.HandlerName);
    }

    [Theory]
    [InlineData("orders", "GET", FunctionExposure.Public)]
    [InlineData("orders/42/extra", "GET", FunctionExposure.Public)]
    [InlineData("orders/42", "DELETE", FunctionExposure.Public)]
    [InlineData("orders/..", "GET", FunctionExposure.Public)]
    [InlineData("orders/%2Fadmin", "GET", FunctionExposure.Public)]
    [InlineData("orders/%5Cadmin", "GET", FunctionExposure.Public)]
    public void Match_rejects_non_matching_routes(
        string path,
        string method,
        FunctionExposure exposure) =>
        Assert.Null(FunctionHttpRouteMatcher.Match(Routes, path, method, exposure));

    [Fact]
    public void Earlier_literal_segment_wins_when_parameter_counts_are_equal()
    {
        var routes = new[]
        {
            new FunctionHttpRouteBinding(
                "parameter-first",
                "ParameterFirst",
                "{category}/details",
                ImmutableHashSet.Create("GET"),
                FunctionExposure.Public),
            new FunctionHttpRouteBinding(
                "literal-first",
                "LiteralFirst",
                "orders/{id}",
                ImmutableHashSet.Create("GET"),
                FunctionExposure.Public)
        };

        var match = FunctionHttpRouteMatcher.Match(routes, "orders/details", "GET", FunctionExposure.Public);

        Assert.NotNull(match);
        Assert.Equal("LiteralFirst", match.HandlerName);
    }
}
