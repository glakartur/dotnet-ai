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
version: 0.4.0
---

# dotnet-aicraft

Semantic .NET code analysis via Roslyn — compiler-level precision, not text search. A background daemon loads the solution once; subsequent commands respond in ~50ms. Auto-starts on first use, shuts down after 60 min idle.

All commands output **JSON to stdout**. Daemon startup messages go to **stderr**.

## When to Use Proactively

| Situation | Command |
|---|---|
| About to modify a method or property | `refs` — know all call sites first |
| About to delete a class or member | `refs` + `callers` + `unused` — confirm impact |
| About to rename anything | `rename --dry-run` then `rename` |
| Need declaration from usage location | `definition --file --line --col` |
| Implementing a feature touching an interface | `impls` — discover all implementors |
| Exploring an unfamiliar codebase | `symbols --pattern "*"` + `impls` on key interfaces |
| Need compiler signal before edits | `diagnostics --severity error` |
| Need call hierarchy | `callers --direction incoming\|outgoing\|both --depth N` |
| Partial symbol name known | `symbols --pattern "Partial*"` |
| Projects added/removed | `server reload` |

## Shared Options

```bash
--solution / -s   # Path to .sln or .csproj (required)
--idle-timeout    # "off" or duration like "5m", "1h" (default 60m, session-scoped)
--debug           # Verbose debug logging to stderr (also: DOTNET_AICRAFT_DEBUG=1)
```

## Identifying Symbols

```bash
# By file location (preferred when reading source)
--file path/to/File.cs --line 42 --col 18

# By fully-qualified name (preferred when name is known)
--symbol "MyApp.Services.OrderService.ProcessOrder"
```

## Commands

### refs — Find All References

```bash
dotnet aicraft refs -s App.sln --file src/Services/OrderService.cs --line 42 --col 18
dotnet aicraft refs -s App.sln --symbol "MyApp.Services.OrderService.ProcessOrder"
```

Output: `[{ file, line, col, context }]`

### rename — Safe Rename

Always dry-run first:

```bash
dotnet aicraft rename -s App.sln --symbol "MyApp.Services.OrderService.ProcessOrder" --to "HandleOrder" --dry-run
dotnet aicraft rename -s App.sln --symbol "MyApp.Services.OrderService.ProcessOrder" --to "HandleOrder"
```

Output: `{ symbol, newName, applied, dryRun, changes[] }`

### definition — Find Declaration

```bash
dotnet aicraft definition -s App.sln --file Services/OrderService.cs --line 42 --col 18
dotnet aicraft definition -s App.sln --symbol "MyApp.Services.OrderService.ProcessOrder"
```

Output: `DefinitionResult` — if metadata-only, `file/line/col` will be null. Use `fullName` for follow-up commands.

### impls — Find Implementations

```bash
dotnet aicraft impls -s App.sln --symbol "MyApp.Interfaces.IOrderProcessor"
```

Output: JSON array of implementing types with file locations.

### callers — Call Graph

```bash
# Default: incoming callers, depth=1
dotnet aicraft callers -s App.sln --symbol "MyApp.Services.OrderService.ProcessOrder"

# Full graph
dotnet aicraft callers -s App.sln --symbol "MyApp.Services.OrderService.ProcessOrder" --direction both --depth 2
```

Output:
- `incoming + depth=1`: `[{ callerSymbol, isDirect, file, line, col, context }]`
- otherwise: `CallGraphResult { rootId, direction, depth, nodes[], edges[] }`

### diagnostics — Roslyn Diagnostics

```bash
dotnet aicraft diagnostics -s App.sln --severity error
dotnet aicraft diagnostics -s App.sln --project MyApp.Core
dotnet aicraft diagnostics -s App.sln --file src/Services/OrderService.cs
```

Output: sorted `[{ project, id, severity, message, file, line, col }]`

### unused — Dead-Code Candidates

```bash
dotnet aicraft unused -s App.sln --kind method
dotnet aicraft unused -s App.sln --project MyApp.Core --public-only
dotnet aicraft unused -s App.sln --kind class --include-generated
```

Output: `UnusedScanSummary { scanned, items[{ symbol, kind, reason, confidence }] }`

### symbols — Pattern Search

```bash
dotnet aicraft symbols -s App.sln --pattern "Process*"
dotnet aicraft symbols -s App.sln --pattern "Process*" --kind method
dotnet aicraft symbols -s App.sln --pattern "*" --kind class --limit 100 --offset 200
```

Kinds: `class|interface|method|property|field|event|...`
Output: `SymbolsResultPage { items[], hasMore }`

### server — Daemon Management

```bash
dotnet aicraft server status -s App.sln         # daemon status + solution stats
dotnet aicraft server reload -s App.sln         # reload after project structure changes
dotnet aicraft server stop   -s App.sln         # stop the daemon
dotnet aicraft server start  -s App.sln --idle-timeout 30m   # start with custom timeout
dotnet aicraft server start  -s App.sln --idle-timeout off   # disable idle auto-shutdown
```

## Agent Workflows

### Refactoring a symbol
1. Find FQN via `symbols` or source reading.
2. `rename --dry-run` → inspect `changes[]`.
3. `rename` to apply.

### Investigating before deletion
1. `refs` → all usages.
2. `callers --direction incoming --depth 1` → immediate callers.
3. `unused` on same kind/project → dead-code confidence.

### Exploring an interface
1. `impls` → all implementing types.
2. `definition` on each implementor for follow-up.

### Declaration from cursor
1. `definition --file --line --col`.
2. Use returned `fullName` for `refs`, `callers`, `rename`.

### Diagnostics triage before refactor
1. `diagnostics --severity error` → fix blockers first.
2. Narrow by `--project` or `--file` as needed.

## Reference Files

- `references/commands.md` — expanded examples and output samples
- `references/patterns.md` — workflow patterns and decision trees

`dotnet aicraft <command> --help` is the source of truth for current flags.
