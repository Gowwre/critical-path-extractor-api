namespace CriticalPathExtractor.Infrastructure

open System
open System.Collections.Generic
open System.IO
open System.Text.Json.Nodes
open System.Threading.Tasks
open Microsoft.AspNetCore.OpenApi
open Microsoft.OpenApi

module OpenApiExamples =
    type private ExampleReference = {
        name: string
        file_name: string
        summary: string
        description: string option
    }

    type private OperationExampleSet = {
        request_examples: (string * IOpenApiExample) list
        response_examples: Map<string, (string * IOpenApiExample) list>
    }

    type private ExampleCatalog = {
        single_project: OperationExampleSet
        batch: OperationExampleSet
    }

    let private parseJsonNode (json: string) : JsonNode =
        match JsonNode.Parse(json) with
        | null -> JsonValue.Create("") :> JsonNode
        | node -> node

    let private resolveExamplesRoot (contentRootPath: string) : string =
        let outputDirectoryPath = Path.Combine(AppContext.BaseDirectory, "OpenApiExamples")

        match Directory.Exists(outputDirectoryPath) with
        | true -> outputDirectoryPath
        | false -> Path.Combine(contentRootPath, "OpenApiExamples")

    let private toOpenApiExample
        (summary: string)
        (description: string option)
        (value: JsonNode)
        : IOpenApiExample =
        let example = OpenApiExample()
        example.Summary <- summary

        match description with
        | Some descriptionText -> example.Description <- descriptionText
        | None -> ()

        example.Value <- value
        example :> IOpenApiExample

    let private tryLoadExample
        (examplesRoot: string)
        (reference: ExampleReference)
        : (string * IOpenApiExample) option =
        let filePath = Path.Combine(examplesRoot, reference.file_name)

        match File.Exists(filePath) with
        | true ->
            let jsonContent = File.ReadAllText(filePath)
            let jsonNode = parseJsonNode jsonContent
            Some (reference.name, toOpenApiExample reference.summary reference.description jsonNode)
        | false ->
            None

    let private loadExamples
        (examplesRoot: string)
        (references: ExampleReference list)
        : (string * IOpenApiExample) list =
        references
        |> List.map (tryLoadExample examplesRoot)
        |> List.choose id

    let private singleProjectRequestReferences =
        [ { name = "basicGraph"
            file_name = "single-request-basic.json"
            summary = "Basic critical path"
            description = Some "Simple four-task DAG with one critical branch." }
          { name = "withThresholdObject"
            file_name = "single-request-with-threshold-object.json"
            summary = "With threshold object"
            description = Some "Uses absolute and percentage near-critical thresholds." } ]

    let private singleProjectResponseReferences =
        Map.ofList
            [ "200",
              [ { name = "success"
                  file_name = "single-response-200-critical-path.json"
                  summary = "Successful CPM analysis"
                  description = Some "Includes critical path, per-task schedule, and metadata." } ]
              "400",
              [ { name = "unknownDependency"
                  file_name = "single-response-400-unknown-dependency.json"
                  summary = "Unknown dependency"
                  description = Some "Task references a dependency ID that does not exist." }
                { name = "duplicateTaskId"
                  file_name = "single-response-400-duplicate-task-id.json"
                  summary = "Duplicate task ID"
                  description = Some "Input contains at least two tasks with the same ID." }
                { name = "circularDependency"
                  file_name = "single-response-400-circular-dependency.json"
                  summary = "Circular dependency"
                  description = Some "Graph contains a cycle and cannot be analyzed." } ]
              "413",
              [ { name = "graphTooLarge"
                  file_name = "single-response-413-graph-too-large.json"
                  summary = "Graph too large"
                  description = Some "Task count exceeds the maximum supported size." } ]
              "422",
              [ { name = "negativeDuration"
                  file_name = "single-response-422-negative-duration.json"
                  summary = "Negative duration"
                  description = Some "Task duration must be zero or positive." } ] ]

    let private batchRequestReferences =
        [ { name = "mixedProjects"
            file_name = "batch-request-mixed.json"
            summary = "Mixed valid and invalid projects"
            description = Some "First project is valid, second contains a cycle." }
          { name = "allValidProjects"
            file_name = "batch-request-valid.json"
            summary = "All valid projects"
            description = Some "Two independent valid projects in one batch." } ]

    let private batchResponseReferences =
        Map.ofList
            [ "200",
              [ { name = "partialSuccess"
                  file_name = "batch-response-200-mixed-results.json"
                  summary = "Partial success"
                  description = Some "One project succeeds while another fails." } ]
              "400",
              [ { name = "tooManyProjects"
                  file_name = "batch-response-400-too-many-projects.json"
                  summary = "Too many projects"
                  description = Some "Batch project count exceeds maximum allowed." }
                { name = "tooManyTotalTasks"
                  file_name = "batch-response-400-too-many-total-tasks.json"
                  summary = "Too many total tasks"
                  description = Some "Combined task count across projects exceeds limit." } ] ]

    let private loadResponseExamples
        (examplesRoot: string)
        (responseReferences: Map<string, ExampleReference list>)
        : Map<string, (string * IOpenApiExample) list> =
        responseReferences
        |> Map.map (fun _ references -> loadExamples examplesRoot references)

    let private loadCatalog (contentRootPath: string) : ExampleCatalog =
        let examplesRoot = resolveExamplesRoot contentRootPath

        { single_project =
            { request_examples = loadExamples examplesRoot singleProjectRequestReferences
              response_examples = loadResponseExamples examplesRoot singleProjectResponseReferences }
          batch =
            { request_examples = loadExamples examplesRoot batchRequestReferences
              response_examples = loadResponseExamples examplesRoot batchResponseReferences } }

    let private setMediaTypeExamples
        (namedExamples: (string * IOpenApiExample) list)
        (mediaType: OpenApiMediaType)
        : unit =
        match namedExamples with
        | [] -> ()
        | _ ->
            let examples = Dictionary<string, IOpenApiExample>()

            namedExamples
            |> List.iter (fun (name, example) -> examples[name] <- example)

            mediaType.Examples <- examples
            mediaType.Example <- null

    let private applyRequestExamples
        (namedExamples: (string * IOpenApiExample) list)
        (operation: OpenApiOperation)
        : unit =
        match operation.RequestBody with
        | null -> ()
        | requestBody ->
            match requestBody.Content.TryGetValue("application/json") with
            | true, mediaType -> setMediaTypeExamples namedExamples mediaType
            | false, _ -> ()

    let private applyResponseExamples
        (statusCode: string)
        (namedExamples: (string * IOpenApiExample) list)
        (operation: OpenApiOperation)
        : unit =
        match operation.Responses.TryGetValue(statusCode) with
        | true, response ->
            match response.Content.TryGetValue("application/json") with
            | true, mediaType -> setMediaTypeExamples namedExamples mediaType
            | false, _ -> ()
        | false, _ -> ()

    let private normalizePath (value: string) : string =
        value.Trim('/').ToLowerInvariant()

    let private responseExamplesForStatus
        (statusCode: string)
        (responseExamples: Map<string, (string * IOpenApiExample) list>)
        : (string * IOpenApiExample) list =
        match Map.tryFind statusCode responseExamples with
        | Some examples -> examples
        | None -> []

    let private applyOperationExamples
        (requestExamples: (string * IOpenApiExample) list)
        (responseExamples: Map<string, (string * IOpenApiExample) list>)
        (responseStatusCodes: string list)
        (operation: OpenApiOperation)
        : unit =
        applyRequestExamples requestExamples operation

        responseStatusCodes
        |> List.iter (fun statusCode ->
            responseExamples
            |> responseExamplesForStatus statusCode
            |> fun examples -> applyResponseExamples statusCode examples operation)

    let configure (contentRootPath: string) (options: OpenApiOptions) : unit =
        let catalog = loadCatalog contentRootPath

        options.AddOperationTransformer(fun (operation: OpenApiOperation) (context: OpenApiOperationTransformerContext) _ ->
            let httpMethod =
                context.Description.HttpMethod
                |> Option.ofObj
                |> Option.defaultValue ""
                |> fun value -> value.ToUpperInvariant()

            let relativePath =
                context.Description.RelativePath
                |> Option.ofObj
                |> Option.defaultValue ""
                |> normalizePath

            match httpMethod, relativePath with
            | "POST", "v1/critical-path" ->
                applyOperationExamples
                    catalog.single_project.request_examples
                    catalog.single_project.response_examples
                    [ "200"; "400"; "413"; "422" ]
                    operation
            | "POST", "v1/critical-path/batch" ->
                applyOperationExamples
                    catalog.batch.request_examples
                    catalog.batch.response_examples
                    [ "200"; "400" ]
                    operation
            | _ ->
                ()

            Task.CompletedTask)
        |> ignore
