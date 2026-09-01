namespace Pole.Features.Search.Application.Persistence

open System.Threading
open Pole.Features.Search.Contracts

type internal MongoDeleteSearchProjection(store : SearchProjectionStore) =
    member _.InvokeAsync(
        identity : SearchProjectionIdentity,
        resourceRevision : int64,
        cancellationToken : CancellationToken) =
        store.RemoveDocumentSetAsync(identity, resourceRevision, cancellationToken)
