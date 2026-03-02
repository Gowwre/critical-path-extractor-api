namespace CriticalPathExtractor.Infrastructure

open System
open Microsoft.AspNetCore.Http

/// <summary>
/// Supported language codes
/// </summary>
type LanguageCode =
    | English
    | Vietnamese

/// <summary>
/// Message keys used by localized strings (prefixed to avoid conflicts with ValidationError)
/// </summary>
type LocalizationKey =
    | MsgUnknownDependency
    | MsgDuplicateTaskId
    | MsgGraphTooLarge
    | MsgNegativeDuration
    | MsgEmptyTaskList
    | MsgInvalidDurationUnit
    | MsgInternalError
    | MsgDisconnectedSubgraph
    | MsgZeroDurationPath
    | MsgTooManyDependencies
    | MsgTooManyProjects
    | MsgTooManyTotalTasks
    | MsgCircularDependency

module Localization =
    
    /// <summary>
    /// Default language when none specified or unsupported
    /// </summary>
    let defaultLanguage = English
    
    /// <summary>
    /// Parse language code from string
    /// </summary>
    let parseLanguage (lang: string) : LanguageCode =
        match lang.ToLowerInvariant() with
        | "vi" | "vi-vn" | "vi_vn" -> Vietnamese
        | _ -> English  // Defaults to English on any other code
    
    /// <summary>
    /// Get language from request (query param takes priority over header)
    /// </summary>
    let getLanguage (httpContext: HttpContext) : LanguageCode =
        let queryResult = httpContext.Request.Query.TryGetValue("lang")
        let headerResult = httpContext.Request.Headers.TryGetValue("Accept-Language")

        let parsePrimaryHeader (headerValue: string) : LanguageCode =
            headerValue.Split(',')
            |> Array.tryHead
            |> Option.map (fun item -> item.Trim().Split(';').[0])
            |> Option.defaultValue "en"
            |> parseLanguage

        match queryResult with
        | true, queryValue ->
            parseLanguage (queryValue.ToString())
        | false, _ ->
            match headerResult with
            | true, headerValue -> parsePrimaryHeader (headerValue.ToString())
            | false, _ -> defaultLanguage
    
    /// <summary>
    /// Get localized string with optional parameters
    /// </summary>
    let getString (lang: LanguageCode) (key: LocalizationKey) (args: obj list) : string =
        let template = 
            match lang with
            | English ->
                match key with
                | MsgUnknownDependency -> "Task '{0}' references unknown dependency '{1}'"
                | MsgDuplicateTaskId -> "Duplicate task ID found: '{0}'"
                | MsgGraphTooLarge -> "Graph contains {0} tasks (max: {1})"
                | MsgNegativeDuration -> "Task '{0}' has a negative duration"
                | MsgEmptyTaskList -> "Task list cannot be empty"
                | MsgInvalidDurationUnit -> "Invalid duration unit: '{0}'. Valid values are: hours, days, weeks"
                | MsgInternalError -> "An internal error occurred during request processing"
                | MsgDisconnectedSubgraph -> "Some tasks are not reachable from any start node"
                | MsgZeroDurationPath -> "Some tasks have zero duration; this may affect scheduling accuracy"
                | MsgTooManyDependencies -> "Task {0} has too many dependencies (max: {1})"
                | MsgTooManyProjects -> "Batch contains too many projects (max: {0})"
                | MsgTooManyTotalTasks -> "Batch contains too many total tasks (max: {0})"
                | MsgCircularDependency -> "The task graph contains a cycle and cannot be analysed."
            | Vietnamese ->
                match key with
                | MsgUnknownDependency -> "Tác vụ '{0}' tham chiếu đến phụ thuộc không xác định '{1}'"
                | MsgDuplicateTaskId -> "Tìm thấy ID tác vụ trùng lặp: '{0}'"
                | MsgGraphTooLarge -> "Đồ thị chứa {0} tác vụ (tối đa: {1})"
                | MsgNegativeDuration -> "Tác vụ '{0}' có thờI gian âm"
                | MsgEmptyTaskList -> "Danh sách tác vụ không được để trống"
                | MsgInvalidDurationUnit -> "Đơn vị thờI gian không hợp lệ: '{0}'. Các giá trị hợp lệ: hours, days, weeks"
                | MsgInternalError -> "Đã xảy ra lỗI nộI bộ khI xử lý yêu cầu"
                | MsgDisconnectedSubgraph -> "Một số tác vụ không thể truy cập từ bất kỳ nút bắt đầu nào"
                | MsgZeroDurationPath -> "Một số tác vụ có thờI gian bằng 0; đIều này có thể ảnh hưởng đến độ chính xác của lịch trình"
                | MsgTooManyDependencies -> "Tác vụ {0} có quá nhiều phụ thuộc (tối đa: {1})"
                | MsgTooManyProjects -> "Lô chứa quá nhiều dự án (tối đa: {0})"
                | MsgTooManyTotalTasks -> "Lô chứa quá nhiều tác vụ tổng cộng (tối đa: {0})"
                | MsgCircularDependency -> "Đồ thị tác vụ chứa chu kỳ và không thể phân tích."
        
        String.Format(template, args |> List.toArray)
    
    /// <summary>
    /// Convenience method to get localized string without parameters
    /// </summary>
    let getStringSimple (lang: LanguageCode) (key: LocalizationKey) : string =
        getString lang key []
