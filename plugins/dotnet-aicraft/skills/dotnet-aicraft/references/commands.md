# dotnet-aicraft — Full Command Reference

## Global Options

All commands accept:

| Option | Short | Required | Description |
|---|---|---|---|
| `--solution` | `-s` | Yes | Path to `.sln` or `.csproj` |
| `--format` | — | No | Output format: `text` (default, compiler/ripgrep-style — LLM-optimized) or `json` (pretty-printed, stable schema for scripting) |
| `--idle-timeout` | — | No | Session-scoped daemon idle timeout: `off` or duration (`5m`, `30m`, `1h`). Default: `60m` |
| `--debug` | — | No | Verbose debug logging to stderr. Equivalent to `DOTNET_AICRAFT_DEBUG=1`. Debug output is flushed before the stdout result. |

### Path conventions in output

File paths in command results are emitted **relative to the solution
directory**, with forward-slash separators on every platform. The absolute
solution root is surfaced once per response:

- `--format text` — a `SolutionRoot: <abs path>` header line near the top.
- `--format json` — a top-level `solutionRoot` field on the envelope.

For commands that return lists (`refs`, `impls`, `callers`, `symbols`,
`diagnostics`, `unused`), the JSON envelope is:

```json
{ "solutionRoot": "/abs/path", "items": [ ... ] }
```

Out-of-tree paths (different volume, generator output outside the solution
tree) fall back to absolute form with forward-slash normalization.

`rename` keeps its existing summary shape (no `SolutionRoot:` text header line);
relative paths still apply inside its `changes[]`.

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

### Examples

```bash
dotnet aicraft refs --solution App.sln \
  --file src/Services/OrderService.cs --line 42 --col 18

dotnet aicraft refs --solution App.sln \
  --symbol "MyApp.Services.OrderService.ProcessOrder"
```

### Output Schema (`--format json`)

```json
{
  "solutionRoot": "/abs/path/to/repo",
  "items": [
    {
      "file": "src/Controllers/OrderController.cs",
      "line": 87,
      "col": 9,
      "context": "_orderService.ProcessOrder(dto.ToRequest());"
    }
  ]
}
```

### Output (`--format text`, default)

```
12 references to MyApp.Services.OrderService.ProcessOrder in App.sln
SolutionRoot: /abs/path/to/repo

src/Controllers/OrderController.cs:87:9: _orderService.ProcessOrder(dto.ToRequest());
```

---

## `dotnet aicraft definition`

Resolve the declaration of a symbol by source location or fully-qualified name.

### Options

| Option | Required | Description |
|---|---|---|
| `--solution` | Yes | Path to solution |
| `--file` | If not using `--symbol` | Source file path |
| `--line` | With `--file` | 1-based line number |
| `--col` | With `--file` | 1-based column number |
| `--symbol` | If not using `--file` | Fully-qualified symbol name |

### Output Schema (`--format json`)

```json
{
  "solutionRoot": "/abs/path/to/repo",
  "fullName": "MyApp.Services.OrderService.ProcessOrder(MyApp.Contracts.OrderRequest)",
  "kind": "method",
  "file": "src/Services/OrderService.cs",
  "line": 42,
  "col": 18,
  "containingType": "MyApp.Services.OrderService",
  "containingNamespace": "MyApp.Services"
}
```

For metadata-only symbols (assembly references), `file/line/col` may be null.
Use `fullName` for follow-up commands (`refs`, `callers`, `rename`).

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
| `--to` | Yes | New name (just the identifier, not fully-qualified) |
| `--dry-run` | No | Preview changes without applying |

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

### Output Schema (`--format json`)

