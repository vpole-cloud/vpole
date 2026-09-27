namespace Pole.Features.Postgres.Contracts;

public static class PostgresConnectionEndpoints
{
    public const string ReadWriteId = "postgres";
    public const int TcpPort = 5432;

    public static string BuildReadWriteServiceName(string resourceId) => $"pg-{resourceId}-rw";
}
