namespace Pole.Features.Search.Application.Persistence

open System
open System.Threading
open System.Threading.Tasks
open Pole.Features.Search.Application.Common
open Pole.Features.Search.Domain

type PersistSearchOperationalStatus =
    delegate of
        sourceType : SearchDocumentType *
        tenantId : string *
        sourceId : string *
        status : SearchOperationalStatusUpdate *
        eventOccurredAtUtc : DateTime *
        cancellationToken : CancellationToken -> Task

type internal MongoPersistSearchOperationalStatus(store : SearchProjectionStore) =
    member _.InvokeAsync(
        sourceType : SearchDocumentType,
        tenantId : string,
        sourceId : string,
        status : SearchOperationalStatusUpdate,
        eventOccurredAtUtc : DateTime,
        cancellationToken : CancellationToken) =
        store.PersistOperationalStatusAsync(
            sourceType,
            tenantId,
            sourceId,
            status,
            eventOccurredAtUtc,
            cancellationToken)
