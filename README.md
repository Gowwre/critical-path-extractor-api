# Critical Path Extractor API

A high-performance, stateless REST API for Critical Path Method (CPM) analysis. Built with F# and ASP.NET Core.

## Features

- **CPM Analysis**: Calculate critical paths, float values, and task schedules
- **Near-Critical Paths**: Detect paths with float within configurable thresholds
- **Graph Validation**: Detect cycles, unknown dependencies, and disconnected subgraphs
- **Batch Processing**: Analyze multiple independent projects in a single request
- **Internationalization**: Full support for English and Vietnamese
- **API Documentation**: Interactive Scalar UI with OpenAPI specification
- **Performance**: O(V+E) complexity, handles up to 10,000 tasks per project

## Quick Start

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

### Running the API

```bash
# Clone the repository
git clone <repository-url>
cd api-fsharp

# Run the application
dotnet run

# API will be available at http://localhost:5000
```

### Testing the API

```bash
# Basic CPM analysis
curl -X POST http://localhost:5000/v1/critical-path \
  -H "Content-Type: application/json" \
  -d '{
    "tasks": [
      {"id": "A", "duration": 3, "dependencies": []},
      {"id": "B", "duration": 2, "dependencies": ["A"]},
      {"id": "C", "duration": 5, "dependencies": ["A"]},
      {"id": "D", "duration": 1, "dependencies": ["B", "C"]}
    ]
  }'
```

## API Endpoints

### Single Project Analysis

```
POST /v1/critical-path
```

Analyzes a single project task graph and returns CPM results.

**Request Body:**
```json
{
  "tasks": [
    {
      "id": "string",           // Required, unique task identifier
      "label": "string",        // Optional, human-readable name
      "duration": number,       // Required, >= 0
      "dependencies": ["string"], // Optional, array of task IDs
      "calendar_id": "string"   // Optional, for future calendar support
    }
  ],
  "options": {
    "duration_unit": "days",    // Optional: "hours", "days", "weeks"
    "near_critical_threshold": { // Optional: number or object
      "absolute": 2,            // Days
      "percentage": 20          // Percentage of project duration
    },
    "include_all_paths": false  // Optional: return all non-critical paths
  }
}
```

**Response:**
```json
{
  "project_duration": 11,
  "duration_unit": "days",
  "critical_path": ["A", "C", "D", "E"],
  "tasks": [
    {
      "id": "A",
      "label": "A",
      "duration": 3,
      "earliest_start": 0,
      "earliest_finish": 3,
      "latest_start": 0,
      "latest_finish": 3,
      "float": 0,
      "is_critical": true,
      "is_near_critical": false
    }
  ],
  "near_critical_paths": [],
  "warnings": [],
  "meta": {
    "task_count": 4,
    "edge_count": 4,
    "computation_ms": 1
  }
}
```

### Batch Analysis

```
POST /v1/critical-path/batch
```

Analyzes multiple independent projects in a single request.

**Request Body:**
```json
{
  "projects": [
    {
      "id": "project-1",
      "tasks": [...],
      "options": {...}
    },
    {
      "id": "project-2",
      "tasks": [...],
      "options": {...}
    }
  ]
}
```

**Response:**
```json
{
  "results": [
    {
      "id": "project-1",
      "status": "ok",
      "project_duration": 11,
      "critical_path": [...],
      "tasks": [...],
      "warnings": [],
      "meta": {...}
    },
    {
      "id": "project-2",
      "status": "error",
      "error": {
        "code": "CIRCULAR_DEPENDENCY",
        "message": "...",
        "affected_tasks": ["A", "B"]
      }
    }
  ],
  "meta": {
    "total_projects": 2,
    "succeeded": 1,
    "failed": 1,
    "total_computation_ms": 2
  }
}
```

### API Documentation

- **Scalar UI**: http://localhost:5000/scalar (development mode)
- **OpenAPI Spec**: http://localhost:5000/openapi/v1.json

## Internationalization (i18n)

The API supports multiple languages for error messages and warnings.

### Supported Languages

- **English** (`en`) - Default
- **Vietnamese** (`vi`)

### Usage

**Query Parameter:**
```bash
curl -X POST "http://localhost:5000/v1/critical-path?lang=vi" \
  -H "Content-Type: application/json" \
  -d '{"tasks": []}'
```

**Accept-Language Header:**
```bash
curl -X POST http://localhost:5000/v1/critical-path \
  -H "Content-Type: application/json" \
  -H "Accept-Language: vi" \
  -d '{"tasks": []}'
```

**Priority:** Query parameter > Header > Default (English)

## Error Handling

