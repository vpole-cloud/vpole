using System.Security.Cryptography;
using System.Text;

namespace Pole.Features.ContainerRegistries.Application.Common;

public static class RegistryProjectName
{
    public static string ForTenant(string tenantId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(tenantId));
        return $"pole-{Convert.ToHexStringLower(hash.AsSpan(0, 16))}";
    }
}
