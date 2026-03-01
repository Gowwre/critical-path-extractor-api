# Critical Path Extractor API — Design Document

---

## 1. Overview

The Critical Path Extractor is a stateless REST API that accepts a project task graph and returns a full Critical Path Method (CPM) analysis. It identifies the critical path, float values for every task, near-critical paths, and scheduling warnings — with no storage, no authentication state, and no external dependencies.

---

## 2. Problem Statement

Most teams manage interdependent tasks without knowing which ones actually control their deadline. This leads to:

- Misallocated urgency — panicking about tasks that have plenty of float
- Blind spots — ignoring tasks on the critical path until it's too late
- Inaccurate estimates — no understanding of how individual delays propagate to the project end date

CPM is the established solution, but it's underserved as a standalone, embeddable API. Existing tools either bundle it into full project management platforms or require developers to implement graph algorithms themselves.

---

## 3. Goals

- Accept a task graph as JSON and return a complete CPM analysis in a single request
- Be fast enough for real-time UI use (target: <10ms for graphs up to 10,000 tasks)
- Return actionable output beyond just the critical path — float values, near-critical paths, and warnings
- Validate input and surface graph problems (cycles, orphans, disconnected subgraphs) explicitly
- Remain purely algorithmic — no LLMs, no external data, no persistent state

---

## 4. Non-Goals

