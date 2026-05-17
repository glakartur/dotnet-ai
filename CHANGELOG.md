# Changelog

## [Unreleased]

### Changed
- **Breaking (output format):** File paths in command results are now emitted
  relative to the solution directory with forward-slash separators on all
  platforms. The absolute solution root is surfaced once per response: as a
  `SolutionRoot: <abs path>` header line in `--format text`, and as a top-level
  `solutionRoot` field in `--format json`. Affects `refs`, `impls`, `callers`,
  `symbols`, `diagnostics`, `definition`, `unused`, and `rename`. For commands
  whose JSON result was previously a top-level array (`refs`, `impls`,
  `diagnostics`), the array is now nested under `items` inside the envelope
  object. `rename` text output omits the `SolutionRoot:` header — its existing
  summary line stays as-is. Out-of-tree paths (different volume, generator
  output outside the solution tree) fall back to their absolute form with
  forward-slash normalization.
- `dotnet aicraft server start` is now a fast, idempotent ensure-running call.
  It no longer runs the daemon foreground process: instead it attaches to a
  running daemon (extending the idle deadline) or spawns one in the background
  and returns once it is ready. With `--idle-timeout` it applies the new
  value identically to `server reload --idle-timeout`. External scripts that
  relied on `server start` blocking until shutdown should switch to a
  long-lived monitoring loop or use the daemon's idle-timeout settings.
- Daemon auto-spawn (from query commands and `server start`) now invokes an
  internal hidden `server daemon` subcommand instead of `server start`. The
  `daemon` subcommand is intentionally undocumented and is for internal use
  only.

## [0.1.0] — unreleased

### Added
- `dotnet aicraft refs` — find all references to a symbol (by location or fully-qualified name)
- `dotnet aicraft rename` — safe cross-solution rename with `--dry-run` support
- `dotnet aicraft impls` — find implementations of interfaces and abstract members
- `dotnet aicraft callers` — call hierarchy (all callers of a method)
- `dotnet aicraft symbols` — search symbols by glob pattern
- `dotnet aicraft server start/stop/status/reload` — daemon lifecycle management
- Background daemon with Unix socket transport — solution loaded once, ~50ms responses
- Incremental file watching — `.cs` changes applied without full solution reload
- JSON output on stdout, logs on stderr — clean for agent consumption
- Cross-platform: Linux, macOS, Windows
- Session-scoped daemon idle auto-shutdown (default 60m) with `--idle-timeout` override (`off` or positive duration with `m|h`, e.g. `5m`, `1h`)
- Strict idle-timeout validation with non-mutating error behavior on invalid input
