using Pole.Testing;

namespace Pole.Features.Postgres.Tests.Kubernetes.Infrastructure;

internal static class PostgresTestData
{
    internal const string TenantId = TestPlatformDefaults.TenantId;
    internal const string DefaultDeploymentTargetId = TestPlatformDefaults.DeploymentTargetId;
    internal const string DefaultStorageProfileId = "507f1f77bcf86cd799439099";
    internal const string DefaultComputeProfileId = "507f1f77bcf86cd799439097";
}
