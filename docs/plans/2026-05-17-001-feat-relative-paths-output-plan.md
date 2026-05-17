---
title: "feat: Emit file paths relative to the solution directory in command output"
status: completed
type: feat
date: 2026-05-17
owner: Artur
origin: docs/brainstorms/2026-05-17-relative-paths-output-requirements.md
---

# feat: Relative file paths in command output

## Summary

`dotnet aicraft` currently emits absolute file paths in every result row of both `text` and `json` formats. On the `dotnet-aicraft` repo alone the repeated 30-char solution prefix accounts for 16-17% of stdout chars on `refs`, `symbols`, and `diagnostics`; deeper enterprise paths push that to 25-35%. This plan introduces a single `PathFormatter` (in the daemon) that materializes every `File` field as a path **relative to the directory of the loaded `.sln`** with forward-slash separators, and a single client-side wrapper that surfaces the absolute `solutionRoot` exactly once per response (text header line for `--format text`, top-level field for `--format json`). The scope covers seven commands: `refs`, `impls`, `callers`, `symbols`, `diagnostics`, `definition`, `unused`. `rename` passes through with cosmetically-relative paths (no separate header) (see origin: `docs/brainstorms/2026-05-17-relative-paths-output-requirements.md`).

---

## Problem Frame

LLM consumers re-read the solution root on every result row even though it is invariant for the lifetime of the request. The information is redundant and token-expensive. The daemon already knows the solution; the client already knows the solution. The only reason rows carry absolute paths is that `Location.GetLineSpan().Path` from Roslyn returns absolute, and no layer rewrites it. Centralizing the rewrite once (at the daemon's result-construction sites) and surfacing the root once per response gives consumers exactly the same information density with materially fewer tokens.

---

## Requirements

Carried forward from origin doc:

- **R1** — Every `File` field returned by the seven in-scope commands is a path relative to the directory of the loaded `.sln`, using `/` as separator on all platforms.
- **R2** — Each response surfaces the absolute solution root exactly once: in `text` format as the first line `SolutionRoot: <abs path>`, in `json` format as a top-level `solutionRoot` field.
- **R3** — Line and column formatting is unchanged: `:line:col` 1-based, no padding.
- **R4** — In-scope commands: `refs`, `impls`, `callers`, `symbols`, `diagnostics`, `definition`, `unused`. `rename` paths pass through the same relativization (no separate `SolutionRoot:` header — its existing summary line is preserved).
- **R5** — No `--paths=absolute|relative` flag in v1. The change is treated as a format improvement; a flag is added only if a real consumer surfaces the need.
- **R6** — Existing snapshot/parity tests are updated to reflect the relative path format and the new header/field; the suite stays green.
- **R7** — Re-measuring `symbols --pattern Server*`, `refs ServerCommand`, and `diagnostics` on `dotnet-aicraft` shows ≥15% char-count reduction versus the pre-change baseline.

---

## Key Technical Decisions

### KTD1 — Relativize on the daemon side, not the client

`Location.GetLineSpan().Path` is materialized inside the daemon at a small, enumerable set of sites (`GetFileLineCol()` in `src/DotnetAICraft/Roslyn/RoslynExtensions.cs`, plus `MapDiagnostic` and `SymbolToResult` in `src/DotnetAICraft/Daemon/DaemonServer.cs`). Doing the rewrite once at construction time means:
- The wire payload itself shrinks (smaller socket frames, faster JSON parse on the client).
- Both `text` and `json` output consume already-relative paths with no per-renderer logic.
- Tests for daemon-produced records (`ReferenceResult`, `SymbolResult`, ...) assert against relative paths directly, which is what callers actually see.

The client is responsible only for adding the `solutionRoot` envelope.

### KTD2 — `PathFormatter` as a stateless static helper

Add `src/DotnetAICraft/Roslyn/PathFormatter.cs`:
- `string ToRelative(string absolutePath, string solutionDir)` — returns `Path.GetRelativePath(solutionDir, absolutePath).Replace('\\', '/')`.
- Falls back to the input path **unchanged** when `absolutePath` is null/empty, when `Path.GetRelativePath` throws (e.g. paths on a different volume), or when the relative path would still start with `..` (out-of-tree files, e.g. generated under `obj/` referenced from elsewhere). Out-of-tree paths stay absolute with `\` → `/` normalization for cross-platform consistency.

The helper is stateless; the solution directory is computed once per request from `Solution.FilePath` and threaded into the existing `GetFileLineCol` callers via a sibling overload `GetFileLineColRelative(Location, string solutionDir)`. The original `GetFileLineCol` is retained for any callers that genuinely need absolute (e.g. `DaemonStatus.SolutionPath` itself, which is the root, not a row).

### KTD3 — JSON output envelope shape

The brainstorm specifies `solutionRoot` "na poziomie root obiektu odpowiedzi". Current JSON output writes raw `result` which is sometimes an array (e.g. `refs`), sometimes an object (e.g. `symbols { items, hasMore }`). To accommodate both uniformly **without** breaking the array shape consumers may already rely on for list commands, wrap **only when needed**:

- For commands whose `result` is an object: inject `solutionRoot` as the first property in a new wrapper object `{ solutionRoot, ...result }`.
- For commands whose `result` is an array: wrap as `{ solutionRoot, items: <array> }` and document the rename of the top-level array to `items` in `CHANGELOG.md`.

**Assumption** (carried as planning-time decision because origin Open Question #1 left it open): `solutionRoot` is **absolute, OS-native** (e.g. `C:\code\others\dotnet-aicraft` on Windows, `/Users/x/repo` on Unix). Forward-slash normalization applies only to relative row paths, not to the root. Rationale: a consumer that wants to re-resolve a row joins root + relative; `Path.Combine(root, "Foo/Bar.cs")` works on both OSes regardless of separator inside the relative segment.

### KTD4 — Text output envelope shape

Prepend a single header line `SolutionRoot: <abs solution dir>` before the existing first count line. The blank-line separator between header and rows is preserved. Empty-result responses still emit the `SolutionRoot:` line followed by the count line and no rows.

`rename` is included in path relativization (R4) but does **not** get a `SolutionRoot:` header line — its existing summary line is the header. JSON output for `rename` does get the `solutionRoot` field per KTD3.

### KTD5 — `diagnostics` results without a file location

`MapDiagnostic` in `src/DotnetAICraft/Daemon/DaemonServer.cs` can produce a `DiagnosticResult` with null `File` (project-level diagnostics). Relativization only applies when `File` is non-null. The text renderer's existing branch (`if d.File is not null and d.Line is not null and d.Col is not null`) is unchanged.

---

## Output Structure

No new directories. Files touched:

```
src/DotnetAICraft/
├── Roslyn/
│   ├── PathFormatter.cs            (NEW)
│   └── RoslynExtensions.cs         (add GetFileLineColRelative overload)
├── Daemon/
│   └── DaemonServer.cs             (thread solutionDir into result construction sites)
├── Commands/
│   ├── Refs/Entry.cs               (wrap JSON, add text header)
│   ├── Impls/Entry.cs              (same)
│   ├── Callers/Entry.cs            (same)
│   ├── Symbols/Entry.cs            (same)
│   ├── Diagnostics/Entry.cs        (same)
│   ├── Definition/Entry.cs         (same)
│   ├── Unused/Entry.cs             (same)
│   ├── Rename/Entry.cs             (JSON wrapper only)
│   └── Shared/CommandHelpers.cs    (new helpers WriteResult/WriteSolutionRootHeader)
└── Output/
    ├── TextOutput.cs               (WriteSolutionRootHeader; per-renderer entry calls remain)
    └── JsonOutput.cs               (Wrap helper)
tests/DotnetAICraft.Tests/
├── Output/TextOutputListTests.cs           (update expected lines)
├── Output/TextOutputDefinitionTests.cs     (update)
├── Output/TextOutputDiagnosticsTests.cs    (update)
├── Output/TextOutputRenameTests.cs         (update — paths only, no extra header)
├── Output/JsonFormatParityTests.cs         (update for envelope)
├── Commands/RefsCommandTests.cs            (update assertions)
├── Commands/(others...)                    (update assertions)
└── Roslyn/PathFormatterTests.cs            (NEW)
```

---

## Implementation Units

### U1. Introduce `PathFormatter` with unit tests

**Goal:** Establish the single relativization primitive used by every downstream unit.
**Requirements:** R1
**Dependencies:** none
**Files:**
- `src/DotnetAICraft/Roslyn/PathFormatter.cs` (new)
- `tests/DotnetAICraft.Tests/Roslyn/PathFormatterTests.cs` (new)
**Approach:**
- Single static class `DotnetAICraft.Roslyn.PathFormatter` with one public method `ToRelative(string? absolutePath, string solutionDir)`.
- Internally uses `System.IO.Path.GetRelativePath` then `.Replace('\\', '/')`.
- Fallback rules per KTD2: null/empty input → returns input as-is (or empty); out-of-tree (`..` prefix on result) → returns absolute path with `\` → `/` normalization; `Path.GetRelativePath` throws (different volume) → same fallback.
- `solutionDir` is computed by caller via `Path.GetDirectoryName(Solution.FilePath)`; helper itself does no I/O and does not assume the directory exists.
**Patterns to follow:** other small static helpers in `src/DotnetAICraft/Roslyn/RoslynExtensions.cs`.
**Test scenarios:**
- File inside solution dir, Windows-style absolute → returns `Foo/Bar.cs` (forward slashes).
- File inside solution dir, Unix-style absolute → returns `Foo/Bar.cs`.
- Same-dir file → returns `File.cs` (no leading `./`).
- File on a different volume (`D:\other\X.cs` against `C:\repo`) → returns absolute path with `\` → `/` normalization.
- Out-of-tree file (`C:\repo\..\sibling\X.cs` resolved) → returns absolute fallback, forward-slash-normalized.
- Null input → returns null.
- Empty input → returns empty string.
- Edge: solutionDir with trailing separator, absolutePath with mixed separators → still returns clean forward-slash relative.
**Verification:** new test class passes; no other behavior changes yet.

---

### U2. Thread `solutionDir` through daemon result construction

**Goal:** Every `File` field in records returned by the seven in-scope commands (plus `rename`) is relative-with-forward-slashes when constructed.
**Requirements:** R1, R3, R4
**Dependencies:** U1
**Files:**
- `src/DotnetAICraft/Roslyn/RoslynExtensions.cs` — add `GetFileLineColRelative(this Location, string solutionDir)` overload that calls `PathFormatter.ToRelative` on the result of `GetFileLineCol`.
- `src/DotnetAICraft/Daemon/DaemonServer.cs` — at every result construction site, compute `solutionDir` once per handler (from `solution.FilePath`) and pass to the new overload. Sites identified:
  - `HandleRefsAsync` (line ~344-353)
  - `CollectCallersAsync` (line ~1035-1050)
  - `CreateCallGraphNode` / call graph construction (line ~1209)
  - `SymbolToResult` (line ~1910-1923) — add `solutionDir` parameter, update both call sites in `symbols` and `unused` handlers
  - `MapDiagnostic` (line ~1596-1624) — add `solutionDir` parameter
  - `HandleDefinitionAsync` / `ResolveDefinitionAsync` (`DefinitionResult.File`)
  - `HandleRenameAsync` / wherever `RenameChange.File` is set
  - `HandleUnusedAsync` (`UnusedCandidateResult` line ~1513)
**Approach:**
- `Solution.FilePath` is non-null for any loaded solution (validated at daemon load). `solutionDir` = `Path.GetDirectoryName(solution.FilePath)!`.
- The original absolute `GetFileLineCol` is preserved (no callers removed) — only the daemon result-construction call sites switch to the relative overload.
- `DaemonStatus.SolutionPath` is left absolute (it IS the root, not a row).
**Patterns to follow:** the existing `GetFileLineCol` extension call style.
**Test scenarios:**
- Daemon integration test (existing `RefsCommandTests`-style): given a known fixture solution, `refs` result rows have `File` values that do NOT start with the absolute solution dir and DO use forward slashes.
- Same shape test for `impls`, `callers`, `symbols`, `unused`, `diagnostics`, `definition`, `rename`.
- `diagnostics` row with `File == null` (project-level) remains null (no relativization attempted).
- File outside solution tree (e.g. generator-emitted under `obj/`): falls back per `PathFormatter` rules; daemon does not throw.
**Verification:** daemon-produced records carry relative paths; existing assertions that compared absolute paths fail (expected) — those are rewritten in U4.

---

### U3. Surface `solutionRoot` in client output (text header + JSON envelope)

**Goal:** Every response for in-scope commands carries the absolute solution root exactly once.
**Requirements:** R2, R4
**Dependencies:** U2
**Files:**
- `src/DotnetAICraft/Output/TextOutput.cs` — add `WriteSolutionRootHeader(string absoluteSolutionDir)` writing `SolutionRoot: <path>` plus a blank line, intended to be called immediately before the existing per-command `WriteXxx` method. Header is NOT added inside the existing `WriteXxx` methods so renderer-level tests stay focused on row formatting.
- `src/DotnetAICraft/Output/JsonOutput.cs` — add `WriteWithSolutionRoot(string solutionRoot, object? data)`:
  - If `data` is an `IEnumerable` (or its JSON representation is an array): emit `{ solutionRoot, items: <data> }`.
  - Otherwise: serialize `data` to a `JsonObject`, prepend `solutionRoot` as the first property, write.
- `src/DotnetAICraft/Commands/Shared/CommandHelpers.cs` — add `WriteResult(DaemonResponse res, OutputFormat format, string solutionAbsolutePath, Action<object?> textWriter)`:
  - Computes `solutionDir = Path.GetDirectoryName(solutionAbsolutePath)!`.
  - For `OutputFormat.Json`: calls `JsonOutput.WriteWithSolutionRoot(solutionDir, res.Result)`.
  - For `OutputFormat.Text`: calls `TextOutput.WriteSolutionRootHeader(solutionDir)` then invokes `textWriter(res.Result)` which each command's `Entry` implements as a thin lambda calling the existing `TextOutput.WriteXxx`.
- Update each `src/DotnetAICraft/Commands/<Cmd>/Entry.cs` (Refs, Impls, Callers, Symbols, Diagnostics, Definition, Unused, Rename) to route through `CommandHelpers.WriteResult` instead of calling `JsonOutput.Write` / `TextOutput.WriteXxx` directly. `Rename` skips the text header per KTD4 — pass a `writeHeader: false` overload or call `JsonOutput.WriteWithSolutionRoot` directly + existing text path.
**Approach:**
- The `solutionAbsolutePath` is the value already held by each Entry (`solutionPath` parameter).
- `solutionDir` for the envelope IS absolute, OS-native (per KTD3) — no forward-slash normalization on the root.
- Error responses (Status != Ok) bypass `WriteResult` and continue to use `CommandHelpers.WriteError` — no `solutionRoot` on error output.
**Patterns to follow:** the existing `Entry.ExecuteAsync` structure in `src/DotnetAICraft/Commands/Refs/Entry.cs`.
**Test scenarios:**
- Refs `--format text` on a fixture: first stdout line is `SolutionRoot: <abs solution dir>`, second line is blank, third is the existing count header.
- Refs `--format json`: top-level object has `solutionRoot` as first property and `items` as the array previously emitted at root.
- Symbols `--format json`: top-level has `solutionRoot`, `items`, `hasMore` (object-result case — no `items` rename).
- Diagnostics `--format text` with zero results: header line + blank + count line, no rows.
- Rename `--format text`: NO `SolutionRoot:` header line; first line is the existing rename summary.
- Rename `--format json`: top-level has `solutionRoot` plus existing `RenameResult` fields.
- Error response (e.g. symbol not found) on either format: no `solutionRoot` injected; existing error shape preserved.
**Verification:** manually running `dotnet aicraft refs --symbol ServerCommand` on this repo shows the header line and forward-slash relative paths; `--format json` includes `solutionRoot` at top level.

---

### U4. Update existing test assertions

**Goal:** All previously-green tests stay green against the new format.
**Requirements:** R6
**Dependencies:** U3
**Files:**
- `tests/DotnetAICraft.Tests/Output/TextOutputListTests.cs`
- `tests/DotnetAICraft.Tests/Output/TextOutputDefinitionTests.cs`
- `tests/DotnetAICraft.Tests/Output/TextOutputDiagnosticsTests.cs`
- `tests/DotnetAICraft.Tests/Output/TextOutputRenameTests.cs`
- `tests/DotnetAICraft.Tests/Output/JsonFormatParityTests.cs`
- `tests/DotnetAICraft.Tests/Commands/RefsCommandTests.cs`
- `tests/DotnetAICraft.Tests/Commands/ImplsCommandTests.cs`
- `tests/DotnetAICraft.Tests/Commands/CallersCommandTests.cs`
- `tests/DotnetAICraft.Tests/Commands/SymbolsCommandTests.cs`
- `tests/DotnetAICraft.Tests/Commands/DiagnosticsCommandTests.cs`
- `tests/DotnetAICraft.Tests/Commands/DefinitionCommandTests.cs`
- `tests/DotnetAICraft.Tests/Commands/UnusedCommandTests.cs`
- `tests/DotnetAICraft.Tests/Commands/RenameCommandTests.cs`
**Approach:**
- `TextOutput*` tests that previously called `TextOutput.WriteRefs(items, target, solution)` directly: leave call-site unchanged (these are pure-renderer tests). Path inputs in the test fixtures may stay as already-relative fixtures (`"/a/File1.cs"`) since `TextOutput.WriteRefs` is path-agnostic.
- Tests that capture full stdout of an end-to-end command run: update expected output to include the new `SolutionRoot:` header (text) or the wrapping `{ solutionRoot, ... }` envelope (json).
- `JsonFormatParityTests`: this test asserts the JSON byte-for-byte parity with the prior schema — it MUST be updated to match the new envelope; preserve the existing keys inside the envelope.
**Patterns to follow:** existing assertions in each file.
**Test scenarios:** the suite itself is the verification — no new scenarios beyond keeping existing ones green.
**Verification:** `dotnet test` from repo root passes locally.

---

### U5. Documentation and measurement

**Goal:** README/CHANGELOG note the format change; the success metric is captured.
**Requirements:** R7
**Dependencies:** U3
**Files:**
- `CHANGELOG.md` — note the breaking text/json format change (added `SolutionRoot:` header, JSON envelope with `solutionRoot` + array rename to `items` for list commands).
- `README.md` — if there is a command output example, update one to show the new shape.
**Approach:** brief CHANGELOG entry under a new "Unreleased" or current-version heading; one paragraph that calls out the rename of the top-level array to `items` in list-command JSON.
**Test expectation:** none — pure docs.
**Verification:** re-run the brainstorm's measurement on `symbols --pattern Server*`, `refs ServerCommand`, `diagnostics`; record char counts in CHANGELOG and confirm ≥15% reduction vs. pre-change baseline (R7).

---

## Scope Boundaries

In scope:
- Path relativization for the seven commands in R4 plus `rename` pass-through.
- `solutionRoot` envelope for both formats.

Out of scope (carried verbatim from origin):
- Changing the format of line/column numbers.
- A `--paths=absolute|relative` opt-out flag.
- Changing `rename` summary line format beyond the cosmetic pass-through of new relative paths.

### Deferred to Follow-Up Work
- Versioning of the JSON schema as a top-level field. If JSON consumers grow to depend on the envelope shape, a `version` or `schemaVersion` sibling to `solutionRoot` can be added later. Origin open question #3 is answered "no schema version field in v1".
- Migration of internal scripts/tests that parse output. Origin open question #2: a repo-wide scan inside this plan (Grep for `C:\` or absolute path patterns in `scripts/` and `docs/`) found no shipped scripts that parse `dotnet aicraft` text output beyond the test suite, which U4 already updates. If any external/private scripts exist, they migrate on next use.

---

## Risks

- **Test fan-out underestimate.** The seven command-level test files plus five output-level test files all carry path-shape expectations. U4 must be done in the same PR as U2/U3 or the suite stays red. Mitigation: implement U2-U4 together; do not merge intermediate states.
- **`Path.GetRelativePath` cross-volume on Windows.** When a Roslyn-loaded source lives on a different drive than the `.sln` (rare but real — symlinked NuGet caches, source-link), `GetRelativePath` returns the absolute path back. PathFormatter's fallback (KTD2) handles this without throwing; tests cover it (U1).
- **PowerShell consumers parsing the header.** The `SolutionRoot:` line is a `key: value` text shape easy to grep; no regression risk identified for the existing PowerShell-friendly text format.

---

## Verification Strategy

- `dotnet test` from repo root passes.
- Manual smoke on `dotnet-aicraft` repo: `dotnet aicraft refs --symbol ServerCommand --format text` and `--format json` both show the new header/envelope and forward-slash relative paths.
- Re-measure the three brainstorm samples (`symbols --pattern Server*`, `refs ServerCommand`, `diagnostics`) and confirm char-count reduction ≥15% (R7). Capture the numbers in CHANGELOG.
