using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Pole.Functions.Protocol;

namespace Pole.Features.Functions.Runtime.Security;

public sealed class FunctionAgentTokenOptions
{
    public const string SectionName = "Functions:AgentAuthentication";

    public string Issuer { get; set; } = "pole-function-runtime";
    public string Audience { get; set; } = "pole-function-agent";
    public string ActiveKeyId { get; set; } = string.Empty;
    public string PrivateKeyPath { get; set; } = string.Empty;
    public int TokenLifetimeSeconds { get; set; } = 60;
    public List<FunctionAgentPublicKeyOptions> TrustedPublicKeys { get; set; } = [];

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Issuer) || string.IsNullOrWhiteSpace(Audience))
            throw new InvalidOperationException("Function Agent token issuer and audience are required.");
        if (string.IsNullOrWhiteSpace(ActiveKeyId) || string.IsNullOrWhiteSpace(PrivateKeyPath))
            throw new InvalidOperationException("Function Agent signing key id and private key path are required.");
        if (TokenLifetimeSeconds is < 5 or > 300)
            throw new InvalidOperationException("Function Agent token lifetime must be between 5 and 300 seconds.");
        using var privateKey = RSA.Create();
        privateKey.ImportFromPem(File.ReadAllText(PrivateKeyPath));
        if (privateKey.KeySize < 2048)
            throw new InvalidOperationException("Function Agent RSA signing key must be at least 2048 bits.");

        var keyIds = new HashSet<string>(StringComparer.Ordinal) { ActiveKeyId };
        foreach (var trustedKey in TrustedPublicKeys)
        {
            if (string.IsNullOrWhiteSpace(trustedKey.KeyId) ||
                string.IsNullOrWhiteSpace(trustedKey.SubjectPublicKeyInfoBase64) ||
                !keyIds.Add(trustedKey.KeyId))
                throw new InvalidOperationException("Function Agent trusted public keys require unique key ids and key material.");
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(trustedKey.SubjectPublicKeyInfoBase64), out _);
            if (rsa.KeySize < 2048)
                throw new InvalidOperationException("Function Agent trusted RSA keys must be at least 2048 bits.");
        }
    }
}

public sealed class FunctionAgentPublicKeyOptions
{
    public string KeyId { get; set; } = string.Empty;
    public string SubjectPublicKeyInfoBase64 { get; set; } = string.Empty;
}

public sealed record FunctionAgentSigningKey(string KeyId, RSA PrivateKey)
{
    public string SubjectPublicKeyInfoBase64 =>
        Convert.ToBase64String(PrivateKey.ExportSubjectPublicKeyInfo());
}

public interface IFunctionAgentSigningKeyProvider
{
    FunctionAgentSigningKey GetSigningKey();
}

public sealed class FileFunctionAgentSigningKeyProvider(FunctionAgentTokenOptions options)
    : IFunctionAgentSigningKeyProvider, IDisposable
{
    private readonly Lazy<FunctionAgentSigningKey> key = new(() => Load(options));

    public FunctionAgentSigningKey GetSigningKey() => key.Value;

    public void Dispose()
    {
        if (key.IsValueCreated)
            key.Value.PrivateKey.Dispose();
    }

    private static FunctionAgentSigningKey Load(FunctionAgentTokenOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ActiveKeyId))
            throw new InvalidOperationException("Function Agent signing key id is not configured.");
        if (string.IsNullOrWhiteSpace(options.PrivateKeyPath))
            throw new InvalidOperationException("Function Agent private key path is not configured.");

        var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(File.ReadAllText(options.PrivateKeyPath));
            if (rsa.KeySize < 2048)
                throw new InvalidOperationException("Function Agent RSA signing key must be at least 2048 bits.");
            return new FunctionAgentSigningKey(options.ActiveKeyId, rsa);
        }
        catch
        {
            rsa.Dispose();
            throw;
        }
    }
}

public sealed record FunctionAgentTokenDescriptor(
    string TenantId,
    string FunctionAppId,
    string RevisionId,
    string TargetUid,
    string Operation,
    FunctionAttemptIdentity? Attempt = null,
    string? AttemptId = null);

public sealed record FunctionAgentTrustedKey(string KeyId, string SubjectPublicKeyInfoBase64);

