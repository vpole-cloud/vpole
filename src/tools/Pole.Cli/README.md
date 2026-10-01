# Pole Private Access CLI

The CLI uses the public `pole.control-plane.cli` Keycloak client with Authorization Code + PKCE (S256). Its callback is fixed at `http://127.0.0.1:48763/callback`; keep that exact URI registered in Keycloak and make the port available when signing in. The issuer and Pole server are explicit configuration, and must match the deployment. HTTP is accepted only for loopback development; forwarding always requires an HTTPS Pole server.

Sign in and store a refreshable token for the current user:

```sh
dotnet run --project src/tools/Pole.Cli -- login \
  --server https://pole.example \
  --issuer https://identity.example/realms/pole.control-plane \
  --client-id pole.control-plane.cli
```

Start a loopback-only forwarder for an active grant:

```sh
dotnet run --project src/tools/Pole.Cli -- forward \
  --server https://pole.example \
  --issuer https://identity.example/realms/pole.control-plane \
  --client-id pole.control-plane.cli \
  --tenant <tenant-id> \
  --grant <grant-id> \
  --port 15432
```

The grant selects its deployment and endpoint; the CLI never accepts a destination host or port. It prints the selected grant resource and listens only on `127.0.0.1`. Every accepted local TCP connection requests a fresh single-use session ticket and opens a new WebSocket stream. Press Ctrl+C to stop.

Tokens are saved below the current user's data directory (`%LOCALAPPDATA%\\Pole\\cli` on Windows or `$XDG_DATA_HOME/pole/cli` on Unix, defaulting to `~/.local/share/pole/cli`). The CLI restricts the directory and token file to the current user and rejects links/reparse points. Tokens are bound to the issuer, client ID, and Pole server authority; changing any requires signing in again.
