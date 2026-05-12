# dotnet-aicraft

**Semantic .NET code analysis for AI agents — powered by Roslyn.**

Instead of text search and grep-based refactoring, `dotnet-aicraft` gives AI agents real
semantic understanding of .NET code: find every reference to a symbol, safely rename
across an entire solution, explore call hierarchies — the same operations your IDE uses.

```
                    ┌─────────────────────────────┐
 dotnet aicraft refs     │  Daemon (lives in memory)   │
 dotnet aicraft rename   │  ┌─────────────────────┐    │
 dotnet aicraft callers  │  │  Roslyn Workspace   │    │
        │           │  │  Solution (cached)  │    │
        └──────────▶│  │  File watcher       │    │
    Unix socket     │  └─────────────────────┘    │
                    └─────────────────────────────┘
```

The daemon loads your solution once and keeps it in memory. Subsequent calls
respond in ~50ms instead of waiting for a full reload every time.

---

## Installation

```bash
dotnet tool install -g dotnet-aicraft
```

Requires .NET 9 SDK or later. Works on **Linux, macOS and Windows**.

---

## Usage

### Shared options

Every command accepts these shared options:

| Option | Description |
|---|---|
| `--solution` / `-s` | Path to `.sln` or `.csproj` file (required) |
| `--idle-timeout` | Session-scoped daemon idle timeout: `off` or a positive duration like `5m`, `1h` (default 60m) |
| `--debug` | Enable verbose debug logging to stderr |

Debug logging can also be enabled via the environment variable `DOTNET_AICRAFT_DEBUG=1`.

---

### Find all references to a symbol

```bash
# By file location (line/col — most useful for agents reading source files)
dotnet aicraft refs --solution App.sln --file src/Services/OrderService.cs --line 42 --col 18

# By fully-qualified name
dotnet aicraft refs --solution App.sln --symbol "MyApp.Services.OrderService.ProcessOrder"
```

```json
[
  {
    "file": "/src/Controllers/OrderController.cs",
    "line": 87,
    "col": 9,
    "context": "_orderService.ProcessOrder(dto.ToRequest());"
  },
  {
    "file": "/tests/OrderServiceTests.cs",
    "line": 34,
    "col": 22,
    "context": "var result = await _sut.ProcessOrder(request);"
  }
]
```

### Find declaration/definition of a symbol

```bash
# By file location

dotnet aicraft definition --solution App.sln --file src/Services/OrderService.cs --line 42 --col 18

# By fully-qualified name

dotnet aicraft definition --solution App.sln --symbol "MyApp.Services.OrderService.ProcessOrder"
```

```json
{
  "fullName": "MyApp.Services.OrderService.ProcessOrder(MyApp.Contracts.OrderRequest)",
  "kind": "method",
  "file": "/src/Services/OrderService.cs",
  "line": 42,
  "col": 18,
  "containingType": "MyApp.Services.OrderService",
  "containingNamespace": "MyApp.Services"
}
```

### Rename a symbol (safe, cross-solution)

```bash
# Preview changes first (dry run)
dotnet aicraft rename --solution App.sln \
  --symbol "MyApp.Services.OrderService.ProcessOrder" \
  --to "HandleOrder" --dry-run

# Apply the rename
dotnet aicraft rename --solution App.sln \
  --symbol "MyApp.Services.OrderService.ProcessOrder" \
  --to "HandleOrder"
```

```json
{
  "symbol": "MyApp.Services.OrderService.ProcessOrder",
  "newName": "HandleOrder",
  "applied": false,
  "dryRun": true,
  "changes": [
    { "file": "...", "line": 42, "col": 17, "oldText": "ProcessOrder", "newText": "HandleOrder" },
    { "file": "...", "line": 87, "col": 22, "oldText": "ProcessOrder", "newText": "HandleOrder" }
  ]
}
```

### Find implementations of an interface

```bash
dotnet aicraft impls --solution App.sln --symbol "MyApp.Interfaces.IOrderProcessor"
```

### Find callers/callees of a method (call graph)

```bash
# Backward-compatible mode (incoming callers, depth=1)
dotnet aicraft callers --solution App.sln --symbol "MyApp.Services.OrderService.ProcessOrder"

# Outgoing callees

dotnet aicraft callers --solution App.sln \
  --symbol "MyApp.Services.OrderService.ProcessOrder" \
  --direction outgoing --depth 2

# Both directions by file location

dotnet aicraft callers --solution App.sln \
  --file Services/OrderService.cs --line 42 --col 18 \
  --direction both --depth 2
```

### Search symbols by pattern

```bash
# Pattern + coarse kind filter (compatible with existing behavior)
dotnet aicraft symbols --solution App.sln --pattern "Process*" --kind method

# Granular kind filters

dotnet aicraft symbols --solution App.sln --pattern "I*" --kind interface
dotnet aicraft symbols --solution App.sln --pattern "*Repository" --kind class

# Pagination

dotnet aicraft symbols --solution App.sln --pattern "*" --kind all --limit 100 --offset 200
```

`symbols` returns paged JSON:

```json
{
  "items": [
    {
      "name": "ProcessOrder",
      "fullName": "MyApp.Services.OrderService.ProcessOrder(MyApp.Contracts.OrderRequest)",
      "kind": "method",
      "file": "/src/Services/OrderService.cs",
      "line": 42,
      "col": 18,
      "containingType": "MyApp.Services.OrderService",
      "containingNamespace": "MyApp.Services"
    }
  ],
  "hasMore": true
}
```

### List Roslyn diagnostics

```bash
dotnet aicraft diagnostics --solution App.sln --severity warning
dotnet aicraft diagnostics --solution App.sln --project MyApp.Core --file src/Services/OrderService.cs
```

