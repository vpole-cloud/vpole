// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Linq.Expressions;
using MongoDB.Driver;
using Pole.Observation;

namespace Pole.Database.Observation;

public sealed record MongoObservationScope(
    string Id,
    string? TenantId
)
{
    public string DemandKey(string subscription) =>
        TenantId is null ? $"{Id}/{subscription}" : $"{TenantId}/{Id}/{subscription}";
}

public sealed record MongoObservationLease(
    MongoObservationScope Scope,
    string InstanceId,
    string Token,
    long Epoch
);

public sealed record MongoObservationDemandIdentity(
    MongoObservationScope Scope,
    string SubscriptionId,
    string ConnectionId,
    string SessionId
);

public sealed record MongoObservationDemand(
    MongoObservationDemandIdentity Identity,
    DateTimeOffset ExpiresAtUtc
);

public sealed record MongoObservationPage(
    IReadOnlyList<MongoObservationScope> Items,
    string? NextCursor
);

public enum MongoObservationRejection
{
    Busy,
    NoDemand,
    LeaseLost,
    Superseded,
    SourceChanged,
    TargetUnavailable,
}

public sealed record MongoObservationRejected(
    MongoObservationRejection Reason
);

public sealed record MongoObservationAccepted;

public sealed record MongoObservationSuccess<T>(
    T Value
)
    where T : notnull;

public union MongoObservationOutcome<T>(MongoObservationSuccess<T>, MongoObservationRejected)
    where T : notnull;

/// <summary>Feature-owned context fencing and event creation participate in the SAME Mongo transaction.</summary>
public interface IMongoObservationDefinition<TContext, TControl, TDemand, TSnapshot>
    where TControl : IObservationControl
    where TDemand : IObservationDemand
    where TSnapshot : IObservationSnapshot
{
    Expression<Func<TDemand, string>> DemandSubject { get; }
    Expression<Func<TDemand, string>>? DemandTenant { get; }
    Expression<Func<TControl, string?>> Source { get; }
    Expression<Func<TSnapshot, string>> SnapshotSource { get; }
    MongoObservationScope Scope(TContext context);
    MongoObservationScope ScopeOf(TControl control);
    FilterDefinition<TControl> ControlScope(MongoObservationScope scope);
    FilterDefinition<TDemand> DemandScope(MongoObservationScope scope);
    FilterDefinition<TSnapshot> SnapshotScope(MongoObservationScope scope);
    FilterDefinition<TControl> ControlPin(TContext context);
    UpdateDefinition<TDemand> DemandIdentityUpdate(MongoObservationScope scope);
    TControl CreateControl(TContext context);
    bool HasPin(TControl control, TContext context);
    UpdateDefinition<TControl> PinUpdate(TContext context);
    FilterDefinition<TSnapshot> SnapshotPin(TContext context);

    Task<MongoObservationRejected?> FenceAsync(
        TContext context,
        IClientSessionHandle session,
        CancellationToken ct);

    Task PublishAsync(
        TControl control,
        IClientSessionHandle session,
        CancellationToken ct);

    bool AcceptSnapshot(TSnapshot document) => true;
}
