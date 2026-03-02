namespace CriticalPathExtractor.Controllers

open Microsoft.AspNetCore.Mvc
open Microsoft.Extensions.Logging
open System
open CriticalPathExtractor.Types
open CriticalPathExtractor
open CriticalPathExtractor.Infrastructure

module private HttpMapping =
    let toErrorPayload code message affectedTasks =
        {| error = {| code = code; message = message; affected_tasks = affectedTasks |} |}

    let toObjectResult (statusCode: int) payload : IActionResult =
        let result = ObjectResult(payload)
        result.StatusCode <- Nullable(statusCode)
        result :> IActionResult

    let toValidationResult (lang: LanguageCode) (validationError: ValidationError) : IActionResult =
        let payload =
            toErrorPayload
                (CpmEngine.getValidationErrorCode validationError)
                (CpmEngine.getValidationErrorMessage validationError lang)
                (CpmEngine.getValidationAffectedTasks validationError)

        match CpmEngine.getValidationStatusCode validationError with
        | 400 -> BadRequestObjectResult(payload) :> IActionResult
        | statusCode -> toObjectResult statusCode payload

    let toInternalErrorResult (lang: LanguageCode) : IActionResult =
        toErrorPayload
            "INTERNAL_ERROR"
            (Localization.getStringSimple lang MsgInternalError)
            []
        |> toObjectResult 500

/// <summary>
/// Critical Path Method (CPM) Analysis API
///
/// Provides endpoints that analyze project task graphs to identify critical paths,
/// calculate float values, and detect scheduling issues.
///
/// Language Support:
/// - Use ?lang= query parameter (e.g., ?lang=vi) or Accept-Language header
/// - Supported values: en (English), vi (Vietnamese)
/// - Query parameter takes priority over header
/// - Defaults to English when unspecified or unsupported
/// </summary>
[<ApiController>]
[<Route("v1/critical-path")>]
type CriticalPathController (logger : ILogger<CriticalPathController>) =
    inherit ControllerBase()

    /// <summary>
    /// Analyze a single project task graph
    ///
    /// Performs CPM analysis on a project task graph to calculate:
    /// - Critical path (tasks with zero float)
    /// - Float values across all tasks
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
    [<RequestSizeLimit(5L * 1024L * 1024L)>]  // 5MB limit on single requests
    member _.Analyze([<FromBody>] request: SingleProjectRequest) : IActionResult =
        let lang = Localization.getLanguage base.HttpContext

        try
            request
            |> CpmEngine.analyze lang
            |> Result.map (fun analysisResult -> OkObjectResult(analysisResult) :> IActionResult)
            |> Result.defaultWith (HttpMapping.toValidationResult lang)
        with
        | ex ->
            logger.LogError(ex, "Unexpected error during CPM analysis")
            HttpMapping.toInternalErrorResult lang

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
    [<RequestSizeLimit(20L * 1024L * 1024L)>]  // 20MB limit on batch requests
    member _.BatchAnalyze([<FromBody>] request: BatchRequest) : IActionResult =
        let lang = Localization.getLanguage base.HttpContext

        try
            request
            |> CpmEngine.analyzeBatch lang
            |> Result.map (fun batchResult -> OkObjectResult(batchResult) :> IActionResult)
            |> Result.defaultWith (HttpMapping.toValidationResult lang)
        with
        | ex ->
            logger.LogError(ex, "Unexpected error during batch CPM analysis")
            HttpMapping.toInternalErrorResult lang