namespace CriticalPathExtractor

open System
open System.Collections.Generic

module Types =
    type DurationUnit = Hours | Days | Weeks

    type CalendarConfig = {
        working_days: string list option
        daily_hours: int option
        holidays: string list option
        timezone: string option
    }

    type NearCriticalThreshold = {
        absolute: float option
        percentage: float option
    }

    type ThresholdValue =
        | Number of float
        | Object of NearCriticalThreshold

    type RequestOptions = {
        duration_unit: string option
        near_critical_threshold: ThresholdValue option
        include_all_paths: bool option
        project_start: string option
        lang: string option
    }

    type TaskRequest = {
        id: string
        label: string option
        duration: float
        dependencies: string list
        calendar_id: string option
    }

    type SingleProjectRequest = {
        tasks: TaskRequest list
        calendars: Map<string, CalendarConfig> option
        options: RequestOptions option
    }

    type BatchProjectRequest = {
        id: string
        tasks: TaskRequest list
        calendars: Map<string, CalendarConfig> option
        options: RequestOptions option
    }

    type BatchRequest = {
        projects: BatchProjectRequest list
    }

    type TaskResult = {
        id: string
        label: string
        duration: float
        earliest_start: float
        earliest_finish: float
        latest_start: float
        latest_finish: float
        float: float
        is_critical: bool
        is_near_critical: bool
        earliest_start_date: string option
        earliest_finish_date: string option
    }

    type NearCriticalPath = {
        path: string list
        duration: float
        float: float
        risk_label: string
    }

    type Warning = {
        type_: string
        affected_tasks: string list
        message: string
    }

    type MetaResult = {
        task_count: int
        edge_count: int
        computation_ms: int64
    }

    type CpmResult = {
        project_duration: float
        duration_unit: string
        critical_path: string list
        tasks: TaskResult list
        near_critical_paths: NearCriticalPath list
        warnings: Warning list
        meta: MetaResult
    }

    type ErrorInfo = {
        code: string
        message: string
        affected_tasks: string list
    }

    type BatchMeta = {
        total_projects: int
        succeeded: int
        failed: int
        total_computation_ms: int64
    }

    type BatchResultItem = {
        id: string
        status: string
        project_duration: float option
        critical_path: string list option
        tasks: TaskResult list option
        near_critical_paths: NearCriticalPath list option
        warnings: Warning list option
        meta: MetaResult option
        error: ErrorInfo option
    }

    type BatchResult = {
        results: BatchResultItem list
        meta: BatchMeta
    }

    type TaskNode = {
        id: string
        label: string
        duration: float
        dependencies: string list
        successors: string list
        calendar_id: string option
        es: float
        ef: float
        ls: float
        lf: float
        total_float: float
    }

    type Graph = {
        nodes: Map<string, TaskNode>
        edges: (string * string) list
        start_nodes: string list
        end_nodes: string list
    }

    type ValidationError =
        | InvalidInput of string
        | CircularDependency of string list
        | UnknownDependency of string * string
        | DuplicateTaskId of string
        | GraphTooLarge of int
        | NegativeDuration of string
        | EmptyTaskList
        | InvalidDurationUnit of string

    exception CpmValidationException of ValidationError
