namespace Pole.Features.Search.Application.Persistence

open System.Threading
open System.Threading.Tasks
open Pole.Features.Search.Domain

type RemoveSearchDocumentSet =
    delegate of
        sourceType : SearchDocumentType *
        tenantId : string *
        sourceId : string *
        cancellationToken : CancellationToken -> Task

type internal MongoRemoveSearchDocumentSet(store : SearchProjectionStore) =
    member _.InvokeAsync(
        sourceType : SearchDocumentType,
        tenantId : string,
        sourceId : string,
        cancellationToken : CancellationToken) =
        store.RemoveDocumentSetAsync(sourceType, tenantId, sourceId, cancellationToken)