```json
{
  "symbol": "MyApp.Services.OrderService.ProcessOrder",
  "newName": "HandleOrder",
  "applied": false,
  "dryRun": true,
  "changes": [
    {
      "file": "src/Services/OrderService.cs",
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
- `changes`: list of all affected locations (paths relative to solution root)

---

## `dotnet aicraft impls`

Find all implementations of an interface or abstract member.

### Options

| Option | Required | Description |
|---|---|---|
| `--solution` | Yes | Path to solution |
| `--symbol` | Yes | Fully-qualified interface or abstract member name |

### Output Schema (`--format json`)

```json
{
  "solutionRoot": "/abs/path/to/repo",
  "items": [
    {
      "symbol": "MyApp.Services.OrderService",
      "file": "src/Services/OrderService.cs",
      "line": 12,
      "col": 14,
      "context": "public class OrderService : IOrderProcessor"
    }
  ]
}
```

---

## `dotnet aicraft callers`

Find call sites that invoke a method (call hierarchy). Default is incoming
callers at depth 1 (legacy flat list). Other directions/depths return a full
call graph.

### Options

| Option | Required | Description |
|---|---|---|
| `--solution` | Yes | Path to solution |
| `--file` | If not using `--symbol` | Source file path |
| `--line` | With `--file` | 1-based line number |
| `--col` | With `--file` | 1-based column number |
| `--symbol` | If not using `--file` | Fully-qualified method name |
| `--direction` | No | `incoming` (default), `outgoing`, or `both` |
| `--depth` | No | Traversal depth (default 1) |

### Examples

```bash
# Backward-compatible flat list (incoming, depth=1)
dotnet aicraft callers --solution App.sln \
  --symbol "MyApp.Services.OrderService.ProcessOrder"

# Full graph
dotnet aicraft callers --solution App.sln \
  --symbol "MyApp.Services.OrderService.ProcessOrder" \
  --direction both --depth 2
```

### Output Schema (`--format json`)

`incoming + depth=1` (legacy flat list):

```json
{
  "solutionRoot": "/abs/path",
  "items": [
    { "callerSymbol": "MyApp.Controllers.OrderController.Post",
      "isDirect": true,
      "file": "src/Controllers/OrderController.cs", "line": 55, "col": 18,
      "context": "await _service.ProcessOrder(request);" }
  ]
}
```

Other directions/depths return `CallGraphResult { rootId, direction, depth, nodes[], edges[] }`.

---

## `dotnet aicraft symbols`

Search for symbols by name pattern. Supports glob-style wildcards (`*`, `?`).

### Options

| Option | Required | Description |
|---|---|---|
| `--solution` | Yes | Path to solution |
| `--pattern` | Yes | Name pattern with optional `*` and `?` wildcards |
| `--kind` | No | Filter by kind. Coarse: `all`, `type`, `member`, `namespace`. Granular: `class`, `interface`, `struct`, `enum`, `delegate`, `method`, `property`, `field`, `event`. Default: `all` |
| `--limit` | No | Max items per page |
| `--offset` | No | Skip first N matches (for pagination) |

### Examples

```bash
dotnet aicraft symbols --solution App.sln --pattern "Process*" --kind method
dotnet aicraft symbols --solution App.sln --pattern "I*" --kind interface
dotnet aicraft symbols --solution App.sln --pattern "*" --kind all --limit 100 --offset 200
```

### Output Schema (`--format json`)

```json
{
  "solutionRoot": "/abs/path",
  "items": [
    {
      "name": "ProcessOrder",
      "fullName": "MyApp.Services.OrderService.ProcessOrder(MyApp.Contracts.OrderRequest)",
      "kind": "method",
      "file": "src/Services/OrderService.cs",
      "line": 42,
      "col": 18,
      "containingType": "MyApp.Services.OrderService",
      "containingNamespace": "MyApp.Services"
    }
  ],
  "hasMore": true
}
```

---

## `dotnet aicraft diagnostics`

List Roslyn diagnostics across the solution with filters.

### Options

| Option | Required | Description |
|---|---|---|
| `--solution` | Yes | Path to solution |
| `--severity` | No | `error`, `warning`, `info`, `hidden` |
| `--project` | No | Restrict to a single project name |
| `--file` | No | Restrict to a single file |

### Example

```bash
dotnet aicraft diagnostics --solution App.sln --severity warning
dotnet aicraft diagnostics --solution App.sln --project MyApp.Core --file src/Services/OrderService.cs
```

### Output (`--format text`, MSBuild-style)

```
3 errors, 17 warnings
SolutionRoot: /abs/path

