namespace Pole.Features.Search

open Microsoft.Extensions.DependencyInjection
open Pole.Features.Search.Application.Services

[<AutoOpen>]
module DependencyInjection =
    let addSearchFeature (services : IServiceCollection) =
        services.AddSearchFeature()

