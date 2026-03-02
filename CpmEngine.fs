namespace CriticalPathExtractor

open System
open CriticalPathExtractor.Types
open CriticalPathExtractor.Infrastructure

module CpmEngine =

    let MAX_TASKS = 10000
    let MAX_DEPENDENCIES_PER_TASK = 500
    let MAX_BATCH_PROJECTS = 100
    let MAX_BATCH_TASKS = 50000

    type private AnalysisConfig = {
        duration_unit: DurationUnit
        include_all_paths: bool
        threshold_value: ThresholdValue option
    }

    type private GraphComputation = {
        graph: Graph
        sorted_nodes: string list
        nodes: Map<string, TaskNode>
    }

    module private DurationUnitCodec =
        let parse (durationUnit: string) : AnalysisResult<DurationUnit> =
            match durationUnit.ToLowerInvariant() with
            | "hours" -> Ok Hours
            | "days" -> Ok Days
            | "weeks" -> Ok Weeks
            | _ -> Error (InvalidDurationUnit durationUnit)

        let toApiValue (durationUnit: DurationUnit) : string =
            match durationUnit with
            | Hours -> "hours"
            | Days -> "days"
            | Weeks -> "weeks"

    module private ImmutableQueue =
        type T<'a> = {
            front: 'a list
            back: 'a list
        }

        let ofList (items: 'a list) : T<'a> =
            { front = items
              back = [] }

        let enqueue (item: 'a) (queue: T<'a>) : T<'a> =
            { queue with back = item :: queue.back }

        let private normalize (queue: T<'a>) : T<'a> =
            match queue.front with
            | [] ->
                { front = List.rev queue.back
                  back = [] }
            | _ ->
                queue

        let tryDequeue (queue: T<'a>) : ('a * T<'a>) option =
            let normalized = normalize queue

            match normalized.front with
            | item :: remaining ->
                Some (item, { normalized with front = remaining })
            | [] ->
                None

    let private bind = Result.bind
    let private map = Result.map

    let private validateDurationUnit = DurationUnitCodec.parse

    let private mapTaskToNode (task: TaskRequest) : TaskNode =
        { id = task.id
          label = task.label |> Option.defaultValue task.id
          duration = task.duration
          dependencies = task.dependencies
          successors = []
          calendar_id = task.calendar_id
          es = 0.0
          ef = 0.0
          ls = 0.0
          lf = 0.0
          total_float = 0.0 }

    let private validateTaskListNotEmpty (tasks: TaskRequest list) : AnalysisResult<TaskRequest list> =
        match tasks with
        | [] -> Error EmptyTaskList
        | _ -> Ok tasks

    let private validateTaskListSize (tasks: TaskRequest list) : AnalysisResult<TaskRequest list> =
        match tasks.Length with
        | length when length > MAX_TASKS -> Error (GraphTooLarge length)
        | _ -> Ok tasks

    let private tryFindDuplicateTaskId (taskIds: string list) : string option =
        taskIds
        |> Seq.groupBy id
        |> Seq.tryFind (fun (_, items) -> Seq.length items > 1)
        |> Option.map fst

    let private validateUniqueTaskIds (tasks: TaskRequest list) : AnalysisResult<Set<string>> =
        let taskIds = tasks |> List.map (fun task -> task.id)

        match tryFindDuplicateTaskId taskIds with
        | Some duplicateId -> Error (DuplicateTaskId duplicateId)
        | None -> Ok (Set.ofList taskIds)

    let private validateTaskDuration (task: TaskRequest) : AnalysisResult<TaskRequest> =
        match task.duration with
        | duration when duration < 0.0 -> Error (NegativeDuration task.id)
        | _ -> Ok task

    let private validateTaskDependencyCount (task: TaskRequest) : AnalysisResult<TaskRequest> =
        match task.dependencies.Length with
        | dependencyCount when dependencyCount > MAX_DEPENDENCIES_PER_TASK ->
            Error (InvalidInput (sprintf "Task %s has too many dependencies (max: %d)" task.id MAX_DEPENDENCIES_PER_TASK))
        | _ -> Ok task

    let private validateTaskDependencies (allTaskIds: Set<string>) (task: TaskRequest) : AnalysisResult<TaskRequest> =
        task.dependencies
        |> List.tryFind (fun dependencyId -> not (Set.contains dependencyId allTaskIds))
        |> Option.map (fun dependencyId -> UnknownDependency (task.id, dependencyId))
        |> function
            | Some validationError -> Error validationError
            | None -> Ok task

    let private validateTask (allTaskIds: Set<string>) : TaskRequest -> AnalysisResult<TaskRequest> =
        validateTaskDuration
        >> bind validateTaskDependencyCount
        >> bind (validateTaskDependencies allTaskIds)

    let private validateAll
        (validator: 'a -> AnalysisResult<'a>)
        (items: 'a list)
        : AnalysisResult<'a list> =
        let folder state item =
            state
            |> bind (fun acc ->
                validator item
                |> map (fun validatedItem -> validatedItem :: acc))

        items
        |> List.fold folder (Ok [])
        |> map List.rev

    let private mapTasksToNodes (tasks: TaskRequest list) : Map<string, TaskNode> =
        tasks
        |> List.map (fun task -> task.id, mapTaskToNode task)
        |> Map.ofList

    let private addSuccessorToDependency
        (successorId: string)
        (dependencyId: string)
        (nodes: Map<string, TaskNode>)
        : Map<string, TaskNode> =
        match Map.tryFind dependencyId nodes with
        | Some dependencyNode ->
            let updatedNode = { dependencyNode with successors = successorId :: dependencyNode.successors }
            Map.add dependencyId updatedNode nodes
        | None ->
            nodes

    let private attachSuccessors
        (tasks: TaskRequest list)
        (nodes: Map<string, TaskNode>)
        : Map<string, TaskNode> =
        tasks
        |> List.fold
            (fun currentNodes task ->
                task.dependencies
                |> List.fold (fun acc dependencyId -> addSuccessorToDependency task.id dependencyId acc) currentNodes)
            nodes

    let private getStartNodes (tasks: TaskRequest list) : string list =
        tasks
        |> List.filter (fun task -> task.dependencies.IsEmpty)
        |> List.map (fun task -> task.id)

    let private getEndNodes (nodes: Map<string, TaskNode>) : string list =
        nodes
        |> Map.toList
        |> List.filter (fun (_, node) -> node.successors.IsEmpty)
        |> List.map fst

    let private getEdges (tasks: TaskRequest list) : (string * string) list =
        tasks
        |> List.collect (fun task -> task.dependencies |> List.map (fun dependencyId -> dependencyId, task.id))

    let private createGraph (tasks: TaskRequest list) (nodes: Map<string, TaskNode>) : Graph =
        { nodes = nodes
          edges = getEdges tasks
          start_nodes = getStartNodes tasks
          end_nodes = getEndNodes nodes }

    let buildGraph (tasks: TaskRequest list) : AnalysisResult<Graph> =
        tasks
        |> validateTaskListNotEmpty
        |> bind validateTaskListSize
        |> bind (fun validTasks ->
            validateUniqueTaskIds validTasks
            |> bind (fun allTaskIds ->
                validTasks
                |> validateAll (validateTask allTaskIds)
                |> map (fun validatedTasks ->
                    validatedTasks
                    |> mapTasksToNodes
                    |> attachSuccessors validatedTasks
                    |> createGraph validatedTasks)))

    let private decrementInDegree
        (inDegree: Map<string, int>)
        (successorId: string)
        : int * Map<string, int> =
        let newDegree = inDegree.[successorId] - 1
        let updatedInDegree = Map.add successorId newDegree inDegree
        newDegree, updatedInDegree

    let private unprocessedNodes (graph: Graph) (processedNodeIds: string list) : string list =
        let processedSet = processedNodeIds |> Set.ofList

        graph.nodes
        |> Map.toList
        |> List.choose (fun (nodeId, _) ->
            match Set.contains nodeId processedSet with
            | true -> None
            | false -> Some nodeId)

    let topologicalSort (graph: Graph) : AnalysisResult<string list> =
        let inDegree =
            graph.nodes
            |> Map.map (fun _ node -> node.dependencies.Length)

        let initialQueue =
            graph.start_nodes
            |> List.sort
            |> ImmutableQueue.ofList

        let rec loop
            (queue: ImmutableQueue.T<string>)
            (currentInDegree: Map<string, int>)
            (processedNodeIds: string list)
            (processedCount: int)
            : AnalysisResult<string list> =
            match ImmutableQueue.tryDequeue queue with
            | None ->
                match processedCount = graph.nodes.Count with
                | true -> Ok (List.rev processedNodeIds)
                | false -> Error (CircularDependency (unprocessedNodes graph processedNodeIds))
            | Some (nodeId, queueAfterDequeue) ->
                let node = graph.nodes.[nodeId]

                let updatedInDegree, updatedQueue =
                    node.successors
                    |> List.fold
                        (fun (inDegreeState, queueState) successorId ->
                            let newDegree, nextInDegree = decrementInDegree inDegreeState successorId

                            let nextQueue =
                                match newDegree with
                                | 0 -> ImmutableQueue.enqueue successorId queueState
                                | _ -> queueState

                            nextInDegree, nextQueue)
                        (currentInDegree, queueAfterDequeue)

                loop updatedQueue updatedInDegree (nodeId :: processedNodeIds) (processedCount + 1)

        loop initialQueue inDegree [] 0

    let private getProjectDuration (graph: Graph) (nodes: Map<string, TaskNode>) : float =
        graph.end_nodes
        |> List.map (fun nodeId -> nodes.[nodeId].ef)
        |> List.max

    let forwardPass (graph: Graph) (sortedNodes: string list) : Map<string, TaskNode> =
        sortedNodes
        |> List.fold
            (fun nodes nodeId ->
                let node = nodes.[nodeId]

                let earliestStart =
                    match node.dependencies with
                    | [] -> 0.0
                    | dependencyIds ->
                        dependencyIds
                        |> List.map (fun dependencyId -> nodes.[dependencyId].ef)
                        |> List.max

                let earliestFinish = earliestStart + node.duration
                let updatedNode = { node with es = earliestStart; ef = earliestFinish }
                Map.add nodeId updatedNode nodes)
            graph.nodes

    let backwardPass (graph: Graph) (sortedNodes: string list) (nodes: Map<string, TaskNode>) : Map<string, TaskNode> =
        let projectDuration = getProjectDuration graph nodes

        sortedNodes
        |> List.rev
        |> List.fold
            (fun currentNodes nodeId ->
                let node = currentNodes.[nodeId]

                let latestFinish =
                    match node.successors with
                    | [] -> projectDuration
                    | successorIds ->
                        successorIds
                        |> List.map (fun successorId -> currentNodes.[successorId].ls)
                        |> List.min

                let latestStart = latestFinish - node.duration
                let totalFloat = latestStart - node.es

                let updatedNode =
                    { node with
                        ls = latestStart
                        lf = latestFinish
                        total_float = totalFloat }

                Map.add nodeId updatedNode currentNodes)
            nodes

    let getRiskLabel (pathFloat: float) (threshold: float) : string =
        match threshold with
        | 0.0 -> "high"
        | _ ->
            let ratio = pathFloat / threshold

            match ratio with
            | value when value > 0.75 -> "low"
            | value when value >= 0.50 -> "medium"
            | _ -> "high"

    let findCriticalPath
        (nodes: Map<string, TaskNode>)
        (startNodes: string list)
        (endNodes: string list)
        : string list =
        let endSet = Set.ofList endNodes

        let rec findCriticalPathRecursive (current: string) : string list option =
            let node = nodes.[current]

            match node.total_float > 0.0, Set.contains current endSet with
            | true, _ -> None
            | false, true -> Some [ current ]
            | false, false ->
                node.successors
                |> List.tryPick (fun successorId ->
                    match findCriticalPathRecursive successorId with
                    | Some path -> Some (current :: path)
                    | None -> None)

        startNodes
        |> List.tryPick findCriticalPathRecursive
        |> Option.defaultValue []

    let private shouldIncludePath (includeAllPaths: bool) (threshold: float) (pathFloat: float) : bool =
        match includeAllPaths with
        | true -> pathFloat > 0.0
        | false -> pathFloat > 0.0 && pathFloat <= threshold

    let private getPathDuration (nodes: Map<string, TaskNode>) (path: string list) : float =
        path
        |> List.sumBy (fun nodeId -> nodes.[nodeId].duration)

    let private createNearCriticalPath
        (threshold: float)
        (path: string list)
        (duration: float)
        (pathFloat: float)
        : NearCriticalPath =
        { path = path
          duration = duration
          float = pathFloat
          risk_label = getRiskLabel pathFloat threshold }

    let findNearCriticalPathsEfficient
        (graph: Graph)
        (nodes: Map<string, TaskNode>)
        (threshold: float)
        (includeAllPaths: bool)
        : NearCriticalPath list =

        match threshold <= 0.0, includeAllPaths with
        | true, false -> []
        | _ ->
            let pathFloatByNode =
                nodes
                |> Map.map (fun _ node -> node.total_float)

            let paths =
                graph.start_nodes
                |> List.collect (fun startNodeId ->
                    let rec buildPaths currentNodeId currentPath currentMinFloat =
                        let node = nodes.[currentNodeId]
                        let nodeFloat = pathFloatByNode.[currentNodeId]
                        let newMinFloat = min currentMinFloat nodeFloat
                        let newPath = currentNodeId :: currentPath

                        match node.successors with
                        | [] ->
                            let path = List.rev newPath

                            match shouldIncludePath includeAllPaths threshold newMinFloat with
                            | true ->
                                let pathDuration = getPathDuration nodes path
                                [ createNearCriticalPath threshold path pathDuration newMinFloat ]
                            | false ->
                                []
                        | successorIds ->
                            successorIds
                            |> List.collect (fun successorId -> buildPaths successorId newPath newMinFloat)

                    buildPaths startNodeId [] Double.MaxValue)

            paths
            |> List.distinctBy (fun path -> String.Join(",", path.path))
            |> List.sortBy (fun path -> path.float)

    let calculateThreshold
        (thresholdValue: ThresholdValue option)
        (projectDuration: float)
        : float =
        match thresholdValue with
        | None ->
            projectDuration * 0.20
        | Some (Number value) ->
            value
        | Some (Object config) ->
            let absolute = config.absolute |> Option.defaultValue 0.0

            let percentage =
                config.percentage
                |> Option.map (fun percentageValue -> projectDuration * (percentageValue / 100.0))
                |> Option.defaultValue 0.0

            match absolute > 0.0, percentage > 0.0 with
            | true, true -> max absolute percentage
            | true, false -> absolute
            | false, true -> percentage
            | false, false -> projectDuration * 0.20

    let private disconnectedSubgraphWarning (lang: LanguageCode) (affectedTasks: string list) : Warning =
        { type_ = "disconnected_subgraph"
          affected_tasks = affectedTasks
          message = Localization.getStringSimple lang MsgDisconnectedSubgraph }

    let detectDisconnectedSubgraphs (graph: Graph) (lang: LanguageCode) : Warning list =
        match graph.start_nodes, graph.end_nodes with
        | [], _
        | _, [] ->
            [ disconnectedSubgraphWarning lang [] ]
        | _ ->
            let rec collectReachable (nodeId: string) (visited: Set<string>) : Set<string> =
                match Set.contains nodeId visited with
                | true -> visited
                | false ->
                    let nextVisited = Set.add nodeId visited
                    let node = graph.nodes.[nodeId]

                    node.successors
                    |> List.fold (fun acc successorId -> collectReachable successorId acc) nextVisited

            let reachableFromStart =
                graph.start_nodes
                |> List.fold (fun visited startNodeId -> collectReachable startNodeId visited) Set.empty

            let allNodes = graph.nodes |> Map.toList |> List.map fst |> Set.ofList
            let disconnected = Set.difference allNodes reachableFromStart

            match Set.isEmpty disconnected with
            | true -> []
            | false -> [ disconnectedSubgraphWarning lang (disconnected |> Set.toList) ]

    let detectZeroDurationPaths (nodes: Map<string, TaskNode>) (lang: LanguageCode) : Warning list =
        let zeroDurationTasks =
            nodes
            |> Map.toList
            |> List.filter (fun (_, node) -> node.duration = 0.0)
            |> List.map fst

        match zeroDurationTasks with
        | [] -> []
        | _ ->
            [ { type_ = "zero_duration_path"
                affected_tasks = zeroDurationTasks
                message = Localization.getStringSimple lang MsgZeroDurationPath } ]

    let getValidationErrorCode (error: ValidationError) : string =
        match error with
        | CircularDependency _ -> "CIRCULAR_DEPENDENCY"
        | UnknownDependency _ -> "UNKNOWN_DEPENDENCY"
        | DuplicateTaskId _ -> "DUPLICATE_TASK_ID"
        | GraphTooLarge _ -> "GRAPH_TOO_LARGE"
        | NegativeDuration _ -> "NEGATIVE_DURATION"
        | EmptyTaskList -> "INVALID_INPUT"
        | InvalidDurationUnit _ -> "INVALID_INPUT"
        | InvalidInput _ -> "INVALID_INPUT"

    let getValidationStatusCode (error: ValidationError) : int =
        match error with
        | GraphTooLarge _ -> 413
        | NegativeDuration _ -> 422
        | _ -> 400

    let getValidationAffectedTasks (error: ValidationError) : string list =
        match error with
        | CircularDependency tasks -> tasks
        | UnknownDependency (taskId, _) -> [ taskId ]
        | DuplicateTaskId taskId -> [ taskId ]
        | NegativeDuration taskId -> [ taskId ]
        | _ -> []

    let getValidationErrorMessage (error: ValidationError) (lang: LanguageCode) : string =
        match error with
        | CircularDependency _ ->
            Localization.getStringSimple lang MsgCircularDependency
        | UnknownDependency (taskId, depId) ->
            Localization.getString lang MsgUnknownDependency [ taskId; depId ]
        | DuplicateTaskId taskId ->
            Localization.getString lang MsgDuplicateTaskId [ taskId ]
        | GraphTooLarge count ->
            Localization.getString lang MsgGraphTooLarge [ count; MAX_TASKS ]
        | NegativeDuration taskId ->
            Localization.getString lang MsgNegativeDuration [ taskId ]
        | EmptyTaskList ->
            Localization.getStringSimple lang MsgEmptyTaskList
        | InvalidDurationUnit durationUnit ->
            Localization.getString lang MsgInvalidDurationUnit [ durationUnit ]
        | InvalidInput message ->
            message

    let private resolveDurationUnit (request: SingleProjectRequest) : AnalysisResult<DurationUnit> =
        request.options
        |> Option.bind (fun options -> options.duration_unit)
        |> Option.defaultValue "days"
        |> validateDurationUnit

    let private resolveIncludeAllPaths (request: SingleProjectRequest) : bool =
        request.options
        |> Option.bind (fun options -> options.include_all_paths)
        |> Option.defaultValue false

    let private resolveThresholdValue (request: SingleProjectRequest) : ThresholdValue option =
        request.options
        |> Option.bind (fun options -> options.near_critical_threshold)

    let private resolveAnalysisConfig (request: SingleProjectRequest) : AnalysisResult<AnalysisConfig> =
        resolveDurationUnit request
        |> map (fun durationUnit ->
            { duration_unit = durationUnit
              include_all_paths = resolveIncludeAllPaths request
              threshold_value = resolveThresholdValue request })

    let private computeGraphComputation : TaskRequest list -> AnalysisResult<GraphComputation> =
        buildGraph
        >> bind (fun graph ->
            topologicalSort graph
            |> map (fun sortedNodes ->
                let nodesAfterForward = forwardPass graph sortedNodes
                let nodesAfterBackward = backwardPass graph sortedNodes nodesAfterForward

                { graph = graph
                  sorted_nodes = sortedNodes
                  nodes = nodesAfterBackward }))

    let private toTaskResult (threshold: float) (node: TaskNode) : TaskResult =
        let isCritical = node.total_float = 0.0
        let isNearCritical = not isCritical && node.total_float <= threshold

        { id = node.id
          label = node.label
          duration = node.duration
          earliest_start = node.es
          earliest_finish = node.ef
          latest_start = node.ls
          latest_finish = node.lf
          float = node.total_float
          is_critical = isCritical
          is_near_critical = isNearCritical
          earliest_start_date = None
          earliest_finish_date = None }

    let private collectWarnings (lang: LanguageCode) (graph: Graph) (nodes: Map<string, TaskNode>) : Warning list =
        [ detectDisconnectedSubgraphs graph lang
          detectZeroDurationPaths nodes lang ]
        |> List.concat

    let analyze (lang: LanguageCode) (request: SingleProjectRequest) : AnalysisResult<CpmResult> =
        request
        |> resolveAnalysisConfig
        |> bind (fun config ->
            let stopwatch = System.Diagnostics.Stopwatch.StartNew()

            request.tasks
            |> computeGraphComputation
            |> map (fun computation ->
                let projectDuration = getProjectDuration computation.graph computation.nodes
                let actualThreshold = calculateThreshold config.threshold_value projectDuration

                let criticalPath =
                    findCriticalPath computation.nodes computation.graph.start_nodes computation.graph.end_nodes

                let taskResults =
                    computation.sorted_nodes
                    |> List.map (fun nodeId -> computation.nodes.[nodeId])
                    |> List.map (toTaskResult actualThreshold)

                let nearCriticalPaths =
                    findNearCriticalPathsEfficient
                        computation.graph
                        computation.nodes
                        actualThreshold
                        config.include_all_paths

                let warnings = collectWarnings lang computation.graph computation.nodes

                stopwatch.Stop()

                { project_duration = projectDuration
                  duration_unit = DurationUnitCodec.toApiValue config.duration_unit
                  critical_path = criticalPath
                  tasks = taskResults
                  near_critical_paths = nearCriticalPaths
                  warnings = warnings
                  meta =
                    { task_count = computation.graph.nodes.Count
                      edge_count = computation.graph.edges.Length
                      computation_ms = stopwatch.ElapsedMilliseconds } }))

    let private toSingleProjectRequest (request: BatchProjectRequest) : SingleProjectRequest =
        { tasks = request.tasks
          calendars = request.calendars
          options = request.options }

    let private toBatchSuccess (request: BatchProjectRequest) (result: CpmResult) : BatchResultItem =
        { id = request.id
          status = "ok"
          project_duration = Some result.project_duration
          critical_path = Some result.critical_path
          tasks = Some result.tasks
          near_critical_paths = Some result.near_critical_paths
          warnings = Some result.warnings
          meta = Some result.meta
          error = None }

    let private toBatchValidationError
        (lang: LanguageCode)
        (request: BatchProjectRequest)
        (validationError: ValidationError)
        : BatchResultItem =
        { id = request.id
          status = "error"
          project_duration = None
          critical_path = None
          tasks = None
          near_critical_paths = None
          warnings = None
          meta = None
          error =
            Some
                { code = getValidationErrorCode validationError
                  message = getValidationErrorMessage validationError lang
                  affected_tasks = getValidationAffectedTasks validationError } }

    let private toBatchInternalError (lang: LanguageCode) (request: BatchProjectRequest) : BatchResultItem =
        { id = request.id
          status = "error"
          project_duration = None
          critical_path = None
          tasks = None
          near_critical_paths = None
          warnings = None
          meta = None
          error =
            Some
                { code = "INTERNAL_ERROR"
                  message = Localization.getStringSimple lang MsgInternalError
                  affected_tasks = [] } }

    let analyzeSingleForBatch (lang: LanguageCode) (request: BatchProjectRequest) : BatchResultItem =
        try
            request
            |> toSingleProjectRequest
            |> analyze lang
            |> map (toBatchSuccess request)
            |> Result.defaultWith (toBatchValidationError lang request)
        with
        | _ ->
            toBatchInternalError lang request

    let private validateBatchProjectCount
        (lang: LanguageCode)
        (request: BatchRequest)
        : AnalysisResult<BatchRequest> =
        match request.projects.Length with
        | projectCount when projectCount > MAX_BATCH_PROJECTS ->
            Error (InvalidInput (Localization.getString lang MsgTooManyProjects [ MAX_BATCH_PROJECTS ]))
        | _ ->
            Ok request

    let private validateBatchTaskCount
        (lang: LanguageCode)
        (request: BatchRequest)
        : AnalysisResult<BatchRequest> =
        let totalTasks = request.projects |> List.sumBy (fun project -> project.tasks.Length)

        match totalTasks with
        | taskCount when taskCount > MAX_BATCH_TASKS ->
            Error (InvalidInput (Localization.getString lang MsgTooManyTotalTasks [ MAX_BATCH_TASKS ]))
        | _ ->
            Ok request

    let analyzeBatch (lang: LanguageCode) (request: BatchRequest) : AnalysisResult<BatchResult> =
        request
        |> validateBatchProjectCount lang
        |> bind (validateBatchTaskCount lang)
        |> map (fun validatedRequest ->
            let stopwatch = System.Diagnostics.Stopwatch.StartNew()

            let results =
                validatedRequest.projects
                |> List.map (analyzeSingleForBatch lang)

            let succeeded =
                results
                |> List.filter (fun resultItem -> resultItem.status = "ok")
                |> List.length

            let failed = results.Length - succeeded

            stopwatch.Stop()

            { results = results
              meta =
                { total_projects = validatedRequest.projects.Length
                  succeeded = succeeded
                  failed = failed
                  total_computation_ms = stopwatch.ElapsedMilliseconds } })