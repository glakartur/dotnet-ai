# dotnet-aicraft — Full Command Reference

## Global Options

All commands accept:

| Option | Short | Required | Description |
|---|---|---|---|
| `--solution` | `-s` | Yes | Path to `.sln` or `.csproj` |
| `--idle-timeout` | — | No | Session-scoped daemon idle timeout: `off` or duration (`5m`, `30m`, `1h`). Default: `60m` |

---

## `dotnet aicraft refs`

Find all references to a symbol in the solution.

### Options

| Option | Required | Description |
|---|---|---|
| `--solution` | Yes | Path to solution |
| `--file` | If not using `--symbol` | Source file path |
| `--line` | With `--file` | 1-based line number |
| `--col` | With `--file` | 1-based column number |
| `--symbol` | If not using `--file` | Fully-qualified symbol name |
| `--idle-timeout` | No | Session-scoped timeout override |

### Examples

```bash
dotnet aicraft refs --solution App.sln \
  --file src/Services/OrderService.cs --line 42 --col 18

dotnet aicraft refs --solution App.sln \
  --symbol "MyApp.Services.OrderService.ProcessOrder"
```

### Output Schema

```json
[
  {
    "file": "/abs/path/to/File.cs",
    "line": 87,
    "col": 9,
    "context": "_orderService.ProcessOrder(dto.ToRequest());"
  }
]
```

---

## `dotnet aicraft rename`

Safely rename a symbol across the entire solution. All call sites, declarations, and XML docs are updated atomically.

### Options

| Option | Required | Description |
|---|---|---|
| `--solution` | Yes | Path to solution |
| `--file` | If not using `--symbol` | Source file path |
| `--line` | With `--file` | 1-based line number |
| `--col` | With `--file` | 1-based column number |
| `--symbol` | If not using `--file` | Fully-qualified symbol name |
| `--to` | Yes | New name (not fully-qualified — just the identifier) |
| `--dry-run` | No | Preview changes without applying |
| `--idle-timeout` | No | Session-scoped timeout override |

### Examples

```bash
# Preview first (always recommended)
dotnet aicraft rename --solution App.sln \
  --symbol "MyApp.Services.OrderService.ProcessOrder" \
  --to "HandleOrder" --dry-run

# Apply rename
dotnet aicraft rename --solution App.sln \
  --symbol "MyApp.Services.OrderService.ProcessOrder" \
  --to "HandleOrder"
```

### Output Schema

```json
{
  "symbol": "MyApp.Services.OrderService.ProcessOrder",
  "newName": "HandleOrder",
  "applied": false,
  "dryRun": true,
  "changes": [
    {
      "file": "/abs/path/to/OrderService.cs",
      "line": 42,
      "col": 17,
      "oldText": "ProcessOrder",
      "newText": "HandleOrder"
    }
  ]
}
```

- `applied`: `true` when changes were written to disk
- `dryRun`: mirrors the `--dry-run` flag
- `changes`: list of all affected locations

---

## `dotnet aicraft impls`

Find all implementations of an interface or abstract member.

### Options

| Option | Required | Description |
|---|---|---|
| `--solution` | Yes | Path to solution |
| `--symbol` | Yes | Fully-qualified interface or abstract member name |
| `--idle-timeout` | No | Session-scoped timeout override |

### Example

```bash
dotnet aicraft impls --solution App.sln \
  --symbol "MyApp.Interfaces.IOrderProcessor"
```

### Output Schema

```json
[
  {
    "symbol": "MyApp.Services.OrderService",
    "file": "/abs/path/to/OrderService.cs",
    "line": 12,
    "col": 14,
    "context": "public class OrderService : IOrderProcessor"
  }
]
```

---

## `dotnet aicraft callers`

Find all call sites that invoke a method (call hierarchy).

### Options

| Option | Required | Description |
|---|---|---|
| `--solution` | Yes | Path to solution |
| `--file` | If not using `--symbol` | Source file path |
| `--line` | With `--file` | 1-based line number |
| `--col` | With `--file` | 1-based column number |
| `--symbol` | If not using `--file` | Fully-qualified method name |
| `--idle-timeout` | No | Session-scoped timeout override |

### Example

```bash
dotnet aicraft callers --solution App.sln \
  --symbol "MyApp.Services.OrderService.ProcessOrder"
```

### Output Schema

```json
[
  {
    "file": "/abs/path/to/Controller.cs",
    "line": 55,
    "col": 18,
    "context": "await _service.ProcessOrder(request);"
  }
]
```

---

## `dotnet aicraft symbols`

Search for symbols by name pattern. Supports glob-style wildcards (`*` matches any substring, `?` matches one character).

### Options

| Option | Required | Description |
|---|---|---|
| `--solution` | Yes | Path to solution |
| `--pattern` | Yes | Name pattern with optional `*` and `?` wildcards |
| `--kind` | No | Filter by kind: `all` \| `type` \| `member` \| `namespace`. Default: `all` |
| `--idle-timeout` | No | Session-scoped timeout override |

### Examples

```bash
# All symbols matching "Process*"
dotnet aicraft symbols --solution App.sln --pattern "Process*"

# Only methods matching "Handle*"
dotnet aicraft symbols --solution App.sln --pattern "Handle*" --kind member

# Exact namespace search
dotnet aicraft symbols --solution App.sln --pattern "MyApp.Services" --kind namespace
```

### Output Schema

```json
[
  {
    "name": "ProcessOrder",
    "fullyQualifiedName": "MyApp.Services.OrderService.ProcessOrder",
    "kind": "Method",
    "file": "/abs/path/to/OrderService.cs",
    "line": 42,
    "col": 17
  }
]
```

---

## `dotnet aicraft server`

Manage the background analysis daemon.

### `server status`

```bash
dotnet aicraft server status --solution App.sln
```

Output:
```json
{
  "running": true,
  "solution": "/abs/path/to/App.sln",
  "projects": 12,
  "documents": 348,
  "idleTimeout": "60m",
  "uptimeSeconds": 182
}
```

### `server start`

```bash
# Start with default 60m timeout
dotnet aicraft server start --solution App.sln

# Start with custom timeout
dotnet aicraft server start --solution App.sln --idle-timeout 30m

# Disable auto-shutdown for this session
dotnet aicraft server start --solution App.sln --idle-timeout off
```

Note: Any analysis command also starts the daemon if not already running.

### `server stop`

```bash
dotnet aicraft server stop --solution App.sln
```

Sends a graceful shutdown signal. The next analysis command will restart the daemon.

### `server reload`

```bash
dotnet aicraft server reload --solution App.sln
```

Use after adding or removing `.csproj` files from the solution. Normal `.cs` file changes are picked up automatically via the file watcher.

---

## Error Output

When a command fails, it returns a JSON error object to stdout:

```json
{
  "error": "Symbol not found",
  "details": "No symbol at MyApp.Services.Missing"
}
```

Always check for the `error` field when parsing output programmatically.

---

## Symbol Name Format

Fully-qualified names follow C# namespace conventions:

| Kind | Example |
|---|---|
| Namespace | `MyApp.Services` |
| Type | `MyApp.Services.OrderService` |
| Method | `MyApp.Services.OrderService.ProcessOrder` |
| Property | `MyApp.Services.OrderService.IsActive` |
| Field | `MyApp.Services.OrderService._cache` |
| Interface | `MyApp.Interfaces.IOrderProcessor` |

When unsure of the fully-qualified name, use `symbols --pattern` to discover it.
