using Pole.Features.Postgres.Application.Common;
using Pole.Features.Postgres.Application.Endpoints;
using Pole.Mediator;
using Xunit.Sdk;

namespace Pole.Features.Postgres.Tests.Integration;

internal static class PostgresOutcomeAssertions
{
    public static PostgresClusterDto AssertCreated(
        this MediatorResult<CreatePostgresClusterOutcome> result) =>
        HandlerOutcome(result) switch
        {
            CreatePostgresClusterOutcome.PostgresClusterCreated created => created.Item,
            CreatePostgresClusterOutcome.CreatePostgresClusterRejected rejected =>
                throw Unexpected("PostgreSQL cluster creation", rejected.Item),
            _ => throw new XunitException("Unknown PostgreSQL cluster creation outcome.")
        };

    public static UpdatePostgresClusterResponse AssertUpdated(
        this MediatorResult<UpdatePostgresClusterOutcome> result) =>
        HandlerOutcome(result) switch
        {
            UpdatePostgresClusterOutcome.PostgresClusterUpdated updated => updated.Item,
            UpdatePostgresClusterOutcome.UpdatePostgresClusterRejected rejected =>
                throw Unexpected("PostgreSQL cluster update", rejected.Item),
            _ => throw new XunitException("Unknown PostgreSQL cluster update outcome.")
        };

    public static UpdatePostgresClusterPasswordResponse AssertPasswordUpdated(
        this MediatorResult<UpdatePostgresClusterPasswordOutcome> result) =>
        HandlerOutcome(result) switch
        {
            UpdatePostgresClusterPasswordOutcome.PostgresClusterPasswordUpdated updated => updated.Item,
            UpdatePostgresClusterPasswordOutcome.UpdatePostgresClusterPasswordRejected rejected =>
                throw Unexpected("PostgreSQL password rotation", rejected.Item),
            _ => throw new XunitException("Unknown PostgreSQL password rotation outcome.")
        };

    public static GetPostgresClusterConnectionStringResponse AssertConnectionString(
        this MediatorResult<GetPostgresClusterConnectionStringOutcome> result) =>
        HandlerOutcome(result) switch
        {
            GetPostgresClusterConnectionStringOutcome.PostgresClusterConnectionString response => response.Item,
            GetPostgresClusterConnectionStringOutcome.GetPostgresClusterConnectionStringRejected rejected =>
                throw Unexpected("PostgreSQL connection string", rejected.Item),
            _ => throw new XunitException("Unknown PostgreSQL connection string outcome.")
        };

    public static DeletePostgresClusterResponse AssertDeletionAccepted(
        this MediatorResult<DeletePostgresClusterOutcome> result) =>
        HandlerOutcome(result) switch
        {
            DeletePostgresClusterOutcome.PostgresClusterDeletionAccepted accepted => accepted.Item,
            DeletePostgresClusterOutcome.DeletePostgresClusterRejected rejected =>
                throw Unexpected("PostgreSQL cluster deletion", rejected.Item),
            _ => throw new XunitException("Unknown PostgreSQL cluster deletion outcome.")
        };

    private static TOutcome HandlerOutcome<TOutcome>(MediatorResult<TOutcome> result)
        where TOutcome : notnull =>
        result.Match(
            outcome => outcome,
            rejected => throw new XunitException(
                $"Expected a PostgreSQL handler outcome, but the pipeline returned " +
                $"'{rejected.Reason.GetType().Name}'."));

    private static XunitException Unexpected(string operation, object error) =>
        new($"Expected {operation} success, but received {error.GetType().Name}: {error}");
}
