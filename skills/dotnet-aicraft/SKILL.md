---
name: dotnet-aicraft
description: >
  This skill should be used whenever working on a .NET solution — not only when explicitly asked.
  Load proactively when: exploring or onboarding to a .NET codebase; modifying, deleting, or moving
  any method, class, interface, or property; planning a refactoring; checking whether code is safe to
  remove; understanding how classes relate; navigating call hierarchies (incoming/outgoing); resolving
  declarations; inspecting compiler diagnostics; or finding unused/dead-code candidates.
  Also load when the user asks to "find references", "find all usages", "rename a symbol",
  "find implementations", "find callers", "find definition", "search symbols", "diagnostics",
  "unused code", or "check daemon status".
  Prefer `dotnet aicraft` over grep/text-search for any symbol-level question in a .NET project.
version: 0.3.0
---

# dotnet-aicraft

`dotnet-aicraft` provides semantic .NET code analysis for AI agents powered by Roslyn. Use it as the **default tool** for any symbol-level question in a .NET solution — it gives exact, compiler-level answers instead of text-search approximations.

## Architecture

A background daemon loads the solution once into memory. All subsequent commands communicate with the daemon over a Unix domain socket, returning results in ~50ms. The daemon auto-starts on first use and shuts down after 60 minutes of inactivity.

```
 dotnet aicraft refs        ┌─────────────────────┐
 dotnet aicraft rename  ──▶ │  Daemon             │
 dotnet aicraft callers     │  Roslyn Workspace   │
          │                 │  Solution (cached)  │
          └─ Unix socket ──▶│  File watcher       │
                            └─────────────────────┘
```

File changes are applied incrementally — no full reload needed when `.cs` files change.

## When to Use Proactively

Use `dotnet aicraft` without being asked whenever any of these situations arise during .NET work:

| Situation | Command to reach for |
|---|---|
| About to modify a method or property | `refs` — know all call sites first |
| About to delete a class or member | `refs` + `callers` + `unused` — confirm impact and dead-code confidence |
| About to rename anything | `rename --dry-run` then `rename` |
| Need declaration from usage location | `definition --file --line --col` |
| Implementing a feature that touches an interface | `impls` — discover all implementors |
| Exploring an unfamiliar codebase | `symbols --pattern "*"` + `impls` on key interfaces |
| Need compiler signal before edits | `diagnostics --severity error` |
| Need to know what calls into / out of a method | `callers --direction incoming|outgoing|both --depth N` |
| Partial symbol name from context | `symbols --pattern "Partial*"` |
| File changes suggest projects were added/removed | `server reload` |

**Prefer `dotnet aicraft` over grep** for all symbol-level questions. Grep finds text; `dotnet aicraft` finds semantic usage — it handles renamed variables, overrides, interface dispatch, and XML doc references that grep misses.

## Installation

```bash
dotnet tool install -g dotnet-aicraft
```

Requires .NET 9 SDK or later. Works on Linux, macOS, and Windows.

## Command Overview

| Command | Purpose |
|---|---|
| `dotnet aicraft refs` | All references to a symbol |
| `dotnet aicraft definition` | Declaration of a symbol by location or FQN |
| `dotnet aicraft rename` | Safe rename across the whole solution |
| `dotnet aicraft impls` | Implementations of an interface or abstract member |
| `dotnet aicraft callers` | Incoming callers or full call graph (incoming/outgoing/both) |
| `dotnet aicraft diagnostics` | Roslyn diagnostics with severity/project/file filters |
| `dotnet aicraft symbols` | Pattern search with granular `--kind` + `--limit/--offset` |
| `dotnet aicraft unused` | Dead-code candidates with `reason` and `confidence` |
| `dotnet aicraft server status` | Check daemon status and solution stats |
| `dotnet aicraft server reload` | Reload solution after adding/removing projects |
| `dotnet aicraft server stop` | Stop the daemon |
| `dotnet aicraft server start` | Start daemon explicitly with custom timeout |

All commands output **JSON to stdout**. Daemon startup messages go to **stderr** and do not interfere with JSON parsing.

## Shared Options

Every command requires `--solution` (or `-s`):

```bash
--solution / -s   # Path to .sln or .csproj file (required)
--idle-timeout    # Session-scoped idle timeout: "off" or duration like "5m", "1h" (default 60m)
```

## Identifying Symbols

Two strategies for targeting a symbol:

**By file location** (preferred when reading source files):
```bash
--file path/to/File.cs --line 42 --col 18
```

**By fully-qualified name** (preferred when you know the symbol):
```bash
--symbol "MyApp.Services.OrderService.ProcessOrder"
```

## Core Commands

### Find All References

```bash
# By location
dotnet aicraft refs --solution App.sln --file src/Services/OrderService.cs --line 42 --col 18

# By fully-qualified name
dotnet aicraft refs --solution App.sln --symbol "MyApp.Services.OrderService.ProcessOrder"
```

Output: JSON array of `{ file, line, col, context }` objects.

### Safe Rename

Always run with `--dry-run` first to preview changes:

```bash
# Preview
dotnet aicraft rename --solution App.sln \
  --symbol "MyApp.Services.OrderService.ProcessOrder" \
  --to "HandleOrder" --dry-run

# Apply
dotnet aicraft rename --solution App.sln \
  --symbol "MyApp.Services.OrderService.ProcessOrder" \
  --to "HandleOrder"
```

