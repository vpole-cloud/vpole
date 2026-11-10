namespace Pole.Server.Extensions;

public static class PoleCliDiscovery
{
    public static IEndpointRouteBuilder MapPoleCliDiscovery(
        this IEndpointRouteBuilder app,
        IConfiguration configuration)
    {
        app.MapGet(
                "/.well-known/pole-cli",
                () =>
                {
                    var issuer = configuration["Cli:Issuer"];
                    if (!Uri.TryCreate(issuer, UriKind.Absolute, out var address) ||
                        (address.Scheme != Uri.UriSchemeHttps &&
                            (address.Scheme != Uri.UriSchemeHttp || !address.IsLoopback)) ||
                        address.UserInfo.Length != 0 ||
                        address.Query.Length != 0 || address.Fragment.Length != 0)
                        return Results.Problem("CLI authentication is not configured.", statusCode: 503);

                    return Results.Ok(
                        new PoleCliDiscoveryDocument(address.AbsoluteUri.TrimEnd('/'), "pole.control-plane.cli"));
                })
            .AllowAnonymous()
            .WithName("GetPoleCliDiscovery")
            .Produces<PoleCliDiscoveryDocument>();

        return app;
    }

    private sealed record PoleCliDiscoveryDocument(
        string Issuer,
        string ClientId
    );
}
