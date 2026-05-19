# Contributing to dotnet-aicraft

## Building from source

```bash
git clone https://github.com/glakartur/dotnet-aicraft
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
- Resets idle deadline after each handled request completion.
- Supports multiple simultaneous solutions (one daemon per solution).

Idle timeout is session-scoped only. If the daemon stops (manual stop or idle shutdown),
the next session starts with the default timeout unless you pass `--idle-timeout` again.

If timeout validation fails, the command returns a JSON error and timeout state is not changed.

### Windows stale socket self-heal policy

On Windows, daemon startup applies a bounded stale-artifact policy before bind:

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

### Daemon first-run output

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

For slice conventions and further architecture notes, see [`docs/architecture/`](docs/architecture/).
