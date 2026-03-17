namespace Pole.Features.Postgres.Common

open Pole.ResultType

[<RequireQualifiedAccess>]
module ResultF =
    let ofValue<'T when 'T : not null> (value : 'T) : Result<'T> =
        Result<'T>.op_Implicit(value)

    let ofFailure<'T when 'T : not null> (failure : Failure) : Result<'T> =
        Result<'T>.op_Implicit(failure)
