// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Cli.Authentication;

public sealed class CliLoginRequiredException()
    : InvalidOperationException("Run 'pole login' to authenticate with the saved Pole server.");

public sealed class OidcAccessTokenProvider(
    Uri issuer,
    string clientId,
    Uri server,
    OidcAuthenticator authenticator,
    CliTokenStore tokenStore
)
{
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromSeconds(45);
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool loaded;
    private OidcTokens? tokens;

    public async Task<string> GetAccessTokenAsync(
        CancellationToken cancellationToken,
        bool allowInteractiveLogin = true)
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

            if (tokens is null)
            {
                if (!allowInteractiveLogin)
                    throw new CliLoginRequiredException();
                tokens = await authenticator.LoginAsync(issuer, clientId, cancellationToken);
            }

            await tokenStore.SaveAsync(issuer, clientId, server, tokens, cancellationToken);
            return tokens.AccessToken;
        }
        finally
        {
            gate.Release();
        }
    }
}