- User authentication or API key management (handled at the infrastructure layer)
- Storing or retrieving past analyses
- Rendering Gantt charts or other visualisations (caller's responsibility)
- Real-time collaboration or project tracking

---

## 5. Algorithm

The API implements the classical Critical Path Method, which runs two passes over a Directed Acyclic Graph (DAG).

### 5.1 Forward Pass (Early Times)

Traverse the graph from start to finish. For each task:

```
Early Start (ES)  = max(Early Finish of all predecessors)
Early Finish (EF) = ES + duration
```

### 5.2 Backward Pass (Late Times)

Traverse the graph from finish to start. For each task:

```
Late Finish (LF) = min(Late Start of all successors)
Late Start (LS)  = LF - duration
```

### 5.3 Float Calculation

```
Total Float = LS - ES  (equivalently, LF - EF)
```

Tasks with zero float are on the critical path. Tasks with float greater than zero can be delayed by that amount without affecting the project end date.

### 5.4 Near-Critical Path Detection

After identifying the critical path, the API traverses all other paths and ranks them by total float. Paths are flagged as near-critical if their float falls below a configurable threshold (default: 20% of total project duration).

### 5.5 Complexity

Both passes are O(V + E) where V is the number of tasks and E is the number of dependency edges — linear in the size of the graph.

---

## 6. API Specification

### 6.1 Endpoint

```
POST /v1/critical-path
Content-Type: application/json
```

### 6.2 Request Schema

```json
{
  "tasks": [
    {
      "id": "string (required, unique)",
      "label": "string (optional, human-readable name)",
      "duration": "number (required, >= 0)",
      "dependencies": ["string (task id)", "..."]
    }
  ],
  "options": {
    "duration_unit": "hours | days | weeks (default: days)",
    "near_critical_threshold": "number (float threshold for near-critical flag, default: 20% of project duration)",
    "include_all_paths": "boolean (return every path ranked by float, default: false)"
  }
}
```

#### Example Request

```json
{
  "tasks": [
    { "id": "A", "label": "Requirements",  "duration": 3, "dependencies": [] },
    { "id": "B", "label": "Backend",       "duration": 2, "dependencies": ["A"] },
    { "id": "C", "label": "Frontend",      "duration": 5, "dependencies": ["A"] },
    { "id": "D", "label": "Integration",   "duration": 1, "dependencies": ["B", "C"] },
    { "id": "E", "label": "QA",            "duration": 2, "dependencies": ["D"] }
  ],
  "options": {
    "duration_unit": "days",
    "near_critical_threshold": 2
  }
}
```

### 6.3 Response Schema

```json
{
  "project_duration": "number",
  "duration_unit": "string",
  "critical_path": ["task_id", "..."],
  "tasks": [
    {
      "id": "string",
      "label": "string",
      "duration": "number",
      "earliest_start": "number",
      "earliest_finish": "number",
      "latest_start": "number",
      "latest_finish": "number",
      "float": "number",
      "is_critical": "boolean",
      "is_near_critical": "boolean"
    }
  ],
  "near_critical_paths": [
    {
      "path": ["task_id", "..."],
      "duration": "number",
      "float": "number",
      "risk_label": "low | medium | high"
    }
  ],
  "warnings": [
    {
      "type": "circular_dependency | orphaned_task | disconnected_subgraph | zero_duration_path",
      "affected_tasks": ["task_id", "..."],
      "message": "string"
    }
  ],
  "meta": {
    "task_count": "number",
    "edge_count": "number",
    "computation_ms": "number"
  }
}
```

#### Example Response

```json
{
  "project_duration": 11,
  "duration_unit": "days",
  "critical_path": ["A", "C", "D", "E"],
  "tasks": [
    { "id": "A", "label": "Requirements", "duration": 3, "earliest_start": 0, "earliest_finish": 3, "latest_start": 0,  "latest_finish": 3,  "float": 0, "is_critical": true,  "is_near_critical": false },
    { "id": "B", "label": "Backend",      "duration": 2, "earliest_start": 3, "earliest_finish": 5, "latest_start": 5,  "latest_finish": 7,  "float": 2, "is_critical": false, "is_near_critical": true  },
    { "id": "C", "label": "Frontend",     "duration": 5, "earliest_start": 3, "earliest_finish": 8, "latest_start": 3,  "latest_finish": 8,  "float": 0, "is_critical": true,  "is_near_critical": false },
    { "id": "D", "label": "Integration",  "duration": 1, "earliest_start": 8, "earliest_finish": 9, "latest_start": 8,  "latest_finish": 9,  "float": 0, "is_critical": true,  "is_near_critical": false },
    { "id": "E", "label": "QA",           "duration": 2, "earliest_start": 9, "earliest_finish": 11,"latest_start": 9,  "latest_finish": 11, "float": 0, "is_critical": true,  "is_near_critical": false }
  ],
  "near_critical_paths": [
    {
      "path": ["A", "B", "D", "E"],
      "duration": 8,
      "float": 2,
      "risk_label": "medium"
    }
  ],
  "warnings": [],
  "meta": {
    "task_count": 5,
    "edge_count": 5,
    "computation_ms": 1
  }
}
```

### 6.4 Error Responses

| HTTP Status | Code | Description |
|---|---|---|
| 400 | `INVALID_INPUT` | Malformed JSON or missing required fields |
| 400 | `CIRCULAR_DEPENDENCY` | Graph contains one or more cycles — CPM is undefined |
| 400 | `UNKNOWN_DEPENDENCY` | A task references a dependency ID that doesn't exist |
| 400 | `DUPLICATE_TASK_ID` | Two or more tasks share the same ID |
| 413 | `GRAPH_TOO_LARGE` | Task count exceeds the maximum allowed (10,000) |
| 422 | `NEGATIVE_DURATION` | One or more tasks have a duration less than zero |

Circular dependency errors return the affected task IDs to help callers debug their graph:

```json
{
  "error": {
    "code": "CIRCULAR_DEPENDENCY",
    "message": "The task graph contains a cycle and cannot be analysed.",
    "affected_tasks": ["C", "D", "E"]
  }
}
```

---

## 7. Extensions (v2 Candidates)

These stay purely algorithmic and require no LLMs or persistent storage.

### 7.1 What-If Analysis

```
POST /v1/critical-path/what-if
```

Accept a base graph plus a set of hypothetical changes (task delays, duration adjustments, added/removed dependencies) and return the updated critical path — useful for live Gantt editors.

### 7.2 PERT Mode

Accept three duration estimates per task — optimistic, most likely, and pessimistic — and return a probability distribution over total project duration using the Program Evaluation and Review Technique (PERT).

```json
{ "id": "C", "duration_optimistic": 3, "duration_likely": 5, "duration_pessimistic": 10 }
```

### 7.3 Crashing Analysis

```
POST /v1/critical-path/crash
```

Accept a cost-per-unit to accelerate each task and a target reduction in project duration. Return the minimum-cost set of tasks to accelerate and by how much — a classic linear optimisation problem.

### 7.4 Resource Leveling

Accept resource assignments per task and resource capacity constraints. Return an adjusted schedule that eliminates overallocation while minimising the impact on total project duration.

---

## 8. Implementation Notes

### 8.1 Recommended Stack

- **Language:** Any — the algorithm has no exotic dependencies. Go or Rust are natural fits for low-latency stateless APIs. Node.js or Python work fine for early versions.
- **Graph library:** Implement the forward/backward pass directly — the algorithm is simple enough that pulling in a full graph library is unnecessary overhead.
- **Cycle detection:** Run a topological sort (Kahn's algorithm or DFS-based) before the CPM passes. If a topological ordering cannot be produced, return `CIRCULAR_DEPENDENCY`.

### 8.2 Input Size Limits

| Parameter | Limit |
|---|---|
| Max tasks | 10,000 |
| Max dependencies per task | 500 |
| Max request body size | 5MB |

### 8.3 Performance Target

| Graph size | Target response time |
|---|---|
| < 100 tasks | < 2ms |
| < 1,000 tasks | < 10ms |
| < 10,000 tasks | < 100ms |

---

## 9. Potential Customers

- **Project management tools** (Jira, Linear, Asana, Monday) — embed CPM analysis into existing task views without building it in-house
- **Construction & engineering software** — CPM is an industry standard; many domain-specific tools lack a clean implementation
- **ERP and supply chain platforms** — manufacturing schedules are often complex dependency graphs
- **Game studios** — multi-department production pipelines benefit heavily from float visibility
- **Developer tools** — build system analysers, CI pipeline optimisers, and release managers all deal with task DAGs

---

## 10. Decisions Log

Previously open questions, now resolved:

| Question | Decision |
|---|---|
| Project calendars | Supported — callers can define custom calendars with working days and holidays |
| Near-critical threshold | Both percentage and absolute value accepted simultaneously |
| Batch endpoint | Yes — included as a first-class endpoint |
| Pricing model | Free, no pricing model |

---

## 11. Project Calendars

Durations are interpreted against a **project calendar** supplied in the request. If no calendar is provided the API defaults to a simple 7-day week with no holidays (i.e. duration units are treated as abstract continuous units).

### 11.1 Calendar Schema

```json
{
  "calendar": {
    "working_days": ["monday", "tuesday", "wednesday", "thursday", "friday"],
    "daily_hours": 8,
    "holidays": ["2025-12-25", "2025-01-01"],
    "timezone": "Europe/London"
  }
}
```

All fields are optional. Omitting `working_days` defaults to all seven days. Omitting `holidays` defaults to none. Omitting `timezone` defaults to UTC.

### 11.2 Effect on Scheduling

When a calendar is supplied, the API converts raw durations into **working-time durations** before running the CPM passes. The output `earliest_start`, `earliest_finish`, `latest_start`, and `latest_finish` values are returned as both raw duration offsets and as **absolute calendar dates** if a `project_start` date is provided:

```json
{
  "project_start": "2025-06-02",
  "calendar": {
    "working_days": ["monday", "tuesday", "wednesday", "thursday", "friday"],
    "holidays": ["2025-12-25"]
  }
}
```

When `project_start` is supplied, each task in the response gains two additional fields:

```json
{
  "id": "C",
  "earliest_start_date": "2025-06-05",
  "earliest_finish_date": "2025-06-12"
}
```

### 11.3 Multiple Calendars

Different tasks can reference different calendars — useful when teams in different regions or roles work different schedules. Calendars are defined in a top-level `calendars` map and referenced by ID on each task:

```json
{
  "calendars": {
    "engineering": {
      "working_days": ["monday", "tuesday", "wednesday", "thursday", "friday"],
      "daily_hours": 8
    },
    "contractors": {
      "working_days": ["monday", "tuesday", "wednesday", "thursday", "friday", "saturday"],
      "daily_hours": 10
    }
  },
  "tasks": [
    { "id": "A", "duration": 3, "calendar_id": "engineering", "dependencies": [] },
    { "id": "B", "duration": 2, "calendar_id": "contractors", "dependencies": ["A"] }
  ]
}
```

If a task omits `calendar_id`, it falls back to a `default` calendar if one is defined, then to the 7-day continuous default.

---

## 12. Near-Critical Threshold

The threshold that determines whether a path is flagged as near-critical accepts **both a percentage and an absolute value simultaneously**. A path is considered near-critical if it breaches either condition — whichever is more permissive.

```json
{
  "options": {
    "near_critical_threshold": {
      "absolute": 2,
      "percentage": 15
    }
  }
}
```

In this example, a path is flagged as near-critical if its float is ≤ 2 days **or** ≤ 15% of the total project duration — whichever float value is larger. This prevents short projects from having an overly tight threshold while ensuring long projects don't have an absurdly large one.

If only one value is supplied, the other is ignored. If neither is supplied, the default is 20% of project duration with no absolute fallback.

The `risk_label` on each near-critical path is derived from how close the float is to zero relative to the threshold:

| Float as % of threshold | Risk label |
|---|---|
| > 75% | `low` |
| 50–75% | `medium` |
| < 50% | `high` |

---

## 13. Batch Endpoint

```
POST /v1/critical-path/batch
Content-Type: application/json
```

Accepts an array of independent project graphs and returns an array of CPM results in the same order. Each item in the batch is processed identically to a single `/v1/critical-path` request and can carry its own options and calendars.

### 13.1 Request Schema

```json
{
  "projects": [
    {
      "id": "project-alpha",
      "tasks": [...],
      "calendars": {...},
      "options": {...}
    },
    {
      "id": "project-beta",
      "tasks": [...],
      "options": {...}
    }
  ]
}
```

### 13.2 Response Schema

```json
{
  "results": [
    {
      "id": "project-alpha",
      "status": "ok",
      "project_duration": 11,
      "critical_path": ["A", "C", "D", "E"],
      "tasks": [...],
      "near_critical_paths": [...],
      "warnings": [],
      "meta": { "task_count": 5, "edge_count": 5, "computation_ms": 1 }
    },
    {
      "id": "project-beta",
      "status": "error",
      "error": {
        "code": "CIRCULAR_DEPENDENCY",
        "message": "The task graph contains a cycle and cannot be analysed.",
        "affected_tasks": ["C", "D"]
      }
    }
  ],
  "meta": {
    "total_projects": 2,
    "succeeded": 1,
    "failed": 1,
    "total_computation_ms": 3
  }
}
```

Errors in one project do not fail the entire batch. Each result carries its own `status` field (`ok` or `error`) so callers can handle partial failures gracefully.

### 13.3 Batch Limits

| Parameter | Limit |
|---|---|
| Max projects per batch | 100 |
| Max total tasks across all projects | 50,000 |
| Max request body size | 20MB |

---

*Document version 0.2 — open questions resolved*