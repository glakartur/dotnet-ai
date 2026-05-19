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

## Claude Code Plugin

The repository ships a Claude Code plugin with a `dotnet-aicraft` skill. When active, the skill tells the AI agent when and how to use `dotnet aicraft` — it loads proactively on any .NET task and steers the agent away from grep/text search toward semantic commands.

### Installation

```bash
claude plugin marketplace add glakartur/dotnet-aicraft
claude plugin install dotnet-aicraft
```

The skill activates automatically in any Claude Code session where you work on a .NET project.

---

## For AI Agents

`dotnet-aicraft` is the **default tool for all symbol-level questions** in a .NET project. It gives compiler-level semantic answers that grep cannot — handling renamed variables, interface dispatch, overrides, and extension methods that text search misses.

### Find the solution file

```bash
find . -name "*.sln" -maxdepth 4 | head -5
```

### Typical agent workflows

**Before modifying a method — check all call sites:**
```bash
dotnet aicraft refs -s App.sln --file path/to/File.cs --line 42 --col 18
```

**Before deleting code — confirm it's unused:**
```bash
dotnet aicraft refs -s App.sln --symbol "MyApp.Services.OrderService.ProcessOrder"
dotnet aicraft unused -s App.sln --kind method --project MyApp.Services
```

**Safe rename — always dry-run first:**
```bash
dotnet aicraft rename -s App.sln --symbol "MyApp.Services.OrderService.ProcessOrder" --to "HandleOrder" --dry-run
dotnet aicraft rename -s App.sln --symbol "MyApp.Services.OrderService.ProcessOrder" --to "HandleOrder"
```

**Discover a fully-qualified name from a partial name:**
```bash
dotnet aicraft symbols -s App.sln --pattern "ProcessOrder*"
# Use the fullName from the result in follow-up commands
```

**Understand what implements an interface:**
```bash
dotnet aicraft impls -s App.sln --symbol "MyApp.Interfaces.IOrderProcessor"
```

### Output format

The default `text` format is optimized for LLM reading (compiler/ripgrep-style). Pass `--format json` only when you need structured fields for programmatic processing.

---

## Usage

### Shared options

Every command accepts these shared options:

| Option | Description |
|---|---|
| `--solution` / `-s` | Path to `.sln` or `.csproj` file (required) |
| `--format` | Output format: `text` (default, compiler/ripgrep-style — optimized for LLMs) or `json` (pretty-printed, stable schema for scripts) |
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
{
  "solutionRoot": "/home/me/App",
  "items": [
    {
      "file": "src/Controllers/OrderController.cs",
      "line": 87,
      "col": 9,
      "context": "_orderService.ProcessOrder(dto.ToRequest());"
    },
    {
      "file": "tests/OrderServiceTests.cs",
      "line": 34,
      "col": 22,
      "context": "var result = await _sut.ProcessOrder(request);"
    }
  ]
}
```

File paths are emitted relative to the solution directory with forward-slash
separators; the absolute root is surfaced once via `solutionRoot` (JSON) or a
`SolutionRoot: <abs path>` header line in text format.

### Find declaration/definition of a symbol

```bash
# By file location

dotnet aicraft definition --solution App.sln --file src/Services/OrderService.cs --line 42 --col 18

# By fully-qualified name

dotnet aicraft definition --solution App.sln --symbol "MyApp.Services.OrderService.ProcessOrder"
```

```json
{
  "solutionRoot": "/home/me/App",
  "fullName": "MyApp.Services.OrderService.ProcessOrder(MyApp.Contracts.OrderRequest)",
  "kind": "method",
  "file": "src/Services/OrderService.cs",
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

Valid `--kind` values: `all` (default), `type`, `member`, `namespace`, `class`, `interface`, `struct`, `enum`, `delegate`, `method`, `constructor`, `property`, `field`, `event`. Default page size is 200 (max 2000); use `--offset` to paginate.

`symbols` returns paged JSON:

```json
{
  "solutionRoot": "/home/me/App",
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

### List Roslyn diagnostics

```bash
dotnet aicraft diagnostics --solution App.sln --severity warning
dotnet aicraft diagnostics --solution App.sln --severity error
dotnet aicraft diagnostics --solution App.sln --project MyApp.Core --file src/Services/OrderService.cs
```

`--severity` values: `all` (default), `error`, `warning`, `info`, `hidden`.

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

# Ensure the daemon is running and set / extend the session-scoped idle timeout.
# This call returns promptly: if the daemon is already up it extends the idle
# deadline; if not, it spawns the daemon in the background and returns once
# the daemon is ready to accept commands.
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
| `dotnet aicraft server start` | Ensure daemon is running and set / extend the session idle timeout (returns promptly) |

Commands output a **compiler/ripgrep-style text format on stdout by default** —
optimized for LLM consumption (`file:line:col: context` lines for location lists,
MSBuild-style severity prefixes for diagnostics). Use `--format json` for the
stable, pretty-printed JSON schema intended for scripting. Daemon logs and
`--debug` output go to **stderr**, and any debug output is flushed **before**
the stdout result so result parsing stays clean.

---

## Limitations

- **SDK-style projects only** — non-SDK `.csproj` files (pre-2017 format with
  `packages.config`) are not supported on Linux/macOS. SDK-style projects
  targeting .NET Framework (e.g. `net48`) work fine.
- **Large solutions** — loading 100+ project solutions takes 30–60s on first run.
  Subsequent calls are fast thanks to the daemon.
- **Generated code** — Roslyn sees the code as written, not post-generation.

---

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for build instructions and internal architecture notes.

---

## License

MIT
