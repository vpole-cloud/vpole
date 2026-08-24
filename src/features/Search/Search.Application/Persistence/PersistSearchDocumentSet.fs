namespace Pole.Features.Search.Application.Persistence

open System.Threading
open System.Threading.Tasks
open Pole.Features.Search.Application.Common

type PersistSearchDocumentSet =
    delegate of documentSet : SearchDocumentSet * cancellationToken : CancellationToken -> Task

type internal MongoPersistSearchDocumentSet(store : SearchProjectionStore) =
    member _.InvokeAsync(documentSet : SearchDocumentSet, cancellationToken : CancellationToken) =
        store.PersistDocumentSetAsync(documentSet, cancellationToken)
