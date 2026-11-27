// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace Pole.Cli.Authentication;

public sealed class CliTokenStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string tokenPath;

    public CliTokenStore() : this(GetTokenPath())
    {
    }

    internal CliTokenStore(string tokenPath) => this.tokenPath = tokenPath;

    public async Task SaveAsync(
        Uri issuer,
        string clientId,
        Uri server,
        OidcTokens tokens,
        CancellationToken cancellationToken)
    {
        var authority = GetAuthority(server);
        var path = tokenPath;
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        ProtectDirectory(directory);
        RejectReparsePoint(path);

        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

            await using (var stream = new FileStream(temporaryPath, options))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    new StoredTokens(issuer.AbsoluteUri, clientId, authority, tokens),
                    JsonOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, path, true);
            ProtectFile(path);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public async Task<OidcTokens?> LoadAsync(
        Uri issuer,
        string clientId,
        Uri server,
        CancellationToken cancellationToken)
    {
        var path = tokenPath;
        if (!File.Exists(path))
            return null;

        var directory = Path.GetDirectoryName(path)!;
        ProtectDirectory(directory);
        ProtectFile(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var stored = await JsonSerializer.DeserializeAsync<StoredTokens>(stream, JsonOptions, cancellationToken);
        return stored is not null && stored.Issuer == issuer.AbsoluteUri && stored.ClientId == clientId &&
            stored.ServerAuthority == GetAuthority(server)
                ? stored.Tokens
                : null;
    }

    public async Task SaveProfileAsync(
        Uri server,
        PoleServerIdentity identity,
        CancellationToken cancellationToken)
    {
        ValidateServer(server);
        var path = GetProfilePath();
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        ProtectDirectory(directory);
        RejectReparsePoint(path);

        var activeTenants = await ReadActiveTenantsAsync(path, cancellationToken);
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

            await using (var stream = new FileStream(temporaryPath, options))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    new StoredProfile(
                        GetAuthority(server),
                        identity.Issuer.AbsoluteUri,
                        identity.ClientId,
                        activeTenants),
                    JsonOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, path, true);
            ProtectFile(path);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public async Task<(Uri Server, PoleServerIdentity Identity)?> LoadProfileAsync(CancellationToken cancellationToken)
    {
        var path = GetProfilePath();
        if (!File.Exists(path))
            return null;

        var directory = Path.GetDirectoryName(path)!;
        ProtectDirectory(directory);
        ProtectFile(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var stored = await JsonSerializer.DeserializeAsync<StoredProfile>(stream, JsonOptions, cancellationToken);
        if (stored is null || !Uri.TryCreate(stored.Server, UriKind.Absolute, out var server) ||
            !Uri.TryCreate(stored.Issuer, UriKind.Absolute, out var issuer))
            return null;

        ValidateServer(server);
        OidcAuthenticator.ValidateDiscoveredIssuer(issuer);
        if (string.IsNullOrWhiteSpace(stored.ClientId) || stored.ClientId.Length > 256 ||
            stored.ClientId.Any(char.IsControl))
            return null;
        return (server, new PoleServerIdentity(issuer, stored.ClientId));
    }

    public async Task<string?> GetActiveTenantAsync(string accountKey, CancellationToken cancellationToken)
    {
        var stored = await ReadProfileAsync(cancellationToken);
        return stored?.ActiveTenants?.GetValueOrDefault(accountKey);
    }

    public async Task SaveActiveTenantAsync(
        string accountKey,
        string tenantId,
        CancellationToken cancellationToken)
    {
        var path = GetProfilePath();
        RejectReparsePoint(path);
        var stored = await ReadProfileAsync(cancellationToken) ??
            throw new InvalidOperationException(
                "No Pole server profile is saved. Run 'pole login --server <url>' first.");
        var activeTenants = stored.ActiveTenants ?? new Dictionary<string, string>(StringComparer.Ordinal);
        activeTenants[accountKey] = tenantId;
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        ProtectDirectory(directory);
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporaryPath, options))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    stored with { ActiveTenants = activeTenants },
                    JsonOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, path, true);
            ProtectFile(path);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public async Task RemoveActiveTenantAsync(string accountKey, CancellationToken cancellationToken)
    {
        var path = GetProfilePath();
        var stored = await ReadProfileAsync(cancellationToken);
        if (stored?.ActiveTenants?.Remove(accountKey) != true)
            return;
        await SaveStoredProfileAsync(path, stored, cancellationToken);
    }

    public void Clear()
    {
        var profilePath = GetProfilePath();
        RejectReparsePoint(profilePath);
        RejectReparsePoint(tokenPath);
        if (File.Exists(profilePath))
            File.Delete(profilePath);
        if (File.Exists(tokenPath))
            File.Delete(tokenPath);
    }

    public static string GetTokenPath()
    {
        if (OperatingSystem.IsWindows())
        {
            var directory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException(
                    "Secure token storage is unavailable because the Windows local application data directory could not be resolved.");
            return Path.Combine(directory, "Pole", "cli", "tokens.json");
        }

        var dataDirectory = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(dataDirectory))
            dataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "share");
        return Path.Combine(dataDirectory, "pole", "cli", "tokens.json");
    }

    private string GetProfilePath() => Path.Combine(Path.GetDirectoryName(tokenPath)!, "server.json");

    private async Task<StoredProfile?> ReadProfileAsync(CancellationToken cancellationToken)
    {
        var path = GetProfilePath();
        if (!File.Exists(path))
            return null;
        var directory = Path.GetDirectoryName(path)!;
        ProtectDirectory(directory);
        ProtectFile(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        return await JsonSerializer.DeserializeAsync<StoredProfile>(stream, JsonOptions, cancellationToken);
    }

    private async Task<Dictionary<string, string>> ReadActiveTenantsAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return new Dictionary<string, string>(StringComparer.Ordinal);
        var existing = await ReadProfileAsync(cancellationToken);
        return existing?.ActiveTenants ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private async Task SaveStoredProfileAsync(
        string path,
        StoredProfile stored,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)!;
        ProtectDirectory(directory);
        RejectReparsePoint(path);
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporaryPath, options))
            {
                await JsonSerializer.SerializeAsync(stream, stored, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, path, true);
            ProtectFile(path);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    internal static void ValidateServer(Uri server) => _ = GetAuthority(server);

    private static string GetAuthority(Uri server)
    {
        if (server.Scheme is not ("http" or "https") || server.UserInfo.Length != 0 || server.Query.Length != 0 ||
            server.Fragment.Length != 0 || server.AbsolutePath != "/" ||
            (server.Scheme == "http" && !IsLoopbackHost(server.Host)))
            throw new InvalidOperationException(
                "Pole server must be an HTTPS origin; HTTP is allowed only for a loopback development origin.");
        return new UriBuilder(server.Scheme, server.Host, server.Port).Uri.GetLeftPart(UriPartial.Authority);
    }

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));

    private static void ProtectDirectory(string directory)
    {
        RejectReparsePoint(directory);
        var applicationDirectory = Path.GetDirectoryName(directory);
        if (applicationDirectory is not null)
            RejectReparsePoint(applicationDirectory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return;
        }

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(
            new FileSystemAccessRule(
                WindowsIdentity.GetCurrent().User!,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
    }

    private static void ProtectFile(string path)
    {
        RejectReparsePoint(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return;
        }

        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(
            new FileSystemAccessRule(
                WindowsIdentity.GetCurrent().User!,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) &&
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException(
                "Secure token storage does not support symbolic links or reparse points.");
    }

    private sealed record StoredTokens(
        string Issuer,
        string ClientId,
        string ServerAuthority,
        OidcTokens Tokens
    );

    private sealed record StoredProfile(
        string Server,
        string Issuer,
        string ClientId,
        Dictionary<string, string>? ActiveTenants = null
    );
}