Output: JSON with `{ symbol, newName, applied, dryRun, changes[] }`.

### Find Implementations

```bash
dotnet aicraft impls --solution App.sln \
  --symbol "MyApp.Interfaces.IOrderProcessor"
```

Output: JSON array of implementing types with file locations.

### Find Callers / Call Graph

```bash
# Backward-compatible mode (incoming + depth=1)
dotnet aicraft callers --solution App.sln \
  --symbol "MyApp.Services.OrderService.ProcessOrder"

# Full graph mode
dotnet aicraft callers --solution App.sln \
  --symbol "MyApp.Services.OrderService.ProcessOrder" \
  --direction both --depth 2
```

Output:
- `incoming + depth=1`: JSON array of caller records (`callerSymbol`, `isDirect`, `file`, `line`, `col`, `context`)
- otherwise: `CallGraphResult` object with `rootId`, `direction`, `depth`, `nodes[]`, `edges[]`

### Find Definition

```bash
# By usage location
dotnet aicraft definition --solution App.sln \
  --file Services/OrderService.cs --line 42 --col 18

# By fully-qualified name
dotnet aicraft definition --solution App.sln \
  --symbol "MyApp.Services.OrderService.ProcessOrder"
```

Output: single `DefinitionResult` object. For metadata-only symbols, `file/line/col` may be null.

### Get Diagnostics

```bash
dotnet aicraft diagnostics --solution App.sln --severity error
dotnet aicraft diagnostics --solution App.sln --project MyApp.Core
dotnet aicraft diagnostics --solution App.sln --file src/Services/OrderService.cs
```

Output: sorted JSON array of diagnostics (`project`, `id`, `severity`, `message`, location fields).

### Find Unused Candidates

```bash
dotnet aicraft unused --solution App.sln --kind method
dotnet aicraft unused --solution App.sln --project MyApp.Core --public-only
```

Output: `UnusedScanSummary` with `scanned` and `items[]`; each item includes `reason` and `confidence`.

### Search Symbols

```bash
# Wildcard search (supports * and ?)
dotnet aicraft symbols --solution App.sln --pattern "Process*"

# Granular kind filter
dotnet aicraft symbols --solution App.sln --pattern "Process*" --kind method

# Pagination
dotnet aicraft symbols --solution App.sln --pattern "*" --kind class --limit 100 --offset 200
```

Output: `SymbolsResultPage` object with `items[]` and `hasMore`.

## Daemon Management

The daemon starts automatically on first use. Explicit management:

```bash
# Status and solution stats
dotnet aicraft server status --solution App.sln

# Reload after project structure changes
dotnet aicraft server reload --solution App.sln

# Stop the daemon
dotnet aicraft server stop --solution App.sln

# Start with custom timeout
dotnet aicraft server start --solution App.sln --idle-timeout 30m

# Disable idle shutdown for this session
dotnet aicraft server start --solution App.sln --idle-timeout off
```

Idle timeout is session-scoped only. A stopped daemon always restarts with the default 60m timeout unless explicitly passed.

## First Run Behavior

The first call starts the daemon and loads the solution (takes a few seconds for large solutions). Daemon startup messages appear on stderr:

```
[dotnet-aicraft] Starting analysis daemon (first run loads the solution)...
[dotnet-aicraft] Ready.
```

All subsequent calls return in ~50ms.

## Agent Workflows

### Refactoring a symbol

1. Identify the symbol's fully-qualified name using `symbols` or by reading the source.
2. Run `rename --dry-run` and inspect the `changes[]` array.
3. Verify the change list looks correct.
4. Run `rename` without `--dry-run` to apply.

### Investigating usage before deletion

1. Run `refs` to find all usages.
2. Run `callers --direction incoming --depth 1` for immediate callers.
3. Run `unused` on the same kind/project to estimate cleanup candidates (`reason` + `confidence`).
4. Decide based on semantic evidence, not text search guesses.

### Discovering interface implementations

1. Run `impls` with the interface's fully-qualified name.
2. Check each implementing type's location for follow-up analysis.

### Resolving declaration from cursor position

1. Run `definition --file --line --col`.
2. If `file/line/col` is null, treat it as metadata-only declaration.
3. Use returned `fullName` for follow-up commands (`refs`, `callers`, `rename`).

### Fast diagnostics triage before refactor

1. Run `diagnostics --severity error`.
2. If needed, narrow by `--project` or `--file`.
3. Fix blockers before large symbol-level changes.

### Finding symbols when the name is partially known

1. Use `symbols --pattern "Partial*"` with wildcards.
2. Narrow by granular kinds (`class|interface|method|property|field|event|...`).
3. Use the `fullName` from results in subsequent commands.

## Limitations

- **SDK-style projects only** — classic .NET Framework `.csproj` files with `packages.config` are not supported on Linux/macOS.
- **Large solutions** — 100+ project solutions take 30–60s on first load.
- **Generated code** — Roslyn sees code as written, not post-generation output.
- **One daemon per solution** — multiple solutions run independent daemons simultaneously.

## Additional Resources

For extended examples, consult:
- **`references/commands.md`** — Expanded command examples and output samples
- **`references/patterns.md`** — Common agent workflow patterns and decision trees

`dotnet aicraft <command> --help` is the source of truth for current flags.
