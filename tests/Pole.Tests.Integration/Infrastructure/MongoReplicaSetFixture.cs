namespace Pole.Tests.Integration.Infrastructure;

public sealed class MongoReplicaSetFixture : Pole.Testing.MongoReplicaSetFixture
{
    public MongoTestStore DbContext { get; private set; } = null!;
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        DbContext = new MongoTestStore(Client, Database);
    }
    public override async Task ResetDatabaseAsync()
    {
        await base.ResetDatabaseAsync();
        DbContext = new MongoTestStore(Client, Database);
    }
}
