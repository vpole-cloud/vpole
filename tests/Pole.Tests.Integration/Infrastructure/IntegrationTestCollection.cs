using Xunit;

namespace Pole.Tests.Integration.Infrastructure;

[CollectionDefinition(Global)]
public sealed class IntegrationTestCollection : ICollectionFixture<MongoReplicaSetFixture>
{
    public const string Global = "integration-global";
    public const string Global2 = "integration-global-2";
    public const string Global3 = "integration-global-3";
    public const string Global4 = "integration-global-4";
    public const string Exclusive = "integration-process-state";
    public const string Name = Global;
    public const string Tenant1 = "integration-tenant-1";
    public const string Tenant2 = "integration-tenant-2";
    public const string Tenant3 = "integration-tenant-3";
    public const string Tenant4 = "integration-tenant-4";

    public static bool IsGlobal(string name) => name is Global or Global2 or Global3 or Global4 or Exclusive;
}

[CollectionDefinition(IntegrationTestCollection.Global2)]
public sealed class GlobalTwoCollection : ICollectionFixture<MongoReplicaSetFixture>;
[CollectionDefinition(IntegrationTestCollection.Global3)]
public sealed class GlobalThreeCollection : ICollectionFixture<MongoReplicaSetFixture>;
[CollectionDefinition(IntegrationTestCollection.Global4)]
public sealed class GlobalFourCollection : ICollectionFixture<MongoReplicaSetFixture>;
[CollectionDefinition(IntegrationTestCollection.Exclusive, DisableParallelization = true)]
public sealed class ProcessStateCollection : ICollectionFixture<MongoReplicaSetFixture>;

[CollectionDefinition(IntegrationTestCollection.Tenant1)]
public sealed class TenantOneCollection : ICollectionFixture<MongoReplicaSetFixture>;
[CollectionDefinition(IntegrationTestCollection.Tenant2)]
public sealed class TenantTwoCollection : ICollectionFixture<MongoReplicaSetFixture>;
[CollectionDefinition(IntegrationTestCollection.Tenant3)]
public sealed class TenantThreeCollection : ICollectionFixture<MongoReplicaSetFixture>;
[CollectionDefinition(IntegrationTestCollection.Tenant4)]
public sealed class TenantFourCollection : ICollectionFixture<MongoReplicaSetFixture>;
