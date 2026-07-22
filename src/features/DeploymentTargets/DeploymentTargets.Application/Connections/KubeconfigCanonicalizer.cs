using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using k8s;
using k8s.KubeConfigModels;
using Pole.ResultType;
using KubeCluster = k8s.KubeConfigModels.Cluster;
using KubeContext = k8s.KubeConfigModels.Context;

namespace Pole.Features.DeploymentTargets.Application.Connections;

internal sealed class CanonicalKubeconfigCredential
{
    [JsonRequired]
    public int FormatVersion { get; init; }

    [JsonRequired]
    public string ContextName { get; init; } = string.Empty;

    [JsonRequired]
    public string ServerUrl { get; init; } = string.Empty;

    [JsonRequired]
    public string CertificateAuthorityData { get; init; } = string.Empty;

    public string? TlsServerName { get; init; }
    public string? Token { get; init; }
    public string? ClientCertificateData { get; init; }
    public string? ClientKeyData { get; init; }

    public override string ToString() =>
        $"CanonicalKubeconfigCredential(FormatVersion={FormatVersion}, ContextName={ContextName}, ServerUrl={ServerUrl}, Credential=[REDACTED])";
}

internal static class KubeconfigCanonicalizer
{
    private const int CurrentFormatVersion = 1;
    private const string ServiceAccountContextName = "pole-service-account";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static Result<CanonicalKubeconfigCredential> Canonicalize(
        string? kubeconfig,
        string? requestedContextName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ConnectionValueValidation.TryEncodeBounded(
                kubeconfig,
                ConnectionPayloadLimits.MaximumKubeconfigBytes,
                out var payload))
        {
            return ConnectionFailures.Validation(
                "kubeconfig",
                "The kubeconfig payload is required and must not exceed the supported size.");
        }

