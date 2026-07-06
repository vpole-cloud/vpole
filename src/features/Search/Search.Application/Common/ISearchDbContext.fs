namespace Pole.Features.Search.Application.Common

open MongoDB.Driver
open Pole.Database
open Pole.Features.Search.Domain

type ISearchDbContext =
    inherit IEventDbContext
    
    abstract SearchDocuments : IMongoCollection<SearchDocument>
