namespace CriticalPathExtractor

open System
open System.Collections.Generic
open CriticalPathExtractor.Types
open CriticalPathExtractor.Infrastructure

module CpmEngine =
    
    let MAX_TASKS = 10000
    let MAX_DEPENDENCIES_PER_TASK = 500
    let MAX_BATCH_PROJECTS = 100
    let MAX_BATCH_TASKS = 50000

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

    let private validateDurationUnit (unit: string) : unit =
        match unit.ToLowerInvariant() with
        | "hours" | "days" | "weeks" -> ()
        | _ -> raise (CpmValidationException (InvalidDurationUnit unit))

    let private validateTask (task: TaskRequest) (allTaskIds: Set<string>) =
        if task.duration < 0.0 then
            raise (CpmValidationException (NegativeDuration task.id))
        
        task.dependencies
        |> List.iter (fun depId ->
            if not (Set.contains depId allTaskIds) then
                raise (CpmValidationException (UnknownDependency (task.id, depId)))
        )
        
        if task.dependencies.Length > MAX_DEPENDENCIES_PER_TASK then
            raise (CpmValidationException (InvalidInput (sprintf "Task %s has too many dependencies (max: %d)" task.id MAX_DEPENDENCIES_PER_TASK)))

    let buildGraph (tasks: TaskRequest list) : Graph =
        if tasks.IsEmpty then
            raise (CpmValidationException EmptyTaskList)
        
        if tasks.Length > MAX_TASKS then
            raise (CpmValidationException (GraphTooLarge tasks.Length))
        
        let taskIds = tasks |> List.map (fun t -> t.id)
        let uniqueIds = Set.ofList taskIds
        if uniqueIds.Count <> tasks.Length then
            let duplicates = 
                taskIds 
                |> Seq.groupBy id 
                |> Seq.filter (fun (_, items) -> Seq.length items > 1)
                |> Seq.map fst
                |> Seq.head
            raise (CpmValidationException (DuplicateTaskId duplicates))
        
        tasks |> List.iter (fun t -> validateTask t uniqueIds)
        
        let nodes = 
            tasks 
            |> List.map (fun t -> t.id, mapTaskToNode t)
            |> Map.ofList
        
        let nodesWithSuccessors = 
            tasks
            |> List.fold (fun acc task ->
                task.dependencies
                |> List.fold (fun innerAcc depId ->
                    match Map.tryFind depId innerAcc with
                    | Some node -> 
                        let updatedNode = { node with successors = task.id :: node.successors }
                        Map.add depId updatedNode innerAcc
                    | None -> innerAcc
                ) acc
            ) nodes
        
        let startNodes = 
            tasks 
            |> List.filter (fun t -> t.dependencies.IsEmpty)
            |> List.map (fun t -> t.id)
        
        let endNodes =
            nodesWithSuccessors
            |> Map.toList
            |> List.filter (fun (_, node) -> node.successors.IsEmpty)
            |> List.map fst
        
        let edges = 
            tasks
            |> List.collect (fun task -> 
                task.dependencies 
                |> List.map (fun dep -> (dep, task.id))
            )
        
        { nodes = nodesWithSuccessors
          edges = edges
          start_nodes = startNodes
          end_nodes = endNodes }

    let topologicalSort (graph: Graph) : string list =
        let inDegree = 
            graph.nodes
            |> Map.map (fun _ node -> node.dependencies.Length)
        
        let queue = Queue<string>()
        graph.start_nodes 
        |> List.sort
        |> List.iter (fun n -> queue.Enqueue(n))
        
        let result = ResizeArray<string>()
        
        let mutable currentInDegree = inDegree
        
        while queue.Count > 0 do
            let nodeId = queue.Dequeue()
            result.Add(nodeId)
            
            let node = graph.nodes.[nodeId]
            node.successors
            |> List.iter (fun succId ->
                let newDegree = currentInDegree.[succId] - 1
                currentInDegree <- Map.add succId newDegree currentInDegree
                
                if newDegree = 0 then
                    queue.Enqueue(succId)
            )
        
        if result.Count <> graph.nodes.Count then
            let processed = Set.ofSeq result
            let unprocessed = 
                graph.nodes
                |> Map.toList
                |> List.filter (fun (id, _) -> not (Set.contains id processed))
                |> List.map fst
            raise (CpmValidationException (CircularDependency unprocessed))
        
        result |> Seq.toList

    let forwardPass (graph: Graph) (sortedNodes: string list) : Map<string, TaskNode> =
        let mutable nodes = graph.nodes
        
        sortedNodes
        |> List.iter (fun nodeId ->
            let node = nodes.[nodeId]
            
            let es =
                if node.dependencies.IsEmpty then
                    0.0
                else
                    node.dependencies
                    |> List.map (fun depId -> nodes.[depId].ef)
                    |> List.max
            
            let ef = es + node.duration
            
            let updatedNode = { node with es = es; ef = ef }
            nodes <- Map.add nodeId updatedNode nodes
        )
        
        nodes

    let backwardPass (graph: Graph) (sortedNodes: string list) (nodes: Map<string, TaskNode>) : Map<string, TaskNode> =
        let mutable updatedNodes = nodes
        let projectDuration = 
            graph.end_nodes
            |> List.map (fun id -> updatedNodes.[id].ef)
            |> List.max
        
        sortedNodes
        |> List.rev
        |> List.iter (fun nodeId ->
            let node = updatedNodes.[nodeId]
            
            let lf =
                if node.successors.IsEmpty then
                    projectDuration
                else
                    node.successors
                    |> List.map (fun succId -> updatedNodes.[succId].ls)
                    |> List.min
            
            let ls = lf - node.duration
            let totalFloat = ls - node.es
            
            let updatedNode = { node with ls = ls; lf = lf; total_float = totalFloat }
            updatedNodes <- Map.add nodeId updatedNode updatedNodes
        )
        
        updatedNodes

    let getRiskLabel (pathFloat: float) (threshold: float) : string =
        if threshold = 0.0 then
            "high"
        else
            let ratio = pathFloat / threshold
            if ratio > 0.75 then
                "low"
            elif ratio >= 0.50 then
                "medium"
            else
                "high"

    let findCriticalPath (nodes: Map<string, TaskNode>) (startNodes: string list) (endNodes: string list) : string list =
        let endSet = Set.ofList endNodes
        
        let rec findCriticalPathRecursive (current: string) : string list option =
            let node = nodes.[current]
            if node.total_float > 0.0 then
                None
            elif Set.contains current endSet then
                Some [current]
            else
                node.successors
                |> List.tryPick (fun succ ->
                    match findCriticalPathRecursive succ with
                    | Some path -> Some (current :: path)
                    | None -> None
                )
        
        startNodes
        |> List.tryPick findCriticalPathRecursive
        |> Option.defaultValue []

    let findNearCriticalPathsEfficient
        (graph: Graph) 
        (nodes: Map<string, TaskNode>) 
        (threshold: float)
        (includeAllPaths: bool)
        : NearCriticalPath list =
        
        if threshold <= 0.0 && not includeAllPaths then
            []
        else
            let pathFloatByNode = 
                nodes
                |> Map.map (fun _ node -> node.total_float)
            
            let paths = 
                graph.start_nodes
                |> List.collect (fun startId ->
                    let rec buildPaths currentId currentPath currentMinFloat =
                        let node = nodes.[currentId]
                        let nodeFloat = pathFloatByNode.[currentId]
                        let newMinFloat = min currentMinFloat nodeFloat
                        let newPath = currentId :: currentPath
                        
                        if node.successors.IsEmpty then
                            let pathList = List.rev newPath
                            let pathDuration = 
                                pathList 
                                |> List.sumBy (fun id -> nodes.[id].duration)
                            
                            if includeAllPaths then
                                if newMinFloat > 0.0 then
                                    [{
                                        path = pathList
                                        duration = pathDuration
                                        float = newMinFloat
                                        risk_label = getRiskLabel newMinFloat threshold
                                    }]
                                else
                                    []
                            else
                                if newMinFloat > 0.0 && newMinFloat <= threshold then
                                    [{
                                        path = pathList
                                        duration = pathDuration
                                        float = newMinFloat
                                        risk_label = getRiskLabel newMinFloat threshold
                                    }]
                                else
                                    []
                        else
                            node.successors
                            |> List.collect (fun succId ->
                                buildPaths succId newPath newMinFloat
                            )
                    
                    buildPaths startId [] System.Double.MaxValue
                )
            
            paths
            |> List.distinctBy (fun p -> String.Join(",", p.path))
            |> List.sortBy (fun p -> p.float)

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
                |> Option.map (fun p -> projectDuration * (p / 100.0))
                |> Option.defaultValue 0.0
            
            if absolute > 0.0 && percentage > 0.0 then
                max absolute percentage
            elif absolute > 0.0 then
                absolute
            elif percentage > 0.0 then
                percentage
            else
                projectDuration * 0.20

    let detectDisconnectedSubgraphs (graph: Graph) (lang: LanguageCode) : Warning list =
        if graph.start_nodes.IsEmpty then
            [{
                type_ = "disconnected_subgraph"
                affected_tasks = []
                message = Localization.getStringSimple lang MsgDisconnectedSubgraph
            }]
        elif graph.end_nodes.IsEmpty then
            [{
                type_ = "disconnected_subgraph"
                affected_tasks = []
                message = Localization.getStringSimple lang MsgDisconnectedSubgraph
            }]
        else
            let reachableFromStart = 
                let rec collectReachable nodeId visited =
                    if Set.contains nodeId visited then
                        visited
                    else
                        let node = graph.nodes.[nodeId]
                        node.successors
                        |> List.fold (fun acc succId ->
                            collectReachable succId (Set.add nodeId acc)
                        ) (Set.add nodeId visited)
                
                graph.start_nodes
                |> List.fold (fun acc startId -> collectReachable startId acc
                ) Set.empty
            
            let allNodes = graph.nodes |> Map.toList |> List.map fst |> Set.ofList
            let disconnected = Set.difference allNodes reachableFromStart
            
            if disconnected.IsEmpty then
                []
            else
                [{
                    type_ = "disconnected_subgraph"
                    affected_tasks = disconnected |> Set.toList
                    message = Localization.getStringSimple lang MsgDisconnectedSubgraph
                }]

    let detectZeroDurationPaths (nodes: Map<string, TaskNode>) (lang: LanguageCode) : Warning list =
        let zeroDurationTasks = 
            nodes
            |> Map.toList
            |> List.filter (fun (_, node) -> node.duration = 0.0)
            |> List.map fst
        
        if zeroDurationTasks.IsEmpty then
            []
        else
            [{
                type_ = "zero_duration_path"
                affected_tasks = zeroDurationTasks
                message = Localization.getStringSimple lang MsgZeroDurationPath
            }]

    let getValidationErrorMessage (error: Types.ValidationError) (lang: LanguageCode) : string =
        match error with
        | Types.CircularDependency _ ->
            Localization.getStringSimple lang MsgCircularDependency
        | Types.UnknownDependency (taskId, depId) ->
            Localization.getString lang MsgUnknownDependency [taskId; depId]
        | Types.DuplicateTaskId taskId ->
            Localization.getString lang MsgDuplicateTaskId [taskId]
        | Types.GraphTooLarge count ->
            Localization.getString lang MsgGraphTooLarge [count; MAX_TASKS]
        | Types.NegativeDuration taskId ->
            Localization.getString lang MsgNegativeDuration [taskId]
        | Types.EmptyTaskList ->
            Localization.getStringSimple lang MsgEmptyTaskList
        | Types.InvalidDurationUnit unit ->
            Localization.getString lang MsgInvalidDurationUnit [unit]
        | Types.InvalidInput msg ->
            msg  // Keep custom messages as-is

    let analyze (request: SingleProjectRequest) (lang: LanguageCode) : CpmResult =
        let stopwatch = System.Diagnostics.Stopwatch()
        stopwatch.Start()
        
        let durationUnit = 
            request.options 
            |> Option.bind (fun o -> o.duration_unit)
            |> Option.defaultValue "days"
        
        validateDurationUnit durationUnit
        
        let includeAllPaths =
            request.options
            |> Option.bind (fun o -> o.include_all_paths)
            |> Option.defaultValue false
        
        let thresholdValue =
            request.options
            |> Option.bind (fun o -> o.near_critical_threshold)
        
        let graph = buildGraph request.tasks
        let sortedNodes = topologicalSort graph
        let nodesAfterForward = forwardPass graph sortedNodes
        let nodesAfterBackward = backwardPass graph sortedNodes nodesAfterForward
        
        let projectDuration = 
            graph.end_nodes
            |> List.map (fun id -> nodesAfterBackward.[id].ef)
            |> List.max
        
        let actualThreshold = calculateThreshold thresholdValue projectDuration
        
        let criticalPath = findCriticalPath nodesAfterBackward graph.start_nodes graph.end_nodes
        
        let taskResults =
            sortedNodes
            |> List.map (fun id ->
                let node = nodesAfterBackward.[id]
                let isCritical = node.total_float = 0.0
                let isNearCritical = not isCritical && node.total_float <= actualThreshold
                {
                    id = node.id
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
                    earliest_finish_date = None
                }
            )
        
        let nearCriticalPaths = 
            findNearCriticalPathsEfficient graph nodesAfterBackward actualThreshold includeAllPaths
        
        let warnings = 
            List.concat [
                detectDisconnectedSubgraphs graph lang
                detectZeroDurationPaths nodesAfterBackward lang
            ]
        
        stopwatch.Stop()
        
        {
            project_duration = projectDuration
            duration_unit = durationUnit
            critical_path = criticalPath
            tasks = taskResults
            near_critical_paths = nearCriticalPaths
            warnings = warnings
            meta = {
                task_count = graph.nodes.Count
                edge_count = graph.edges.Length
                computation_ms = stopwatch.ElapsedMilliseconds
            }
        }

    let analyzeSingleForBatch (request: BatchProjectRequest) (lang: LanguageCode) : BatchResultItem =
        try
            let analyzeRequest = {
                tasks = request.tasks
                calendars = request.calendars
                options = request.options
            }
            let result = analyze analyzeRequest lang
            
            {
                id = request.id
                status = "ok"
                project_duration = Some result.project_duration
                critical_path = Some result.critical_path
                tasks = Some result.tasks
                near_critical_paths = Some result.near_critical_paths
                warnings = Some result.warnings
                meta = Some result.meta
                error = None
            }
        with
        | CpmValidationException validationError ->
            let errorCode, affectedTasks =
                match validationError with
                | Types.CircularDependency tasks -> "CIRCULAR_DEPENDENCY", tasks
                | Types.UnknownDependency (taskId, _) -> "UNKNOWN_DEPENDENCY", [taskId]
                | Types.DuplicateTaskId taskId -> "DUPLICATE_TASK_ID", [taskId]
                | Types.GraphTooLarge _ -> "GRAPH_TOO_LARGE", []
                | Types.NegativeDuration taskId -> "NEGATIVE_DURATION", [taskId]
                | Types.EmptyTaskList -> "INVALID_INPUT", []
                | Types.InvalidDurationUnit _ -> "INVALID_INPUT", []
                | Types.InvalidInput _ -> "INVALID_INPUT", []
            
            {
                id = request.id
                status = "error"
                project_duration = None
                critical_path = None
                tasks = None
                near_critical_paths = None
                warnings = None
                meta = None
                error = Some {
                    code = errorCode
                    message = getValidationErrorMessage validationError lang
                    affected_tasks = affectedTasks
                }
            }
        | ex ->
            {
                id = request.id
                status = "error"
                project_duration = None
                critical_path = None
                tasks = None
                near_critical_paths = None
                warnings = None
                meta = None
                error = Some {
                    code = "INTERNAL_ERROR"
                    message = Localization.getStringSimple lang MsgInternalError
                    affected_tasks = []
                }
            }

    let analyzeBatch (request: BatchRequest) (lang: LanguageCode) : BatchResult =
        let stopwatch = System.Diagnostics.Stopwatch()
        stopwatch.Start()
        
        if request.projects.Length > MAX_BATCH_PROJECTS then
            raise (CpmValidationException (Types.InvalidInput (Localization.getString lang MsgTooManyProjects [MAX_BATCH_PROJECTS])))

        let totalTasks = request.projects |> List.sumBy (fun p -> p.tasks.Length)
        if totalTasks > MAX_BATCH_TASKS then
            raise (CpmValidationException (Types.InvalidInput (Localization.getString lang MsgTooManyTotalTasks [MAX_BATCH_TASKS])))
        
        let results = 
            request.projects
            |> List.map (fun p -> analyzeSingleForBatch p lang)
        
        let succeeded = results |> List.filter (fun r -> r.status = "ok") |> List.length
        let failed = results.Length - succeeded
        
        stopwatch.Stop()
        
        {
            results = results
            meta = {
                total_projects = request.projects.Length
                succeeded = succeeded
                failed = failed
                total_computation_ms = stopwatch.ElapsedMilliseconds
            }
        }
