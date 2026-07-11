using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Pole.ResultType;

namespace Pole.Features.ContainerRegistries.Application.Security;

public interface IRegistryCredentialProtector
{
    string Protect(string secret);
    Result<string> Unprotect(string protectedSecret);
}

public sealed class RegistryCredentialProtector(IDataProtectionProvider dataProtectionProvider)
    : IRegistryCredentialProtector
{
    private readonly IDataProtector _protector =
        dataProtectionProvider.CreateProtector("Pole.ContainerRegistry.Credential.v1");

    public string Protect(string secret) => _protector.Protect(secret);

    public Result<string> Unprotect(string protectedSecret)
    {
        try
        {
            return _protector.Unprotect(protectedSecret);
        }
        catch (CryptographicException)
        {
            return Failures.Internal(
                "The container registry credential could not be decrypted.",
                "The Data Protection key used to encrypt the credential is unavailable or invalid.");
        }
    }
}
