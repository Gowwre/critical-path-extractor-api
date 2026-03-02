module CriticalPathExtractor.Tests

open System
open System.Text.Json
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Mvc
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Primitives
open Xunit
open CriticalPathExtractor
open CriticalPathExtractor.Types
open CriticalPathExtractor.Controllers
open CriticalPathExtractor.Infrastructure

let private createTask id duration dependencies =
    { id = id
      label = None
      duration = duration
      dependencies = dependencies
      calendar_id = None }

let private createOptions durationUnit nearCriticalThreshold includeAllPaths =
    { duration_unit = durationUnit
      near_critical_threshold = nearCriticalThreshold
      include_all_paths = includeAllPaths
      project_start = None
      lang = None }

let private createSingleRequest tasks options =
    { tasks = tasks
      calendars = None
      options = options }

let private createBatchProject id tasks options =
    { id = id
      tasks = tasks
      calendars = None
      options = options }

let private createBatchRequest projects =
    { projects = projects }

let private createNode id duration dependencies successors totalFloat =
    { id = id
      label = id
      duration = duration
      dependencies = dependencies
      successors = successors
      calendar_id = None
      es = 0.0
      ef = duration
      ls = 0.0
      lf = duration
      total_float = totalFloat }

let private recBuildStrings prefix count =
    let rec loop index acc =
        match index > count with
        | true -> List.rev acc
        | false ->
            let value = sprintf "%s%i" prefix index
            loop (index + 1) (value :: acc)

    loop 1 []

let private buildTasks count =
    let rec loop index acc =
        match index > count with
        | true -> List.rev acc
        | false ->
            let taskId = sprintf "T%05i" index
            let nextTask = createTask taskId 1.0 []
            loop (index + 1) (nextTask :: acc)

    loop 1 []

let private oversizedTaskList = lazy (buildTasks (CpmEngine.MAX_TASKS + 1))
let private oversizedBatchTaskList = lazy (buildTasks (CpmEngine.MAX_BATCH_TASKS + 1))

let private createController (queryLang: string option) (headerLang: string option) =
    let loggerFactory = LoggerFactory.Create(fun _ -> ())
    let logger = loggerFactory.CreateLogger<CriticalPathController>()
    let controller = CriticalPathController(logger)
    let httpContext = DefaultHttpContext()

    match queryLang with
    | Some language ->
        httpContext.Request.QueryString <- QueryString(sprintf "?lang=%s" language)
    | None ->
        ()

    match headerLang with
    | Some language ->
        httpContext.Request.Headers["Accept-Language"] <- StringValues(language)
    | None ->
        ()

    let controllerContext = ControllerContext()
    controllerContext.HttpContext <- httpContext
    controller.ControllerContext <- controllerContext
    controller

let private getStatusCode (result: IActionResult) : int =
    match result with
    | :? BadRequestObjectResult -> 400
    | :? ObjectResult as objectResult ->
        objectResult.StatusCode
        |> Option.ofNullable
        |> Option.defaultValue 200
    | _ ->
        failwithf "Unexpected result type: %s" (result.GetType().FullName)

let private getPayloadJson (result: IActionResult) : string =
    match result with
    | :? ObjectResult as objectResult -> JsonSerializer.Serialize(objectResult.Value)
    | _ -> failwithf "Expected ObjectResult payload but received %s" (result.GetType().FullName)

let private expectOkValue<'a> (result: IActionResult) : 'a =
    match result with
    | :? OkObjectResult as okResult -> okResult.Value :?> 'a
    | _ -> failwithf "Expected OkObjectResult but received %s" (result.GetType().FullName)

[<Fact>]
let ``smoke analyze computes schedule and metadata`` () =
    let request =
        createSingleRequest
            [ createTask "A" 3.0 []
              createTask "B" 2.0 [ "A" ]
              createTask "C" 5.0 [ "A" ]
              createTask "D" 1.0 [ "B"; "C" ] ]
            None

    match CpmEngine.analyze English request with
    | Ok result ->
        Assert.Equal<float>(9.0, result.project_duration)
        Assert.Equal<string list>([ "A"; "C"; "D" ], result.critical_path)
        Assert.Equal("days", result.duration_unit)
        Assert.Equal(4, result.meta.task_count)
        Assert.Equal(4, result.meta.edge_count)

        let maybeTaskB = result.tasks |> List.tryFind (fun taskResult -> taskResult.id = "B")

        match maybeTaskB with
        | Some taskB -> Assert.Equal<float>(3.0, taskB.float)
        | None -> failwith "Expected task B in response"
    | Error error ->
        failwithf "Expected success, received %A" error

