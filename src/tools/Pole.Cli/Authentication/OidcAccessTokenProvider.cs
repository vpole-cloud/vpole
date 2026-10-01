namespace Pole.Cli.Authentication;

public sealed class OidcAccessTokenProvider(
    Uri issuer,
    string clientId,
    Uri server,
    OidcAuthenticator authenticator,
    CliTokenStore tokenStore)
{
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromSeconds(45);
    private readonly SemaphoreSlim gate = new(1, 1);
    private OidcTokens? tokens;
    private bool loaded;

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!loaded)
            {
                tokens = await tokenStore.LoadAsync(issuer, clientId, server, cancellationToken);
                loaded = true;
            }

            if (tokens is { } cached && cached.ExpiresAtUtc > DateTimeOffset.UtcNow + RefreshSkew)
                return cached.AccessToken;

            if (tokens is { } expired)
                tokens = await authenticator.RefreshAsync(issuer, clientId, expired, cancellationToken);

            tokens ??= await authenticator.LoginAsync(issuer, clientId, cancellationToken);
            await tokenStore.SaveAsync(issuer, clientId, server, tokens, cancellationToken);
            return tokens.AccessToken;
        }
        finally
        {
            gate.Release();
        }
    }
}
