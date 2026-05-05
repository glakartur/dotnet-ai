# Changelog

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