[<Fact>]
let ``smoke analyze rejects empty task list`` () =
    let request = createSingleRequest [] None

    match CpmEngine.analyze English request with
    | Error EmptyTaskList -> ()
    | other -> failwithf "Expected EmptyTaskList, received %A" other

[<Fact>]
let ``smoke analyze rejects duplicate ids`` () =
    let request =
        createSingleRequest
            [ createTask "A" 1.0 []
              createTask "A" 2.0 [] ]
            None

    match CpmEngine.analyze English request with
    | Error (DuplicateTaskId "A") -> ()
    | other -> failwithf "Expected DuplicateTaskId, received %A" other

[<Fact>]
let ``smoke analyze rejects unknown dependency`` () =
    let request =
        createSingleRequest
            [ createTask "A" 1.0 [ "UNKNOWN" ] ]
            None

    match CpmEngine.analyze English request with
    | Error (UnknownDependency ("A", "UNKNOWN")) -> ()
    | other -> failwithf "Expected UnknownDependency, received %A" other

[<Fact>]
let ``smoke analyze rejects negative duration`` () =
    let request =
        createSingleRequest
            [ createTask "A" -1.0 [] ]
            None

    match CpmEngine.analyze English request with
    | Error (NegativeDuration "A") -> ()
    | other -> failwithf "Expected NegativeDuration, received %A" other

[<Fact>]
let ``smoke analyze normalizes supported duration units`` () =
    let options = createOptions (Some "WEEKS") None None |> Some
    let request = createSingleRequest [ createTask "A" 1.0 [] ] options

    match CpmEngine.analyze English request with
    | Ok result -> Assert.Equal("weeks", result.duration_unit)
    | Error error -> failwithf "Expected success, received %A" error

[<Fact>]
let ``smoke analyze rejects invalid duration unit`` () =
    let options = createOptions (Some "quarters") None None |> Some
    let request = createSingleRequest [ createTask "A" 1.0 [] ] options

    match CpmEngine.analyze English request with
    | Error (InvalidDurationUnit "quarters") -> ()
    | other -> failwithf "Expected InvalidDurationUnit, received %A" other

[<Fact>]
let ``smoke analyze rejects graph larger than max tasks`` () =
    let request = createSingleRequest oversizedTaskList.Value None

    match CpmEngine.analyze English request with
    | Error (GraphTooLarge count) -> Assert.Equal(CpmEngine.MAX_TASKS + 1, count)
    | other -> failwithf "Expected GraphTooLarge, received %A" other

[<Fact>]
let ``smoke analyze rejects task with too many dependencies`` () =
    let dependencyIds = recBuildStrings "DEP-" (CpmEngine.MAX_DEPENDENCIES_PER_TASK + 1)

    let dependencyTasks =
        dependencyIds
        |> List.map (fun dependencyId -> createTask dependencyId 1.0 [])

    let dependentTask = createTask "MAIN" 1.0 dependencyIds

    let request =
        createSingleRequest
            (dependentTask :: dependencyTasks)
            None

    match CpmEngine.analyze English request with
    | Error (InvalidInput message) ->
        Assert.Contains("too many dependencies", message)
    | other ->
        failwithf "Expected InvalidInput for dependency count, received %A" other

[<Fact>]
let ``smoke threshold calculation handles defaults numbers and objects`` () =
    let defaultThreshold = CpmEngine.calculateThreshold None 50.0
    let numericThreshold = CpmEngine.calculateThreshold (Some (Number 7.0)) 50.0

    let objectThreshold =
        CpmEngine.calculateThreshold
            (Some (Object { absolute = Some 4.0; percentage = Some 20.0 }))
            50.0

    Assert.Equal<float>(10.0, defaultThreshold)
    Assert.Equal<float>(7.0, numericThreshold)
    Assert.Equal<float>(10.0, objectThreshold)

[<Fact>]
let ``smoke disconnected subgraph warning identifies unreachable nodes`` () =
    let nodeA = createNode "A" 1.0 [] [] 0.0
    let nodeB = createNode "B" 1.0 [] [] 0.0

    let graph =
        { nodes = Map.ofList [ "A", nodeA; "B", nodeB ]
          edges = []
          start_nodes = [ "A" ]
          end_nodes = [ "A"; "B" ] }

    let warnings = CpmEngine.detectDisconnectedSubgraphs graph English

    Assert.Single(warnings) |> ignore
    Assert.Equal("disconnected_subgraph", warnings.Head.type_)
    Assert.Contains("B", warnings.Head.affected_tasks)

