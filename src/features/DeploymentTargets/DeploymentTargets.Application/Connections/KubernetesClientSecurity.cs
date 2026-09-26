using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using k8s;
using Pole.Features.DeploymentTargets.Application.Common;

namespace Pole.Features.DeploymentTargets.Application.Connections;

internal union CreateCandidateKubernetesClientOutcome(
    IKubernetes,
    DeploymentTargetConnectionRejected);

public union CreateStoredKubernetesClientOutcome(
    IKubernetes,
    StoredDeploymentTargetConnectionUnavailable);

internal static class KubernetesClientSecurity
{
    internal static readonly TimeSpan HttpClientTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    public static CreateCandidateKubernetesClientOutcome CreateForCandidate(
        CanonicalKubeconfigCredential credential,
        CancellationToken cancellationToken)
    {
        try
        {
            return CreateFromCanonicalCore(credential, cancellationToken);
        }
        catch (InvalidKubernetesClientConfigurationException)
        {
            return ConnectionRejections.Validation(
                    "connection",
                    "The Kubernetes connection configuration is invalid.");
        }
    }

    public static CreateStoredKubernetesClientOutcome CreateForStored(
        CanonicalKubeconfigCredential credential,
        CancellationToken cancellationToken)
    {
        try
        {
            return CreateFromCanonicalCore(credential, cancellationToken);
        }
        catch (InvalidKubernetesClientConfigurationException)
        {
            return new StoredDeploymentTargetConnectionUnavailable();
        }
    }

    public static CreateCandidateKubernetesClientOutcome CreateInClusterForCandidate(
        CancellationToken cancellationToken)
    {
        try
        {
            return CreateInClusterCore(cancellationToken);
        }
        catch (InvalidKubernetesClientConfigurationException)
        {
            return ConnectionRejections.Validation(
                    "connection",
                    "The Kubernetes connection configuration is invalid.");
        }
    }

    public static CreateStoredKubernetesClientOutcome CreateInClusterForStored(
        CancellationToken cancellationToken)
    {
        try
        {
            return CreateInClusterCore(cancellationToken);
        }
        catch (InvalidKubernetesClientConfigurationException)
        {
            return new StoredDeploymentTargetConnectionUnavailable();
        }
    }

