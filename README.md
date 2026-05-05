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

### Find callers of a method (call hierarchy)

```bash
dotnet aicraft callers --solution App.sln --symbol "MyApp.Services.OrderService.ProcessOrder"

# Or by location:
dotnet aicraft callers --solution App.sln --file Services/OrderService.cs --line 42 --col 18
```

### Search symbols by pattern

```bash
dotnet aicraft symbols --solution App.sln --pattern "Process*" --kind method
dotnet aicraft symbols --solution App.sln --pattern "*Repository" --kind type
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
- **Resets idle deadline after each handled request completion**
- **Supports** multiple simultaneous solutions (one daemon per solution)

Idle timeout is session-scoped only. If the daemon stops (manual stop or idle shutdown),
the next session starts with the default timeout unless you pass `--idle-timeout` again.

If timeout validation fails, the command returns a JSON error and timeout state is not changed.

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
| `dotnet aicraft rename` | Safe rename across solution (with `--dry-run`) |
| `dotnet aicraft impls` | Implementations of interface/abstract member |
| `dotnet aicraft callers` | All callers of a method |
| `dotnet aicraft symbols` | Search symbols by name pattern |
| `dotnet aicraft server status` | Daemon status |
| `dotnet aicraft server reload` | Reload solution |
| `dotnet aicraft server stop` | Stop daemon |

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
