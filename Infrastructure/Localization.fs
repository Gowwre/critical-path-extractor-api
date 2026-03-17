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
/// Message keys for localized strings (prefixed to avoid conflicts with ValidationError)
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
        | _ -> English  // Default to English for any other code
    
    /// <summary>
    /// Get language from request (query param takes priority over header)
    /// </summary>
    let getLanguage (httpContext: HttpContext) : LanguageCode =
        let tryGetQuery () =
            match httpContext.Request.Query.TryGetValue("lang") with
            | true, v -> Some (parseLanguage (v.ToString()))
            | false, _ -> None
        
        let tryGetHeader () =
            match httpContext.Request.Headers.TryGetValue("Accept-Language") with
            | true, v ->
                let primaryLang = 
                    v.ToString().Split(',')
                    |> Array.tryHead
                    |> Option.map (fun s -> s.Trim().Split(';').[0])
                    |> Option.defaultValue "en"
                Some (parseLanguage primaryLang)
            | false, _ -> None
        
        tryGetQuery ()
        |> Option.orElseWith tryGetHeader
        |> Option.defaultValue defaultLanguage
    
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
                | MsgInternalError -> "An internal error occurred while processing the request"
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
