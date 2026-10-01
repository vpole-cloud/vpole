using System.Security.Cryptography.X509Certificates;

namespace Pole.Connector;

public sealed class ConnectorIdentityStore
{
    private const UnixFileMode DirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode CredentialFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private readonly string directory;

    private ConnectorIdentityStore(string directory) => this.directory = directory;

    public static ConnectorIdentityStore Open(string directory)
    {
        if (OperatingSystem.IsWindows() || !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Connector key storage requires Unix file modes.");
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("Connector state directory is required.", nameof(directory));
        var path = Path.GetFullPath(directory);
        Directory.CreateDirectory(path);
        if ((System.IO.File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Connector state directory cannot be a symbolic link.");
        File.SetUnixFileMode(path, DirectoryMode);
        if (File.GetUnixFileMode(path) != DirectoryMode)
            throw new IOException("Connector state directory permissions are unsafe.");
        return new ConnectorIdentityStore(path);
    }

    public bool HasPending => File.Exists(PendingPath);
    public bool HasActive => File.Exists(ActivePath);

    public string PendingPath => Path.Combine(directory, "connector.next.pfx");
    public string ActivePath => Path.Combine(directory, "connector.pfx");

    public async Task WritePendingAsync(
        X509Certificate2 certificate,
        CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows() || !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException();
        if (!certificate.HasPrivateKey || HasPending)
            throw new IOException("A pending connector identity already exists.");
        var payload = certificate.Export(X509ContentType.Pkcs12, string.Empty);
        var temporaryPath = Path.Combine(directory, $".connector.next.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var file = new FileStream(temporaryPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 4096,
                Options = FileOptions.WriteThrough,
                UnixCreateMode = CredentialFileMode
            }))
            {
                if (File.GetUnixFileMode(temporaryPath) != CredentialFileMode)
                    throw new IOException("Connector credential file permissions are unsafe.");
                await file.WriteAsync(payload, cancellationToken);
                await file.FlushAsync(cancellationToken);
                file.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, PendingPath);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    public X509Certificate2 LoadPending() => Load(PendingPath);
    public X509Certificate2 LoadActive() => Load(ActivePath);

    public void PromotePending()
    {
        if (OperatingSystem.IsWindows() || !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException();
        if (!HasPending)
            throw new IOException("No pending connector identity exists.");
        File.Move(PendingPath, ActivePath, overwrite: true);
        File.SetUnixFileMode(ActivePath, CredentialFileMode);
    }

    private static X509Certificate2 Load(string path)
    {
        if (OperatingSystem.IsWindows() || !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException();
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
            File.GetUnixFileMode(path) != CredentialFileMode)
            throw new IOException("Connector credential file permissions are unsafe.");
        var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            path,
            string.Empty,
            X509KeyStorageFlags.EphemeralKeySet);
        if (!certificate.HasPrivateKey)
        {
            certificate.Dispose();
            throw new IOException("Connector credential file lacks a private key.");
        }
        return certificate;
    }
}