### Find likely unused symbols (dead-code candidates)

```bash
dotnet aicraft unused --solution App.sln --kind method
dotnet aicraft unused --solution App.sln --project MyApp.Core --public-only
dotnet aicraft unused --solution App.sln --kind class --include-generated
```

### Daemon management

```bash
# Check if daemon is running and solution stats
dotnet aicraft server status --solution App.sln

# Reload solution (e.g. after adding/removing projects)
dotnet aicraft server reload --solution App.sln

# Stop the daemon
dotnet aicraft server stop --solution App.sln

# Start daemon with session-scoped idle timeout
dotnet aicraft server start --solution App.sln --idle-timeout 30m

# Disable idle auto-shutdown for current daemon session
dotnet aicraft server start --solution App.sln --idle-timeout off
```

---

## How the daemon works

The first time you run any `dotnet aicraft` command against a solution, the tool starts
a background daemon that loads the solution and keeps it in memory. The daemon:

- **Listens** on a Unix domain socket (path derived from the solution path)
- **Watches** for `.cs` file changes and applies them incrementally (no full reload)
- **Auto-shuts down after 60 minutes of inactivity** by default
- **Supports session-scoped timeout override** via `--idle-timeout` (`off` or a positive duration like `5m`, `1h`)

---

## Internal architecture

The command surface stays stable (`refs`, `definition`, `rename`, `impls`, `callers`,
`symbols`, `diagnostics`, `unused`, `server`), while implementation follows a
slice-first internal layout:

```
src/DotnetAICraft/
  Commands/
    <Command>/
      Entry.cs          # CLI-facing orchestration
      Validation.cs     # command-specific input normalization/validation
      UseCase.cs        # semantic operation against Roslyn/daemon model
      OutputMapping.cs  # response projection to contract models
```

Design intent:

- Keep behavior and daemon contract stable while making each command traceable end-to-end.
- Keep shared helpers narrow and explicit (`CommandHelpers` + daemon compatibility helpers).
- Add shared kernel only when reuse evidence exists across multiple slices.
- **Resets idle deadline after each handled request completion**
- **Supports** multiple simultaneous solutions (one daemon per solution)

Idle timeout is session-scoped only. If the daemon stops (manual stop or idle shutdown),
the next session starts with the default timeout unless you pass `--idle-timeout` again.

If timeout validation fails, the command returns a JSON error and timeout state is not changed.

### Windows stale socket self-heal policy

On Windows, daemon startup now applies a bounded stale-artifact policy before bind:

- Regular-file stale socket artifacts are auto-removed and startup continues.
- Reparse-point stale artifacts are auto-removed only when all safety gates pass:
  - local (non-UNC) target path,
  - target path under the current user's `%TEMP%` root,
  - daemon artifact naming boundary (`dotnet-aicraft-*.sock`),
  - supported reparse tag (symbolic link).
- Any safety-policy failure is fail-closed. Startup stops and returns structured details:
  - `error.code = DAEMON_STARTUP_STALE_SOCKET_INVALID_TYPE`
  - `error.details.reasonCode` (for example `outsideTempRoot`, `nonLocalTarget`, `unsupportedReparseTag`)
  - `error.details.remediation` with Windows-safe manual cleanup guidance.

For safety and privacy, stale-artifact diagnostics expose sanitized fields (artifact name/category,
reason code, remediation) and do not include absolute local paths.

```bash
# First call — daemon starts, loads solution (takes a few seconds)
$ dotnet aicraft refs --solution App.sln --file Foo.cs --line 10 --col 5
[dotnet-aicraft] Starting analysis daemon (first run loads the solution)...
[dotnet-aicraft] Ready.
[{ "file": "...", ... }]

# All subsequent calls — instant
$ dotnet aicraft callers --solution App.sln --file Foo.cs --line 10 --col 5
[{ ... }]   # ~50ms
```

---

## All commands

| Command | Description |
|---|---|
| `dotnet aicraft refs` | All references to a symbol |
| `dotnet aicraft definition` | Resolve declaration by location or FQN |
| `dotnet aicraft rename` | Safe rename across solution (with `--dry-run`) |
| `dotnet aicraft impls` | Implementations of interface/abstract member |
| `dotnet aicraft callers` | Call graph (`incoming`, `outgoing`, `both`) with `--depth` |
| `dotnet aicraft symbols` | Search symbols by name pattern with pagination |
| `dotnet aicraft diagnostics` | Roslyn diagnostics (`severity/project/file` filters) |
| `dotnet aicraft unused` | Candidates for unused/dead code |
| `dotnet aicraft server status` | Daemon status |
| `dotnet aicraft server reload` | Reload solution |
| `dotnet aicraft server stop` | Stop daemon |
| `dotnet aicraft server start` | Start daemon with custom idle timeout |

All commands output **JSON to stdout**. Daemon logs go to **stderr** so they
don't interfere with JSON parsing.

---

## Building from source

```bash
git clone https://github.com/yourusername/dotnet-aicraft
cd dotnet-aicraft

dotnet restore
dotnet build -c Release

# Install locally for testing
dotnet pack src/DotnetAICraft/DotnetAICraft.csproj -c Release -o ./nupkg
dotnet tool install -g --add-source ./nupkg dotnet-aicraft

# Run tests
dotnet test
```

---

## Limitations

- **SDK-style projects only** — classic .NET Framework `.csproj` files (pre-2017
  format with `packages.config`) are not supported on Linux/macOS.
- **Large solutions** — loading 100+ project solutions takes 30–60s on first run.
  Subsequent calls are fast thanks to the daemon.
- **Generated code** — Roslyn sees the code as written, not post-generation.

---

## License

MIT