    private static IKubernetes CreateFromCanonicalCore(
        CanonicalKubeconfigCredential credential,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // Only the newly constructed single-context object reaches BuildConfigFromConfigObject.
            // The uploaded kubeconfig is never allowed to execute auth plugins or read local files.
            var sanitized = KubeconfigCanonicalizer.CreateSanitizedConfiguration(credential);
            var configuration = KubernetesClientConfiguration.BuildConfigFromConfigObject(
                sanitized,
                credential.ContextName);
            if (configuration.SkipTlsVerify ||
                !ConnectionValueValidation.TryNormalizeServerUrl(configuration.Host, out var builtHost) ||
                !string.Equals(builtHost, credential.ServerUrl, StringComparison.Ordinal) ||
                !TryDetachStrictTrust(
                    configuration,
                    credential.CertificateAuthorityData,
                    out var expectedHost,
                    out var trustedAuthorities))
            {
                throw new InvalidKubernetesClientConfigurationException();
            }

            ApplyTransportHardening(configuration, expectedHost, trustedAuthorities);
            cancellationToken.ThrowIfCancellationRequested();
            return new Kubernetes(configuration);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidKubernetesClientConfigurationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new InvalidKubernetesClientConfigurationException(exception);
        }
    }

    private static IKubernetes CreateInClusterCore(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var configuration = KubernetesClientConfiguration.InClusterConfig();
            if (configuration.SkipTlsVerify ||
                !ConnectionValueValidation.TryNormalizeServerUrl(configuration.Host, out _) ||
                !TryDetachStrictTrust(configuration, out var expectedHost, out var trustedAuthorities))
            {
                throw new InvalidKubernetesClientConfigurationException();
            }

            ApplyTransportHardening(configuration, expectedHost, trustedAuthorities);
            cancellationToken.ThrowIfCancellationRequested();
            return new Kubernetes(configuration);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidKubernetesClientConfigurationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new InvalidKubernetesClientConfigurationException(exception);
        }
    }

    private sealed class InvalidKubernetesClientConfigurationException(Exception? innerException = null)
        : Exception("The Kubernetes connection configuration is invalid.", innerException);

    private static bool TryDetachStrictTrust(
        KubernetesClientConfiguration configuration,
        out string expectedHost,
        out byte[][] trustedAuthorities)
    {
        expectedHost = string.Empty;
        trustedAuthorities = [];
        if (!Uri.TryCreate(configuration.Host, UriKind.Absolute, out var serverUri) ||
            configuration.SslCaCerts is not { Count: > 0 } authorities)
        {
            return false;
        }

        expectedHost = string.IsNullOrWhiteSpace(configuration.TlsServerName)
            ? serverUri.IdnHost
            : configuration.TlsServerName;
        var authorityCertificates = authorities.Cast<X509Certificate2>().ToArray();
        try
        {
            trustedAuthorities = authorityCertificates
                .Select(certificate => certificate.Export(X509ContentType.Cert))
                .ToArray();
        }
        catch (CryptographicException)
        {
            return false;
        }
        finally
        {
            foreach (var authorityCertificate in authorityCertificates)
                authorityCertificate.Dispose();
            configuration.SslCaCerts = null!;
        }

        if (trustedAuthorities.Length == 0 || trustedAuthorities.Any(authority => authority.Length == 0))
            return false;

        // KubernetesClient installs its own permissive callback whenever SslCaCerts remains set.
        // Detach the collection and install Pole's exclusive custom-root validator below.
        return true;
    }

    private static bool TryDetachStrictTrust(
        KubernetesClientConfiguration configuration,
        string certificateAuthorityData,
        out string expectedHost,
        out byte[][] trustedAuthorities)
    {
        expectedHost = string.Empty;
        trustedAuthorities = [];
        if (!Uri.TryCreate(configuration.Host, UriKind.Absolute, out var serverUri) ||
            !TryLoadTrustedAuthorities(certificateAuthorityData, out trustedAuthorities))
        {
            DisposeConfigurationAuthorities(configuration);
            return false;
        }

        expectedHost = string.IsNullOrWhiteSpace(configuration.TlsServerName)
            ? serverUri.IdnHost
            : configuration.TlsServerName;
        DisposeConfigurationAuthorities(configuration);
        return true;
    }

    private static bool TryLoadTrustedAuthorities(
        string certificateAuthorityData,
        out byte[][] trustedAuthorities)
    {
        trustedAuthorities = [];
        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(certificateAuthorityData);
        }
        catch (FormatException)
        {
            return false;
        }

        var certificates = new X509Certificate2Collection();
        try
        {
            var pem = Encoding.UTF8.GetString(decoded);
            if (pem.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal))
            {
                certificates.ImportFromPem(pem);
            }
            else
            {
                certificates.Add(X509CertificateLoader.LoadCertificate(decoded));
            }

            trustedAuthorities = certificates
                .Cast<X509Certificate2>()
                .Select(certificate => certificate.Export(X509ContentType.Cert))
                .ToArray();
            return trustedAuthorities.Length > 0 &&
                   trustedAuthorities.All(authority => authority.Length > 0);
        }
        catch (Exception exception) when (
            exception is CryptographicException or ArgumentException)
        {
            trustedAuthorities = [];
            return false;
        }
        finally
        {
            foreach (var certificate in certificates)
                certificate.Dispose();
        }
    }

    private static void DisposeConfigurationAuthorities(
        KubernetesClientConfiguration configuration)
    {
        if (configuration.SslCaCerts is { } authorities)
        {
            foreach (var authority in authorities)
                authority.Dispose();
        }

        configuration.SslCaCerts = null!;
    }

    private static void ApplyTransportHardening(
        KubernetesClientConfiguration configuration,
        string expectedHost,
        IReadOnlyList<byte[]> trustedAuthorities)
    {
        var existingSetup = configuration.FirstMessageHandlerSetup;
        configuration.FirstMessageHandlerSetup = handler =>
        {
            existingSetup?.Invoke(handler);
            handler.AllowAutoRedirect = false;
            handler.ConnectTimeout = ConnectTimeout;
            handler.UseProxy = false;
            handler.SslOptions.TargetHost = expectedHost;
            handler.SslOptions.RemoteCertificateValidationCallback =
                CreateStrictCertificateValidator(expectedHost, trustedAuthorities);
        };
        configuration.HttpClientTimeout = HttpClientTimeout;
    }

    private static RemoteCertificateValidationCallback CreateStrictCertificateValidator(
        string expectedHost,
        IReadOnlyList<byte[]> trustedAuthorities) => (_, certificate, presentedChain, _) =>
        {
            if (certificate is null)
                return false;

            var ownedCertificates = new List<X509Certificate2>();
            try
            {
                var leaf = X509CertificateLoader.LoadCertificate(
                    certificate.Export(X509ContentType.Cert));
                ownedCertificates.Add(leaf);
                if (!leaf.MatchesHostname(
                        expectedHost,
                        allowWildcards: true,
                        allowCommonName: false))
                {
                    return false;
                }

                using var strictChain = new X509Chain();
                var policy = strictChain.ChainPolicy;
                policy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                policy.RevocationMode = X509RevocationMode.NoCheck;
                policy.VerificationFlags = X509VerificationFlags.NoFlag;
                policy.DisableCertificateDownloads = true;
                policy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));

                foreach (var authorityBytes in trustedAuthorities)
                {
                    var authority = X509CertificateLoader.LoadCertificate(authorityBytes);
                    ownedCertificates.Add(authority);
                    policy.CustomTrustStore.Add(authority);
                    policy.ExtraStore.Add(authority);
                }

                if (presentedChain is not null)
                {
                    foreach (var element in presentedChain.ChainElements.Cast<X509ChainElement>().Skip(1))
                    {
                        var intermediate = X509CertificateLoader.LoadCertificate(
                            element.Certificate.Export(X509ContentType.Cert));
                        ownedCertificates.Add(intermediate);
                        policy.ExtraStore.Add(intermediate);
                    }
                }

                if (!strictChain.Build(leaf) || strictChain.ChainElements.Count == 0)
                    return false;

                var terminal = strictChain.ChainElements[^1].Certificate.RawData;
                return trustedAuthorities.Any(authority => authority.Length == terminal.Length &&
                    CryptographicOperations.FixedTimeEquals(authority, terminal));
            }
            catch (Exception exception) when (
                exception is CryptographicException or ArgumentException or InvalidOperationException)
            {
                return false;
            }
            finally
            {
                foreach (var ownedCertificate in ownedCertificates)
                    ownedCertificate.Dispose();
            }
        };
}