error src/Bar.cs:88:1 [CS0103]: The name 'foo' does not exist in the current context
warning src/Foo.cs:42:5 [CS0168]: The variable 'x' is declared but never used
```

### Output Schema (`--format json`)

```json
{
  "solutionRoot": "/abs/path",
  "items": [
    { "project": "MyApp.Core", "id": "CS0168", "severity": "warning",
      "message": "...", "file": "src/Foo.cs", "line": 42, "col": 5 }
  ]
}
```

---

## `dotnet aicraft unused`

Find symbols with no references — candidates for dead code.

### Options

| Option | Required | Description |
|---|---|---|
| `--solution` | Yes | Path to solution |
| `--kind` | No | Restrict to a kind (e.g. `method`, `class`, `property`) |
| `--project` | No | Restrict to a single project |
| `--public-only` | No | Only include public symbols |
| `--include-generated` | No | Include generated code (default: skipped) |

### Output Schema (`--format json`)

```json
{
  "solutionRoot": "/abs/path",
  "scanned": 1234,
  "items": [
    { "symbol": "MyApp.Internal.LegacyHelper.DoStuff",
      "kind": "method", "reason": "no references",
      "confidence": "high",
      "file": "src/Internal/LegacyHelper.cs", "line": 17, "col": 22 }
  ]
}
```

---

## `dotnet aicraft server`

Manage the background analysis daemon.

### `server status`

```bash
dotnet aicraft server status --solution App.sln
```

Returns daemon state, project/document counts, current idle timeout, uptime.

### `server start`

`server start` is a **fast, idempotent ensure-running** call. It returns
promptly: with no running daemon it spawns one in the background and waits only
until the daemon is ready to accept commands; with a running daemon it extends
the idle deadline (no `--idle-timeout`) or overwrites the session idle timeout
(with `--idle-timeout`, equivalent to `server reload --idle-timeout`).

```bash
# Ensure the daemon is running
dotnet aicraft server start --solution App.sln

# Ensure running and set / extend the session idle timeout
dotnet aicraft server start --solution App.sln --idle-timeout 30m

# Disable auto-shutdown for this session
dotnet aicraft server start --solution App.sln --idle-timeout off
```

External scripts that previously relied on `server start` blocking until
shutdown should switch to a long-lived monitoring loop or use the daemon's
idle-timeout settings.

Note: Any analysis command also starts the daemon if not already running.
The internal foreground daemon process is reachable via a hidden
`server daemon` subcommand — it is for internal auto-spawn use only and not
intended for direct invocation.

### `server stop`

```bash
dotnet aicraft server stop --solution App.sln
```

Sends a graceful shutdown signal. The next analysis command will restart the daemon.

### `server reload`

```bash
dotnet aicraft server reload --solution App.sln
```

Use after adding or removing `.csproj` files from the solution. Normal `.cs`
file changes are picked up automatically via the file watcher (which ignores
`obj/`, `bin/`, and `*.g.cs`).

### Windows stale-socket self-heal

On Windows, daemon startup applies a bounded stale-artifact policy before bind:
regular-file stale sockets are auto-removed; reparse-point artifacts are
removed only when every safety gate passes (local non-UNC target, target under
the current user's `%TEMP%` root, `dotnet-aicraft-*.sock` naming, supported
reparse tag). Any failure is fail-closed and returns:

- `error.code = DAEMON_STARTUP_STALE_SOCKET_INVALID_TYPE`
- `error.details.reasonCode` (e.g. `outsideTempRoot`, `nonLocalTarget`, `unsupportedReparseTag`)
- `error.details.remediation` with Windows-safe manual cleanup guidance

Diagnostics are sanitized — no absolute local paths are surfaced.

---

## Error Output

When a command fails, the JSON envelope carries an `error` object:

```json
{ "error": { "code": "SOLUTION_UNAVAILABLE",
             "message": "Solution is currently unavailable.",
             "details": { "hint": "Run 'server reload' or fix project files and retry." } } }
```

In `--format text`, errors render as:

```
error SOLUTION_UNAVAILABLE: Solution is currently unavailable.
hint: Run 'server reload' or fix the solution/project files and retry.
```

Always check for the `error` field when parsing JSON output programmatically.

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
