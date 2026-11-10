# Pole Private Access CLI

The CLI discovers the public OIDC issuer and client ID from the selected Pole server, then signs in with Authorization
Code + PKCE (S256) in the system browser. It never collects identity-provider or database passwords. The callback is
fixed at `http://127.0.0.1:48763/callback`; register that exact URI in the public CLI OIDC client.

Sign in to a Pole installation, then choose a tenant when the account has more than one:

```sh
pole login --server https://pole.example
pole login
pole tenant list
pole tenant select tenant-1
pole status
```

Forward a provider resource by its short kind and immutable name:

```sh
pole forward postgres/orders
pole forward redis/cache --deployment <deployment-id> --endpoint primary --local-port 16379
```

Pole resolves eligible grants, deployments, and endpoints for the active tenant. When several routes match, the CLI
prompts in a terminal; non-interactive calls print the choices and fail until the route is unambiguous. `--local-port`
defaults to `0`, which asks the operating system for a free port. The listener binds only to `127.0.0.1` and prints the
selected tenant, resource, endpoint, and local address. Database credentials remain the responsibility of the database
client.

`pole logout` removes the saved local token and server profile. It does not revoke the identity-provider browser
session. Tokens are saved below the current user's data directory (`%LOCALAPPDATA%\\Pole\\cli` on Windows or
`$XDG_DATA_HOME/pole/cli` on Unix, defaulting to `~/.local/share/pole/cli`). The CLI restricts the directory and files
to the current user and rejects links/reparse points. Active tenant context is stored per server and OIDC account
subject. Forwarding can refresh a token silently but never opens the browser; if refresh has expired, run `pole login`.
