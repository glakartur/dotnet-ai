# dotnet-aicraft — Agent Workflow Patterns

## Decision Tree: Which Command to Use

```
Need to find something?
├── Know the symbol name? → refs or callers with --symbol
├── Reading a file and have line/col? → refs or callers with --file --line --col
├── Partial name only? → symbols --pattern "Partial*"
└── Looking for interface implementors? → impls --symbol "Namespace.IInterface"

Need to change something?
└── Rename → rename --dry-run first, then rename to apply
```

---

## Pattern 1: Understand Impact Before Refactoring

Before removing or modifying a symbol, assess how many places depend on it.

```bash
# Step 1: Find all usages
dotnet aicraft refs --solution App.sln \
  --symbol "MyApp.Services.OrderService.ProcessOrder"

# Step 2: Find all callers
dotnet aicraft callers --solution App.sln \
  --symbol "MyApp.Services.OrderService.ProcessOrder"

# Step 3: Find implementations if it's an interface member
dotnet aicraft impls --solution App.sln \
  --symbol "MyApp.Interfaces.IOrderProcessor.ProcessOrder"
```

Decide based on the full evidence set — not grep output.

---

## Pattern 2: Safe Symbol Rename

```bash
# Step 1: Discover the exact fully-qualified name
dotnet aicraft symbols --solution App.sln --pattern "ProcessOrder" --kind member

# Step 2: Dry-run the rename
dotnet aicraft rename --solution App.sln \
  --symbol "MyApp.Services.OrderService.ProcessOrder" \
  --to "HandleOrder" --dry-run

# Step 3: Inspect changes[] in the output
# Verify the file list and context snippets look correct

# Step 4: Apply
dotnet aicraft rename --solution App.sln \
  --symbol "MyApp.Services.OrderService.ProcessOrder" \
  --to "HandleOrder"
```

Never skip the dry-run. It costs nothing and prevents surprises.

---

## Pattern 3: Explore an Unknown Codebase

When starting work on an unfamiliar solution:

```bash
# Find all types in a namespace
dotnet aicraft symbols --solution App.sln --pattern "MyApp.Services.*" --kind type

# Find all methods matching a domain concept
dotnet aicraft symbols --solution App.sln --pattern "*Order*" --kind member

# Discover who implements a central interface
dotnet aicraft impls --solution App.sln \
  --symbol "MyApp.Core.IRepository"

# Trace how a key method is used
dotnet aicraft callers --solution App.sln \
  --symbol "MyApp.Core.IRepository.GetById"
```

---

## Pattern 4: Navigate from File + Line to Symbol

When reading source code and the cursor is at a specific location:

```bash
# At line 42, col 18 in OrderService.cs — what is this?
dotnet aicraft refs --solution App.sln \
  --file src/Services/OrderService.cs --line 42 --col 18

# Follow the call hierarchy upward
dotnet aicraft callers --solution App.sln \
  --file src/Services/OrderService.cs --line 42 --col 18
```

Column numbers in editors are usually 1-based. The `context` field in results confirms which symbol was resolved.

---

## Pattern 5: Daemon Lifecycle Management

### Normal workflow (automatic)

The daemon starts automatically on first use. No explicit management needed unless:
- The solution structure changed (projects added/removed)
- The daemon is using excessive memory
- Debugging connectivity issues

### After adding/removing projects

```bash
dotnet aicraft server reload --solution App.sln
```

### Diagnosing slow responses

```bash
dotnet aicraft server status --solution App.sln
```

Check `running: true` and `projects` / `documents` counts. If not running, the next command will restart it.

### Long-running agent session

Prevent idle shutdown during an extended session:

```bash
dotnet aicraft server start --solution App.sln --idle-timeout off
```

Or extend the timeout:

```bash
dotnet aicraft server start --solution App.sln --idle-timeout 4h
```

---

## Pattern 6: Parallel Multi-Solution Analysis

Each solution runs its own daemon. Run commands concurrently across solutions:

```bash
dotnet aicraft refs --solution Backend.sln --symbol "MyApp.Shared.Events.OrderCreated" &
dotnet aicraft refs --solution Frontend.sln --symbol "MyApp.Shared.Events.OrderCreated" &
wait
```

---

## Pattern 7: Parsing JSON Output Programmatically

All commands output clean JSON to stdout. Daemon logs go only to stderr.

```bash
# Capture JSON only
refs_output=$(dotnet aicraft refs --solution App.sln --symbol "MyApp.Foo" 2>/dev/null)

# Check for errors
if echo "$refs_output" | jq -e '.error' > /dev/null 2>&1; then
  echo "Error: $(echo $refs_output | jq -r '.error')"
fi
```

In scripts or agents processing the output, always check for the `error` field before iterating `results`.

---

## Common Mistakes to Avoid

| Mistake | Fix |
|---|---|
| Using text search (grep) for refactoring | Use `rename` — it handles all call sites atomically |
| Applying rename without dry-run | Always run `--dry-run` first |
| Passing only the method name to `--symbol` | Use the fully-qualified name: `Namespace.Class.Method` |
| Assuming the daemon is always running | Check with `server status` if responses seem slow |
| Using 0-based line/col numbers | `dotnet aicraft` uses **1-based** line and column numbers |
| Expecting stderr in JSON output | Daemon startup messages go to stderr; JSON is always clean on stdout |

---

## Output Field Reference Quick Lookup

### `refs` and `callers`
- `file` — absolute path to source file
- `line` — 1-based line number of the reference
- `col` — 1-based column number
- `context` — source line text at that location

### `rename`
- `symbol` — original fully-qualified name
- `newName` — the new identifier (not fully-qualified)
- `applied` — `true` if changes were written to disk
- `dryRun` — `true` if `--dry-run` was passed
- `changes[].oldText` / `changes[].newText` — text before and after rename

### `impls`
- `symbol` — fully-qualified name of the implementing type/member
- `file`, `line`, `col`, `context` — location of the implementation declaration

### `symbols`
- `name` — short identifier name
- `fullyQualifiedName` — fully-qualified name to use in subsequent commands
- `kind` — `Method`, `Class`, `Interface`, `Property`, `Field`, `Namespace`, etc.
- `file`, `line`, `col` — declaration location