public sealed record FunctionAgentTrustBundle(
    string Issuer,
    string Audience,
    IReadOnlyList<FunctionAgentTrustedKey> TrustedKeys);

public interface IFunctionAgentTrustBundleProvider
{
    FunctionAgentTrustBundle GetTrustBundle();
}

public interface IFunctionAgentTokenIssuer : IFunctionAgentTrustBundleProvider
{
    string Issue(FunctionAgentTokenDescriptor descriptor);
}

public sealed class FunctionAgentTokenIssuer(
    FunctionAgentTokenOptions options,
    IFunctionAgentSigningKeyProvider signingKeyProvider,
    TimeProvider timeProvider) : IFunctionAgentTokenIssuer
{
    private readonly FunctionAgentSigningKey signingKey = signingKeyProvider.GetSigningKey();
    private readonly JsonWebTokenHandler tokenHandler = new() { SetDefaultTimesOnTokenCreation = false };

    public FunctionAgentTrustBundle GetTrustBundle() => new(
        options.Issuer,
        options.Audience,
        new[] { new FunctionAgentTrustedKey(signingKey.KeyId, signingKey.SubjectPublicKeyInfoBase64) }
            .Concat(options.TrustedPublicKeys.Select(key => new FunctionAgentTrustedKey(
                key.KeyId,
                key.SubjectPublicKeyInfoBase64)))
            .ToArray());

    public string Issue(FunctionAgentTokenDescriptor descriptor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.TenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.FunctionAppId);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.RevisionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.TargetUid);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.Operation);
        if (descriptor.Attempt is { } attempt &&
            (!string.Equals(attempt.TenantId, descriptor.TenantId, StringComparison.Ordinal) ||
             !string.Equals(attempt.FunctionAppId, descriptor.FunctionAppId, StringComparison.Ordinal) ||
             !string.Equals(attempt.RevisionId, descriptor.RevisionId, StringComparison.Ordinal)))
        {
            throw new ArgumentException("Function attempt identity does not match the token subject.", nameof(descriptor));
        }
        if (descriptor.Attempt is not null && descriptor.AttemptId is not null &&
            !string.Equals(descriptor.Attempt.AttemptId, descriptor.AttemptId, StringComparison.Ordinal))
        {
            throw new ArgumentException("Function attempt identifiers do not match.", nameof(descriptor));
        }
        var now = timeProvider.GetUtcNow();
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, "pole-function-runtime"),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(PoleFunctionAgentToken.Claims.TenantId, descriptor.TenantId),
            new(PoleFunctionAgentToken.Claims.FunctionAppId, descriptor.FunctionAppId),
            new(PoleFunctionAgentToken.Claims.RevisionId, descriptor.RevisionId),
            new(PoleFunctionAgentToken.Claims.TargetUid, descriptor.TargetUid),
            new(PoleFunctionAgentToken.Claims.Operation, descriptor.Operation)
        };
        AddAttemptClaims(claims, descriptor);
        var securityKey = new RsaSecurityKey(signingKey.PrivateKey)
        {
            KeyId = signingKey.KeyId,
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
        };
        return tokenHandler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = options.Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.AddSeconds(-5).UtcDateTime,
            Expires = now.AddSeconds(options.TokenLifetimeSeconds).UtcDateTime,
            TokenType = PoleFunctionAgentToken.Type,
            SigningCredentials = new SigningCredentials(securityKey, SecurityAlgorithms.RsaSha256)
        });
    }

    private static void AddAttemptClaims(List<Claim> claims, FunctionAgentTokenDescriptor descriptor)
    {
        var attemptId = descriptor.Attempt?.AttemptId ?? descriptor.AttemptId;
        if (!string.IsNullOrWhiteSpace(attemptId))
            claims.Add(new Claim(PoleFunctionAgentToken.Claims.AttemptId, attemptId));
        if (descriptor.Attempt is not { } attempt)
            return;
        claims.Add(new Claim(PoleFunctionAgentToken.Claims.InvocationId, attempt.InvocationId));
        claims.Add(new Claim(PoleFunctionAgentToken.Claims.HandlerName, attempt.HandlerName));
        claims.Add(new Claim(
            PoleFunctionAgentToken.Claims.AttemptNumber,
            attempt.AttemptNumber.ToString(CultureInfo.InvariantCulture)));
    }
}
