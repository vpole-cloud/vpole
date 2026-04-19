namespace Pole.Features.Postgres.Common

open System.Threading
open System.Threading.Tasks
open MongoDB.Driver

[<AutoOpen>]
module MongoExtensions =
    type IFindFluent<'TDocument, 'TProjection> with
        member this.FirstOrNoneAsync(?cancellationToken: CancellationToken) : Task<'TProjection option> =
            task {
                let! value = this.FirstOrDefaultAsync(defaultArg cancellationToken CancellationToken.None)
                return if obj.ReferenceEquals(box value, null) then None else Some value
            }