        try
        {
            using var stream = new MemoryStream(payload, writable: false);
            var parsed = KubernetesClientConfiguration.LoadKubeConfig(stream);
            cancellationToken.ThrowIfCancellationRequested();
            return SelectAndCanonicalize(parsed, requestedContextName);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return ConnectionFailures.Validation(
                "kubeconfig",
                "The kubeconfig payload could not be parsed.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    public static byte[] Serialize(CanonicalKubeconfigCredential credential) =>
        JsonSerializer.SerializeToUtf8Bytes(credential, SerializerOptions);

    public static Result<CanonicalKubeconfigCredential> DeserializeStored(
        ReadOnlySpan<byte> payload,
        int maximumBytes = ConnectionPayloadLimits.MaximumKubeconfigBytes)
    {
        if (payload.IsEmpty || payload.Length > maximumBytes)
            return ConnectionFailures.StoredConnectionUnavailable();

        try
        {
            var credential = JsonSerializer.Deserialize<CanonicalKubeconfigCredential>(payload, SerializerOptions);
            if (credential is null || !IsValidCanonical(credential))
                return ConnectionFailures.StoredConnectionUnavailable();
            return credential;
        }
        catch (JsonException)
        {
            return ConnectionFailures.StoredConnectionUnavailable();
        }
        catch (NotSupportedException)
        {
            return ConnectionFailures.StoredConnectionUnavailable();
        }
    }

    public static K8SConfiguration CreateSanitizedConfiguration(CanonicalKubeconfigCredential credential)
    {
        const string clusterName = "pole-cluster";
        const string userName = "pole-user";

        return new K8SConfiguration
        {
            ApiVersion = "v1",
            Kind = "Config",
            CurrentContext = credential.ContextName,
            Clusters =
            [
                new KubeCluster
                {
                    Name = clusterName,
                    ClusterEndpoint = new ClusterEndpoint
                    {
                        Server = credential.ServerUrl,
                        CertificateAuthorityData = credential.CertificateAuthorityData,
                        TlsServerName = credential.TlsServerName,
                        SkipTlsVerify = false
                    }
                }
            ],
            Contexts =
            [
                new KubeContext
                {
                    Name = credential.ContextName,
                    ContextDetails = new ContextDetails
                    {
                        Cluster = clusterName,
                        User = userName
                    }
                }
            ],
            Users =
            [
                new User
                {
                    Name = userName,
                    UserCredentials = new UserCredentials
                    {
                        Token = credential.Token,
                        ClientCertificateData = credential.ClientCertificateData,
                        ClientKeyData = credential.ClientKeyData
                    }
                }
            ]
        };
    }

    public static Result<CanonicalKubeconfigCredential> CreateServiceAccountCredential(
        string? serverUrl,
        string? certificateAuthorityData,
        string? token)
    {
        if (!ConnectionValueValidation.TryNormalizeServerUrl(serverUrl, out var normalizedServer))
        {
            return ConnectionFailures.Validation(
                "serverUrl",
                "The Kubernetes server URL must be an absolute HTTPS URL without credentials, query, or fragment.");
        }

        if (!ConnectionValueValidation.TryNormalizeBase64(
                certificateAuthorityData,
                ConnectionPayloadLimits.MaximumCertificateDataCharacters,
                out var normalizedCa))
        {
            return ConnectionFailures.Validation(
                "certificateAuthorityData",
                "Embedded base64 certificate-authority data is required.");
        }

        if (!ConnectionValueValidation.HasValidTokenShape(token))
        {
            return ConnectionFailures.Validation(
                "token",
                "The service-account token is invalid.");
        }

        return new CanonicalKubeconfigCredential
        {
            FormatVersion = CurrentFormatVersion,
            ContextName = ServiceAccountContextName,
            ServerUrl = normalizedServer,
            CertificateAuthorityData = normalizedCa,
            Token = token
        };
    }

    public static bool IsServiceAccountCredential(CanonicalKubeconfigCredential credential) =>
        string.Equals(credential.ContextName, ServiceAccountContextName, StringComparison.Ordinal) &&
        credential.TlsServerName is null &&
        credential.Token is not null &&
        credential.ClientCertificateData is null &&
        credential.ClientKeyData is null;

    private static Result<CanonicalKubeconfigCredential> SelectAndCanonicalize(
        K8SConfiguration parsed,
        string? requestedContextName)
    {
        var contexts = parsed.Contexts?.ToList() ?? [];
        var clusters = parsed.Clusters?.ToList() ?? [];
        var users = parsed.Users?.ToList() ?? [];
        if (contexts.Count == 0)
        {
            return ConnectionFailures.Validation(
                "kubeconfig",
                "The kubeconfig does not define a context.");
        }

        var contextNameResult = ResolveContextName(parsed.CurrentContext, requestedContextName, contexts);
        if (!contextNameResult.TryGetValue(out var contextName, out var contextNameFailure))
            return contextNameFailure;

        var matchingContexts = contexts
            .Where(context => string.Equals(context.Name, contextName, StringComparison.Ordinal))
            .ToList();
        if (matchingContexts.Count != 1 || matchingContexts[0].ContextDetails is null)
        {
            return ConnectionFailures.Validation(
                "contextName",
                "The selected kubeconfig context is missing or ambiguous.");
        }

        var details = matchingContexts[0].ContextDetails;
        if (!TryNormalizeName(details.Cluster, out var clusterName) ||
            !TryNormalizeName(details.User, out var userName))
        {
            return ConnectionFailures.Validation(
                "kubeconfig",
                "The selected context must reference one cluster and one user.");
        }

        var matchingClusters = clusters
            .Where(cluster => string.Equals(cluster.Name, clusterName, StringComparison.Ordinal))
            .ToList();
        var matchingUsers = users
            .Where(user => string.Equals(user.Name, userName, StringComparison.Ordinal))
            .ToList();
        if (matchingClusters.Count != 1 || matchingClusters[0].ClusterEndpoint is null ||
            matchingUsers.Count != 1 || matchingUsers[0].UserCredentials is null)
        {
            return ConnectionFailures.Validation(
                "kubeconfig",
                "The selected context references missing or ambiguous connection data.");
        }

        var clusterEndpoint = matchingClusters[0].ClusterEndpoint;
        var userCredentials = matchingUsers[0].UserCredentials;
        if (clusterEndpoint.SkipTlsVerify)
        {
            return ConnectionFailures.Validation(
                "kubeconfig",
                "Kubeconfigs that disable TLS verification are not supported.");
        }

        if (!string.IsNullOrWhiteSpace(clusterEndpoint.CertificateAuthority))
        {
            return ConnectionFailures.Validation(
                "kubeconfig",
                "Certificate-authority file references are not supported; embed the data instead.");
        }

        if (!ConnectionValueValidation.TryNormalizeServerUrl(clusterEndpoint.Server, out var serverUrl))
        {
            return ConnectionFailures.Validation(
                "kubeconfig",
                "The selected cluster must use an absolute HTTPS server URL.");
        }

        if (!ConnectionValueValidation.TryNormalizeBase64(
                clusterEndpoint.CertificateAuthorityData,
                ConnectionPayloadLimits.MaximumCertificateDataCharacters,
                out var certificateAuthorityData))
        {
            return ConnectionFailures.Validation(
                "kubeconfig",
                "The selected cluster must contain embedded base64 certificate-authority data.");
        }

        if (!ConnectionValueValidation.TryNormalizeTlsServerName(
                clusterEndpoint.TlsServerName,
                out var tlsServerName))
        {
            return ConnectionFailures.Validation(
                "kubeconfig",
                "The selected cluster contains an invalid TLS server name.");
        }

        var credentialsResult = CanonicalizeCredentials(userCredentials);
        if (!credentialsResult.TryGetValue(out var canonicalCredentials, out var credentialsFailure))
            return credentialsFailure;

        return new CanonicalKubeconfigCredential
        {
            FormatVersion = CurrentFormatVersion,
            ContextName = contextName,
            ServerUrl = serverUrl,
            CertificateAuthorityData = certificateAuthorityData,
            TlsServerName = tlsServerName,
            Token = canonicalCredentials.Token,
            ClientCertificateData = canonicalCredentials.ClientCertificateData,
            ClientKeyData = canonicalCredentials.ClientKeyData
        };
    }

    private static Result<string> ResolveContextName(
        string? currentContext,
        string? requestedContextName,
        IReadOnlyList<KubeContext> contexts)
    {
        string? selectedName;
        if (!string.IsNullOrWhiteSpace(requestedContextName))
        {
            selectedName = requestedContextName;
        }
        else if (!string.IsNullOrWhiteSpace(currentContext))
        {
            selectedName = currentContext;
        }
        else if (contexts.Count == 1)
        {
            selectedName = contexts[0].Name;
        }
        else
        {
            return ConnectionFailures.Validation(
                "contextName",
                "A context name is required when the kubeconfig does not select exactly one context.");
        }

        if (!TryNormalizeName(selectedName, out var normalized) ||
            normalized.Length > ConnectionPayloadLimits.MaximumContextNameCharacters)
        {
            return ConnectionFailures.Validation(
                "contextName",
                "The selected kubeconfig context name is invalid.");
        }

        return normalized;
    }

    private static Result<CanonicalKubeconfigCredential> CanonicalizeCredentials(UserCredentials credentials)
    {
        if (credentials.ExternalExecution is not null || credentials.AuthProvider is not null)
        {
            return ConnectionFailures.Validation(
                "kubeconfig",
                "Exec and auth-provider credentials are not supported.");
        }

        if (!string.IsNullOrWhiteSpace(credentials.ClientCertificate) ||
            !string.IsNullOrWhiteSpace(credentials.ClientKey))
        {
            return ConnectionFailures.Validation(
                "kubeconfig",
                "Client certificate and key file references are not supported; embed the data instead.");
        }

        if (!string.IsNullOrWhiteSpace(credentials.UserName) ||
            !string.IsNullOrWhiteSpace(credentials.Password))
        {
            return ConnectionFailures.Validation(
                "kubeconfig",
                "Basic authentication credentials are not supported.");
        }

        if (!string.IsNullOrWhiteSpace(credentials.Impersonate) ||
            credentials.ImpersonateGroups?.Any() == true ||
            credentials.ImpersonateUserExtra?.Count > 0)
        {
            return ConnectionFailures.Validation(
                "kubeconfig",
                "Impersonation settings are not supported.");
        }

        var hasToken = !string.IsNullOrWhiteSpace(credentials.Token);
        var hasCertificate = !string.IsNullOrWhiteSpace(credentials.ClientCertificateData);
        var hasKey = !string.IsNullOrWhiteSpace(credentials.ClientKeyData);
        if (hasToken)
        {
            if (hasCertificate || hasKey || !ConnectionValueValidation.HasValidTokenShape(credentials.Token))
            {
                return ConnectionFailures.Validation(
                    "kubeconfig",
                    "The selected user must contain exactly one complete inline credential.");
            }

            return new CanonicalKubeconfigCredential { Token = credentials.Token };
        }

        if (!hasCertificate || !hasKey)
        {
            return ConnectionFailures.Validation(
                "kubeconfig",
                "The selected user must contain an inline token or a complete inline client certificate and key.");
        }

        if (!ConnectionValueValidation.TryNormalizeBase64(
                credentials.ClientCertificateData,
                ConnectionPayloadLimits.MaximumCertificateDataCharacters,
                out var clientCertificateData) ||
            !ConnectionValueValidation.TryNormalizeBase64(
                credentials.ClientKeyData,
                ConnectionPayloadLimits.MaximumCertificateDataCharacters,
                out var clientKeyData))
        {
            return ConnectionFailures.Validation(
                "kubeconfig",
                "The inline client certificate and key must contain valid base64 data.");
        }

        return new CanonicalKubeconfigCredential
        {
            ClientCertificateData = clientCertificateData,
            ClientKeyData = clientKeyData
        };
    }

    private static bool IsValidCanonical(CanonicalKubeconfigCredential credential)
    {
        if (credential.FormatVersion != CurrentFormatVersion ||
            !TryNormalizeName(credential.ContextName, out var contextName) ||
            contextName.Length > ConnectionPayloadLimits.MaximumContextNameCharacters ||
            !string.Equals(contextName, credential.ContextName, StringComparison.Ordinal) ||
            !ConnectionValueValidation.TryNormalizeServerUrl(credential.ServerUrl, out var serverUrl) ||
            !string.Equals(serverUrl, credential.ServerUrl, StringComparison.Ordinal) ||
            !ConnectionValueValidation.TryNormalizeBase64(
                credential.CertificateAuthorityData,
                ConnectionPayloadLimits.MaximumCertificateDataCharacters,
                out var certificateAuthorityData) ||
            !string.Equals(certificateAuthorityData, credential.CertificateAuthorityData, StringComparison.Ordinal) ||
            !ConnectionValueValidation.TryNormalizeTlsServerName(credential.TlsServerName, out var tlsServerName) ||
            !string.Equals(tlsServerName, credential.TlsServerName, StringComparison.Ordinal))
        {
            return false;
        }

        var hasToken = credential.Token is not null;
        var hasCertificate = credential.ClientCertificateData is not null;
        var hasKey = credential.ClientKeyData is not null;
        if (hasToken)
        {
            return !hasCertificate && !hasKey &&
                   ConnectionValueValidation.HasValidTokenShape(credential.Token);
        }

        return hasCertificate && hasKey &&
               ConnectionValueValidation.TryNormalizeBase64(
                   credential.ClientCertificateData,
                   ConnectionPayloadLimits.MaximumCertificateDataCharacters,
                   out var normalizedCertificate) &&
               string.Equals(normalizedCertificate, credential.ClientCertificateData, StringComparison.Ordinal) &&
               ConnectionValueValidation.TryNormalizeBase64(
                   credential.ClientKeyData,
                   ConnectionPayloadLimits.MaximumCertificateDataCharacters,
                   out var normalizedKey) &&
               string.Equals(normalizedKey, credential.ClientKeyData, StringComparison.Ordinal);
    }

    private static bool TryNormalizeName(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        normalized = value.Trim();
        return string.Equals(value, normalized, StringComparison.Ordinal);
    }
}