| HTTP Status | Error Code | Description |
|-------------|------------|-------------|
| 400 | `INVALID_INPUT` | Malformed request or validation error |
| 400 | `CIRCULAR_DEPENDENCY` | Graph contains cycles |
| 400 | `UNKNOWN_DEPENDENCY` | Task references non-existent dependency |
| 400 | `DUPLICATE_TASK_ID` | Multiple tasks share the same ID |
| 413 | `GRAPH_TOO_LARGE` | Exceeds 10,000 tasks limit |
| 422 | `NEGATIVE_DURATION` | Task has negative duration |
| 500 | `INTERNAL_ERROR` | Unexpected server error |

## Configuration

### Limits

| Parameter | Limit |
|-----------|-------|
| Max tasks per project | 10,000 |
| Max dependencies per task | 500 |
| Max projects per batch | 100 |
| Max total tasks per batch | 50,000 |
| Max request body size (single) | 5 MB |
| Max request body size (batch) | 20 MB |

### Near-Critical Threshold

The threshold can be specified as:

1. **Number** (absolute): `"near_critical_threshold": 2`
2. **Object** (absolute + percentage):
   ```json
   {
     "near_critical_threshold": {
       "absolute": 2,
       "percentage": 20
     }
   }
   ```
   
   When both are provided, the more permissive (larger) value is used.

## Examples

### Example 1: Simple Critical Path

```json
{
  "tasks": [
    {"id": "A", "duration": 3, "dependencies": []},
    {"id": "B", "duration": 2, "dependencies": ["A"]},
    {"id": "C", "duration": 5, "dependencies": ["A"]},
    {"id": "D", "duration": 1, "dependencies": ["B", "C"]}
  ]
}
```

**Result:**
- Critical Path: `["A", "C", "D"]` (11 days)
- Task B has float of 3 days

### Example 2: Near-Critical Paths

```json
{
  "tasks": [
    {"id": "A", "duration": 3, "dependencies": []},
    {"id": "B", "duration": 2, "dependencies": ["A"]},
    {"id": "C", "duration": 5, "dependencies": ["A"]}
  ],
  "options": {
    "near_critical_threshold": 5,
    "include_all_paths": true
  }
}
```

### Example 3: Batch Processing

```json
{
  "projects": [
    {
      "id": "website",
      "tasks": [
        {"id": "design", "duration": 5, "dependencies": []},
        {"id": "develop", "duration": 10, "dependencies": ["design"]}
      ]
    },
    {
      "id": "mobile-app",
      "tasks": [
        {"id": "research", "duration": 3, "dependencies": []},
        {"id": "prototype", "duration": 7, "dependencies": ["research"]}
      ]
    }
  ]
}
```

## Architecture

```
┌─────────────────┐     ┌──────────────┐     ┌──────────────┐
│   HTTP Request  │────▶│  Controller  │────▶│  CpmEngine   │
└─────────────────┘     └──────────────┘     └──────────────┘
                                                        │
                       ┌──────────────┐                │
                       │  Response    │◀───────────────┘
                       └──────────────┘
                              │
                       ┌──────────────┐
                       │ Localization│
                       └──────────────┘
```

### Key Components

- **Domain.fs**: Type definitions for requests, responses, and internal models
- **CpmEngine.fs**: Core CPM algorithm implementation
- **Localization.fs**: i18n support with English and Vietnamese translations
- **CriticalPathController.fs**: HTTP endpoints and error handling

## Performance

- **Time Complexity**: O(V + E) for graph analysis
- **Space Complexity**: O(V + E) for graph storage
- **Benchmarks** (typical):
  - 100 tasks: < 1ms
  - 1,000 tasks: < 10ms
  - 10,000 tasks: < 100ms

## Development

### Building

```bash
dotnet build
```

### Testing

```bash
# Run all tests
dotnet test

# Run with verbose output
dotnet test --verbosity normal
```

### Code Style

- Follow F# conventions
- Use discriminated unions for domain modeling
- Prefer pure functions for business logic
- Use railway-oriented programming for error handling

## Deployment

### Docker

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY bin/Release/net10.0/publish/ ./
EXPOSE 80
EXPOSE 443
ENTRYPOINT ["dotnet", "api-fsharp.dll"]
```

### Production Considerations

- Set `ASPNETCORE_ENVIRONMENT=Production`
- Configure HTTPS redirection
- Set up request rate limiting
- Monitor memory usage for large graphs
- Enable structured logging

## Contributing

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

## Acknowledgments

- Built with [ASP.NET Core](https://dotnet.microsoft.com/apps/aspnet)
- API documentation powered by [Scalar](https://scalar.com/)
- CPM algorithm based on classical project management methodology
