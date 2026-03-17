namespace CriticalPathExtractor

#nowarn "20"

open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open System.Text.Json.Serialization
open CriticalPathExtractor.Infrastructure
open Scalar.AspNetCore

module Program =
    let exitCode = 0

    [<EntryPoint>]
    let main args =
        let builder = WebApplication.CreateBuilder(args)

        // Add OpenAPI support
        builder.Services.AddOpenApi()
        
        builder.Services.AddControllers()
            .AddJsonOptions(fun options ->
                options.JsonSerializerOptions.Converters.Add(ThresholdValueConverter())
            )

        let app = builder.Build()

        // Configure OpenAPI and Scalar in development
        match app.Environment.IsDevelopment() with
        | true ->
            app.MapOpenApi() |> ignore
            app.MapScalarApiReference() |> ignore
        | false -> ()

        app.UseHttpsRedirection()
        app.UseAuthorization()
        app.MapControllers()

        app.Run()

        exitCode
