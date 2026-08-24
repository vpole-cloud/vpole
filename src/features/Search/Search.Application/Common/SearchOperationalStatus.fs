namespace Pole.Features.Search.Application.Common

type SearchOperationalStatusUpdate =
    { DesiredGeneration : int64
      ObservedGeneration : int64
      Status : string
      FailureCode : string option }