[<Fact>]
let ``smoke zero duration warning identifies zero tasks`` () =
    let nodes =
        Map.ofList
            [ "A", createNode "A" 0.0 [] [] 0.0
              "B", createNode "B" 2.0 [] [] 0.0 ]

    let warnings = CpmEngine.detectZeroDurationPaths nodes English

    Assert.Single(warnings) |> ignore
    Assert.Equal("zero_duration_path", warnings.Head.type_)
    Assert.Contains("A", warnings.Head.affected_tasks)

[<Fact>]
let ``smoke batch analyze supports mixed success and error results`` () =
    let validProject =
        createBatchProject
            "valid"
            [ createTask "A" 1.0 []
              createTask "B" 2.0 [ "A" ] ]
            None

    let cyclicProject =
        createBatchProject
            "cyclic"
            [ createTask "A" 1.0 [ "B" ]
              createTask "B" 1.0 [ "A" ] ]
            None

    let request = createBatchRequest [ validProject; cyclicProject ]

    match CpmEngine.analyzeBatch English request with
    | Ok result ->
        Assert.Equal(2, result.meta.total_projects)
        Assert.Equal(1, result.meta.succeeded)
        Assert.Equal(1, result.meta.failed)

        let validResult = result.results |> List.find (fun item -> item.id = "valid")
        let cyclicResult = result.results |> List.find (fun item -> item.id = "cyclic")

        Assert.Equal("ok", validResult.status)
        Assert.Equal("error", cyclicResult.status)

        match cyclicResult.error with
        | Some errorInfo -> Assert.Equal("CIRCULAR_DEPENDENCY", errorInfo.code)
        | None -> failwith "Expected error details for cyclic project"
    | Error error ->
        failwithf "Expected batch success with partial failures, received %A" error

[<Fact>]
let ``smoke batch analyze rejects too many projects`` () =
    let projectCount = CpmEngine.MAX_BATCH_PROJECTS + 1

    let rec buildProjects index projects =
        match index > projectCount with
        | true -> List.rev projects
        | false ->
            let nextProject = createBatchProject (sprintf "project-%i" index) [ createTask "A" 1.0 [] ] None
            buildProjects (index + 1) (nextProject :: projects)

    let request = createBatchRequest (buildProjects 1 [])

    match CpmEngine.analyzeBatch English request with
    | Error (InvalidInput message) -> Assert.Contains(string CpmEngine.MAX_BATCH_PROJECTS, message)
    | other -> failwithf "Expected InvalidInput for project limit, received %A" other

[<Fact>]
let ``smoke batch analyze rejects too many total tasks`` () =
    let oversizedProject = createBatchProject "oversized" oversizedBatchTaskList.Value None
    let request = createBatchRequest [ oversizedProject ]

    match CpmEngine.analyzeBatch English request with
    | Error (InvalidInput message) -> Assert.Contains(string CpmEngine.MAX_BATCH_TASKS, message)
    | other -> failwithf "Expected InvalidInput for total task limit, received %A" other

[<Fact>]
let ``smoke localization query language has priority over header`` () =
    let context = DefaultHttpContext()
    context.Request.QueryString <- QueryString("?lang=en")
    context.Request.Headers["Accept-Language"] <- StringValues("vi")

    let language = Localization.getLanguage context

    Assert.Equal(English, language)

[<Fact>]
let ``smoke localization reads header when query missing`` () =
    let context = DefaultHttpContext()
    context.Request.Headers["Accept-Language"] <- StringValues("vi")

    let language = Localization.getLanguage context

    Assert.Equal(Vietnamese, language)

[<Fact>]
let ``smoke localization defaults to english`` () =
    let context = DefaultHttpContext()
    let language = Localization.getLanguage context
    Assert.Equal(English, language)

[<Fact>]
let ``smoke threshold converter deserializes number`` () =
    let options = JsonSerializerOptions()
    options.Converters.Add(ThresholdValueConverter())

    let result = JsonSerializer.Deserialize<ThresholdValue>("2.5", options)

    match result with
    | Number value -> Assert.Equal<float>(2.5, value)
    | other -> failwithf "Expected Number threshold, received %A" other

