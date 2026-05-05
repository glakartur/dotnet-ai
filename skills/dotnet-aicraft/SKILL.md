---
name: dotnet-aicraft
description: >
  This skill should be used whenever working on a .NET solution — not only when explicitly asked.
  Load proactively when: exploring or onboarding to a .NET codebase; modifying, deleting, or moving
  any method, class, interface, or property; planning a refactoring; checking whether code is safe to
  remove; understanding how classes relate; navigating call hierarchies; or renaming anything in C#.
  Also load when the user asks to "find references", "find all usages", "rename a symbol",
  "find implementations", "find callers", "search symbols", or "check daemon status".
  Prefer `dotnet aicraft` over grep/text-search for any symbol-level question in a .NET project.
version: 0.2.0
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
| About to delete a class or member | `refs` + `callers` — confirm nothing depends on it |
| About to rename anything | `rename --dry-run` then `rename` |
| Implementing a feature that touches an interface | `impls` — discover all implementors |
| Exploring an unfamiliar codebase | `symbols --pattern "*"` + `impls` on key interfaces |
| Need to know what calls into a method | `callers` |
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
| `dotnet aicraft rename` | Safe rename across the whole solution |
| `dotnet aicraft impls` | Implementations of an interface or abstract member |
| `dotnet aicraft callers` | All callers of a method (call hierarchy) |
| `dotnet aicraft symbols` | Search symbols by name pattern |
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

### Find Callers

```bash
dotnet aicraft callers --solution App.sln \
  --symbol "MyApp.Services.OrderService.ProcessOrder"

# Or by location
dotnet aicraft callers --solution App.sln \
  --file Services/OrderService.cs --line 42 --col 18
```

Output: JSON array of call sites with context snippets.

### Search Symbols

```bash
# Wildcard search (supports * and ?)
dotnet aicraft symbols --solution App.sln --pattern "Process*"

# Filtered by kind: all | type | member | namespace (default: all)
dotnet aicraft symbols --solution App.sln --pattern "Process*" --kind method
```

Output: JSON array of matching symbols with fully-qualified names and locations.

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
2. Run `callers` to see the call hierarchy.
3. Decide based on evidence, not text search guesses.

### Discovering interface implementations

1. Run `impls` with the interface's fully-qualified name.
2. Check each implementing type's location for follow-up analysis.

### Finding symbols when the name is partially known

1. Use `symbols --pattern "Partial*"` with wildcards.
2. Filter with `--kind type|member|namespace` to narrow results.
3. Use the fully-qualified name from results in subsequent commands.

## Limitations

- **SDK-style projects only** — classic .NET Framework `.csproj` files with `packages.config` are not supported on Linux/macOS.
- **Large solutions** — 100+ project solutions take 30–60s on first load.
- **Generated code** — Roslyn sees code as written, not post-generation output.
- **One daemon per solution** — multiple solutions run independent daemons simultaneously.

## Additional Resources

For complete output schemas and all option details, consult:
- **`references/commands.md`** — Full command reference with output schemas and all flags
- **`references/patterns.md`** — Common agent workflow patterns and decision trees
