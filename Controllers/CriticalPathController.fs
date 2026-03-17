namespace CriticalPathExtractor.Controllers

open Microsoft.AspNetCore.Mvc
open Microsoft.Extensions.Logging
open System
open CriticalPathExtractor.Types
open CriticalPathExtractor
open CriticalPathExtractor.Infrastructure

/// <summary>
/// Critical Path Method (CPM) Analysis API
/// 
/// Provides endpoints for analyzing project task graphs to identify critical paths,
/// calculate float values, and detect scheduling issues.
/// 
/// Language Support:
/// - Use ?lang= query parameter (e.g., ?lang=vi) or Accept-Language header
/// - Supported values: en (English), vi (Vietnamese)
/// - Query parameter takes priority over header
/// - Defaults to English if not specified or unsupported
/// </summary>
[<ApiController>]
[<Route("v1/critical-path")>]
type CriticalPathController (logger : ILogger<CriticalPathController>) =
    inherit ControllerBase()
    
    static member private CreateObjectResult (statusCode: int) (content: obj) : ObjectResult =
        let result = ObjectResult(content)
        result.StatusCode <- Nullable(statusCode)
        result

    /// <summary>
    /// Analyze a single project task graph
    /// 
    /// Performs CPM analysis on a project task graph to calculate:
    /// - Critical path (tasks with zero float)
    /// - Float values for all tasks
    /// - Near-critical paths (within threshold)
    /// - Scheduling warnings
    /// 
    /// Language: Use ?lang= query parameter or Accept-Language header (en/vi)
    /// </summary>
    /// <param name="request">Project request containing tasks, dependencies, and options</param>
    /// <returns>CPM analysis result with critical path, task schedules, and warnings</returns>
    /// <response code="200">Analysis completed successfully</response>
    /// <response code="400">Invalid input, circular dependency, unknown dependency, or duplicate task ID</response>
    /// <response code="413">Graph too large (exceeds 10,000 tasks)</response>
    /// <response code="422">Negative duration detected</response>
    /// <response code="500">Internal server error</response>
    [<HttpPost>]
    [<Route("")>]
    [<ProducesResponseType(typeof<CpmResult>, 200)>]
    [<ProducesResponseType(typeof<obj>, 400)>]
    [<ProducesResponseType(typeof<obj>, 413)>]
    [<ProducesResponseType(typeof<obj>, 422)>]
    [<ProducesResponseType(typeof<obj>, 500)>]
    [<RequestSizeLimit(5L * 1024L * 1024L)>]  // 5MB limit for single requests
    member _.Analyze([<FromBody>] request: SingleProjectRequest) : IActionResult =
        // Resolve language from query param or header
        let lang = Localization.getLanguage base.HttpContext
        
        try
            let result = CpmEngine.analyze request lang
            OkObjectResult(result) :> IActionResult
        with
        | CpmValidationException validationError ->
            let errorCode =
                match validationError with
                | Types.CircularDependency _ -> "CIRCULAR_DEPENDENCY"
                | Types.UnknownDependency _ -> "UNKNOWN_DEPENDENCY"
                | Types.DuplicateTaskId _ -> "DUPLICATE_TASK_ID"
                | Types.GraphTooLarge _ -> "GRAPH_TOO_LARGE"
                | Types.NegativeDuration _ -> "NEGATIVE_DURATION"
                | Types.EmptyTaskList -> "INVALID_INPUT"
                | Types.InvalidDurationUnit _ -> "INVALID_INPUT"
                | Types.InvalidInput _ -> "INVALID_INPUT"
            
            let affectedTasks =
                match validationError with
                | Types.CircularDependency tasks -> tasks
                | Types.UnknownDependency (taskId, _) -> [taskId]
                | Types.DuplicateTaskId taskId -> [taskId]
                | Types.NegativeDuration taskId -> [taskId]
                | _ -> []
            
            let message = CpmEngine.getValidationErrorMessage validationError lang
            
            let errorContent = {|
                error = {|
                    code = errorCode
                    message = message
                    affected_tasks = affectedTasks
                |}
            |}
            
            match validationError with
            | Types.GraphTooLarge _ -> CriticalPathController.CreateObjectResult 413 errorContent :> IActionResult
            | NegativeDuration _ -> CriticalPathController.CreateObjectResult 422 errorContent :> IActionResult
            | _ -> BadRequestObjectResult(errorContent) :> IActionResult
            
        | ex ->
            logger.LogError(ex, "Unexpected error during CPM analysis")
            CriticalPathController.CreateObjectResult 500 {|
                error = {|
                    code = "INTERNAL_ERROR"
                    message = Localization.getStringSimple lang MsgInternalError
                    affected_tasks = []
                |}
            |} :> IActionResult

    /// <summary>
    /// Analyze multiple project task graphs in batch
    /// 
    /// Performs CPM analysis on multiple independent project graphs.
    /// Each project is processed independently and results are returned
    /// in the same order as the input.
    /// 
    /// Language: Use ?lang= query parameter or Accept-Language header (en/vi)
    /// </summary>
    /// <param name="request">Batch request containing multiple project graphs</param>
    /// <returns>Batch results with individual project analyses and summary statistics</returns>
    /// <response code="200">Batch analysis completed successfully</response>
    /// <response code="400">Invalid batch input (too many projects or total tasks)</response>
    /// <response code="500">Internal server error</response>
    [<HttpPost>]
    [<Route("batch")>]
    [<ProducesResponseType(typeof<BatchResult>, 200)>]
    [<ProducesResponseType(typeof<obj>, 400)>]
    [<ProducesResponseType(typeof<obj>, 500)>]
    [<RequestSizeLimit(20L * 1024L * 1024L)>]  // 20MB limit for batch requests
    member _.BatchAnalyze([<FromBody>] request: BatchRequest) : IActionResult =
        // Resolve language from query param or header
        let lang = Localization.getLanguage base.HttpContext
        
        try
            let result = CpmEngine.analyzeBatch request lang
            OkObjectResult(result) :> IActionResult
        with
        | CpmValidationException (Types.InvalidInput msg) ->
            BadRequestObjectResult({|
                error = {|
                    code = "INVALID_INPUT"
                    message = msg
                    affected_tasks = []
                |}
            |}) :> IActionResult
        | ex ->
            logger.LogError(ex, "Unexpected error during batch CPM analysis")
            CriticalPathController.CreateObjectResult 500 {|
                error = {|
                    code = "INTERNAL_ERROR"
                    message = Localization.getStringSimple lang MsgInternalError
                    affected_tasks = []
                |}
            |} :> IActionResult