[<Fact>]
let ``smoke threshold converter deserializes object`` () =
    let options = JsonSerializerOptions()
    options.Converters.Add(ThresholdValueConverter())

    let result = JsonSerializer.Deserialize<ThresholdValue>("{\"absolute\":4,\"percentage\":20}", options)

    match result with
    | Object config ->
        Assert.Equal<float>(4.0, config.absolute.Value)
        Assert.Equal<float>(20.0, config.percentage.Value)
    | other ->
        failwithf "Expected Object threshold, received %A" other

[<Fact>]
let ``smoke threshold converter serializes object`` () =
    let options = JsonSerializerOptions()
    options.Converters.Add(ThresholdValueConverter())

    let value = Object { absolute = Some 4.0; percentage = Some 20.0 }
    let json = JsonSerializer.Serialize(value, options)

    Assert.Contains("\"absolute\":4", json)
    Assert.Contains("\"percentage\":20", json)

[<Fact>]
let ``smoke controller analyze returns ok on valid request`` () =
    let controller = createController None None

    let request =
        createSingleRequest
            [ createTask "A" 1.0 []
              createTask "B" 2.0 [ "A" ] ]
            None

    let result = controller.Analyze(request)
    let payload = expectOkValue<CpmResult> result

    Assert.Equal(200, getStatusCode result)
    Assert.Equal<string list>([ "A"; "B" ], payload.critical_path)

[<Fact>]
let ``smoke controller analyze maps unknown dependency to bad request`` () =
    let controller = createController None None
    let request = createSingleRequest [ createTask "A" 1.0 [ "MISSING" ] ] None

    let result = controller.Analyze(request)
    let payloadJson = getPayloadJson result

    Assert.Equal(400, getStatusCode result)
    Assert.Contains("\"code\":\"UNKNOWN_DEPENDENCY\"", payloadJson)

[<Fact>]
let ``smoke controller analyze maps graph too large to status 413`` () =
    let controller = createController None None
    let request = createSingleRequest oversizedTaskList.Value None

    let result = controller.Analyze(request)
    let payloadJson = getPayloadJson result

    Assert.Equal(413, getStatusCode result)
    Assert.Contains("\"code\":\"GRAPH_TOO_LARGE\"", payloadJson)

[<Fact>]
let ``smoke controller analyze maps negative duration to status 422`` () =
    let controller = createController None None
    let request = createSingleRequest [ createTask "A" -1.0 [] ] None

    let result = controller.Analyze(request)
    let payloadJson = getPayloadJson result

    Assert.Equal(422, getStatusCode result)
    Assert.Contains("\"code\":\"NEGATIVE_DURATION\"", payloadJson)

[<Fact>]
let ``smoke controller analyze uses query language over header`` () =
    let controller = createController (Some "en") (Some "vi")
    let request = createSingleRequest [] None

    let result = controller.Analyze(request)
    let payloadJson = getPayloadJson result

    Assert.Equal(400, getStatusCode result)
    Assert.Contains("Task list cannot be empty", payloadJson)

[<Fact>]
let ``smoke controller batch analyze returns ok with mixed project statuses`` () =
    let controller = createController None None

    let validProject = createBatchProject "valid" [ createTask "A" 1.0 [] ] None

    let invalidProject =
        createBatchProject
            "invalid"
            [ createTask "A" 1.0 [ "B" ]
              createTask "B" 1.0 [ "A" ] ]
            None

    let request = createBatchRequest [ validProject; invalidProject ]
    let result = controller.BatchAnalyze(request)
    let payload = expectOkValue<BatchResult> result

    Assert.Equal(200, getStatusCode result)
    Assert.Equal(2, payload.results.Length)

    let invalidResult = payload.results |> List.find (fun item -> item.id = "invalid")
    Assert.Equal("error", invalidResult.status)

[<Fact>]
let ``smoke controller batch analyze returns bad request for invalid batch limits`` () =
    let controller = createController None None
    let projectCount = CpmEngine.MAX_BATCH_PROJECTS + 1

    let rec buildProjects index projects =
        match index > projectCount with
        | true -> List.rev projects
        | false ->
            let nextProject = createBatchProject (sprintf "project-%i" index) [ createTask "A" 1.0 [] ] None
            buildProjects (index + 1) (nextProject :: projects)

    let request = createBatchRequest (buildProjects 1 [])
    let result = controller.BatchAnalyze(request)
    let payloadJson = getPayloadJson result

    Assert.Equal(400, getStatusCode result)
    Assert.Contains("\"code\":\"INVALID_INPUT\"", payloadJson)