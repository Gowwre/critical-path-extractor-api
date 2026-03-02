namespace CriticalPathExtractor

#nowarn "20"

open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open CriticalPathExtractor.Infrastructure
open Scalar.AspNetCore

module Program =
    let exitCode = 0

    let private configureJsonOptions (options: Microsoft.AspNetCore.Mvc.JsonOptions) : unit =
        options.JsonSerializerOptions.Converters.Add(ThresholdValueConverter())

    let private configureControllers (builder: WebApplicationBuilder) : WebApplicationBuilder =
        builder.Services.AddControllers().AddJsonOptions(configureJsonOptions) |> ignore
        builder

    let private configureServices (builder: WebApplicationBuilder) : WebApplicationBuilder =
        builder.Services.AddOpenApi(OpenApiExamples.configure builder.Environment.ContentRootPath) |> ignore
        builder |> configureControllers

    let private configureDevelopmentOpenApi (app: WebApplication) : WebApplication =
        match app.Environment.IsDevelopment() with
        | true ->
            app.MapOpenApi() |> ignore
            app.MapScalarApiReference() |> ignore
            app
        | false ->
            app

    let private configureMiddleware (app: WebApplication) : WebApplication =
        app.UseHttpsRedirection()
        app.UseAuthorization()
        app.MapControllers() |> ignore
        app

    [<EntryPoint>]
    let main args =
        args
        |> WebApplication.CreateBuilder
        |> configureServices
        |> fun builder -> builder.Build()
        |> configureDevelopmentOpenApi
        |> configureMiddleware
        |> fun app -> app.Run()

        exitCode